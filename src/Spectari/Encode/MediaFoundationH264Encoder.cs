using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Spectari.Encode;

/// <summary>Asynchronous Media Foundation H.264 encoder for pooled DXGI surfaces.</summary>
internal sealed class MediaFoundationH264Encoder : IHardwareVideoEncoder
{
    private const int MfEventFlagNoWait = 1;
    private static readonly Guid MftEnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    private readonly Queue<PendingInput> _pending = new();
    private readonly Queue<SubmittedInput> _submitted = new();
    private readonly AnnexBAccessUnitAssembler _annexB = new();

    private IMFActivate? _activation;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private IMFDXGIDeviceManager? _deviceManager;
    private ID3D11Device? _deviceReference;
    private string _friendlyName = "Media Foundation H.264";
    private int _inputCredits;
    private bool _mfStarted;
    private bool _initialized;
    private bool _sequenceHeaderLoaded;
    private bool _draining;
    private bool _disposed;
    private long _submittedFrameCount;
    private int _outputBufferBytes = 1024 * 1024;

    internal string FriendlyName => _friendlyName;
    public long SubmittedFrameCount => Interlocked.Read(ref _submittedFrameCount);

    public HardwareEncoderProbeResult Probe(
        HardwareEncoderProbeContext context,
        HardwareVideoEncoderParameters parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureMediaFoundationStarted();
            _activation?.Dispose();
            _activation = FindEncoderActivation(context.Adapter.Luid);
            if (_activation is null)
            {
                return HardwareEncoderProbeResult.Unavailable(
                    $"no hardware H.264 Media Foundation transform matched adapter {context.Adapter.Luid}");
            }

            try { _friendlyName = _activation.FriendlyName; }
            catch { _friendlyName = "Media Foundation H.264"; }
            return HardwareEncoderProbeResult.AvailableNow();
        }
        catch (Exception ex)
        {
            return HardwareEncoderProbeResult.Unavailable(
                $"Media Foundation H.264 probe failed: {SingleLine(ex.Message)}");
        }
    }

    public void Initialize(
        HardwareEncoderInitialization initialization,
        HardwareVideoEncoderParameters parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Hardware encoder is already initialized.");
        if (_activation is null)
            throw new InvalidOperationException("Hardware encoder must be probed before initialization.");

        try
        {
            _transform = _activation.ActivateObject<IMFTransform>();
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();

            Marshal.AddRef(initialization.D3D11DevicePointer);
            _deviceReference = new ID3D11Device(initialization.D3D11DevicePointer);
            _deviceManager.ResetDevice(_deviceReference).CheckError();

            _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            _transform.ProcessMessage(
                TMessageType.MessageSetD3DManager,
                (UIntPtr)(nuint)_deviceManager.NativePointer);

            using (IMFMediaType outputType = CreateOutputType(parameters))
                _transform.SetOutputType(0, outputType, 0);
            ConfigureCodecApi(parameters);
            using (IMFMediaType inputType = CreateInputType(parameters))
                _transform.SetInputType(0, inputType, 0);

            _outputBufferBytes = checked((int)Math.Min(
                int.MaxValue,
                Math.Max(1024L * 1024L, parameters.MaximumBitrateKbps * 1000L / 8L)));

            LoadSequenceHeader();
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _initialized = true;
            PumpEvents([]);
        }
        catch
        {
            ReleaseAll(FrameLeaseReturnReason.Failure);
            ReleaseTransform();
            throw;
        }
    }

    public IReadOnlyList<EncodedAccessUnit> Encode(
        VideoFrameLease frame,
        long presentationTime100ns,
        long duration100ns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _transform is null)
        {
            frame.Return(FrameLeaseReturnReason.InputRejected);
            throw new InvalidOperationException("Hardware encoder is not initialized.");
        }

        var output = new List<EncodedAccessUnit>();
        _pending.Enqueue(new PendingInput(frame, presentationTime100ns, duration100ns));
        try
        {
            PumpEvents(output);
            SubmitInputs();
            PumpEvents(output);
            return output;
        }
        catch
        {
            ReleaseAll(FrameLeaseReturnReason.Failure);
            throw;
        }
    }

    public IReadOnlyList<EncodedAccessUnit> Drain()
    {
        if (!_initialized || _transform is null) return [];
        var output = new List<EncodedAccessUnit>();
        _draining = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            while (_draining && Stopwatch.GetTimestamp() < deadline)
            {
                int before = output.Count;
                PumpEvents(output);
                if (_draining && before == output.Count) Thread.Sleep(1);
            }
            if (_draining)
                Console.Error.WriteLine("[mf-encode] drain timed out after 2 seconds; flushing outstanding surfaces.");
            ReleaseAll(FrameLeaseReturnReason.Flush);
            return output;
        }
        catch
        {
            ReleaseAll(FrameLeaseReturnReason.Failure);
            throw;
        }
        finally
        {
            _draining = false;
        }
    }

    public void Flush()
    {
        if (_transform is not null)
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _inputCredits = 0;
        _draining = false;
        ReleaseAll(FrameLeaseReturnReason.Flush);
    }

    private void SubmitInputs()
    {
        while (_inputCredits > 0 && _pending.TryDequeue(out PendingInput pending))
        {
            VideoFrameLease frame = pending.Frame;
            ID3D11Texture2D texture = frame.NativeTexture
                ?? throw new InvalidOperationException("NV12 encoder lease lost its texture.");
            using IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                typeof(ID3D11Texture2D).GUID,
                texture,
                0,
                false);
            using IMFSample sample = MediaFactory.MFCreateSample();
            sample.SampleTime = pending.PresentationTime100ns;
            sample.SampleDuration = pending.Duration100ns;
            sample.AddBuffer(buffer);

            try
            {
                _transform!.ProcessInput(0, sample, 0);
                _inputCredits--;
                _submitted.Enqueue(new SubmittedInput(frame, pending.PresentationTime100ns));
                Interlocked.Increment(ref _submittedFrameCount);
            }
            catch
            {
                frame.Return(FrameLeaseReturnReason.InputRejected);
                throw;
            }
        }
    }

    private void PumpEvents(List<EncodedAccessUnit> output)
    {
        if (_events is null) return;
        while (TryGetEvent(out IMFMediaEvent? mediaEvent))
        {
            using (mediaEvent)
            {
                mediaEvent!.Status.CheckError();
                switch (mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _inputCredits++;
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        ReadOutput(output);
                        break;
                    case MediaEventTypes.TransformDrainComplete:
                        _draining = false;
                        break;
                }
            }
        }
    }

    private bool TryGetEvent(out IMFMediaEvent? mediaEvent)
    {
        try
        {
            mediaEvent = _events!.GetEvent(MfEventFlagNoWait);
            return true;
        }
        catch (SharpGenException ex) when (
            ex.ResultCode == Vortice.MediaFoundation.ResultCode.NoEventsAvailable)
        {
            mediaEvent = null;
            return false;
        }
    }

    private void ReadOutput(List<EncodedAccessUnit> output)
    {
        OutputStreamInfo info = _transform!.GetOutputStreamInfo(0);
        bool providesSamples = ((OutputStreamInfoFlags)info.Flags &
            (OutputStreamInfoFlags.OutputStreamProvidesSamples |
             OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        IMFSample? allocated = null;
        if (!providesSamples)
        {
            allocated = MediaFactory.MFCreateSample();
            using IMFMediaBuffer outputBuffer =
                MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _outputBufferBytes));
            allocated.AddBuffer(outputBuffer);
        }

        var data = new OutputDataBuffer { StreamID = 0, Sample = allocated! };
        Result result = _transform.ProcessOutput(
            ProcessOutputFlags.None,
            1,
            ref data,
            out _);
        try
        {
            if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return;
            result.CheckError();
            IMFSample sample = data.Sample ?? allocated
                ?? throw new InvalidOperationException("Media Foundation produced no output sample.");
            try
            {
                if (!_sequenceHeaderLoaded) LoadSequenceHeader();
                bool keyFrame = IsKeyFrame(sample);
                byte[] bytes = CopySampleBytes(sample);
                if (bytes.Length > 0)
                    output.Add(_annexB.Assemble(bytes, keyFrame));
                ReturnSubmittedLease(sample.SampleTime);
            }
            finally
            {
                if (!ReferenceEquals(sample, allocated)) sample.Dispose();
            }
        }
        finally
        {
            data.Events?.Dispose();
            allocated?.Dispose();
        }
    }

    private void ReturnSubmittedLease(long sampleTime)
    {
        if (_submitted.Count == 0) return;
        SubmittedInput selected = _submitted.Dequeue();
        if (selected.PresentationTime100ns != sampleTime)
        {
            Console.Error.WriteLine(
                $"[mf-encode] output timestamp {sampleTime} did not match oldest input {selected.PresentationTime100ns}; releasing in submission order.");
        }
        selected.Frame.Return(FrameLeaseReturnReason.OutputProduced);
    }

    private void LoadSequenceHeader()
    {
        using IMFMediaType current = _transform!.GetOutputCurrentType(0);
        try
        {
            byte[] header = current.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (header.Length == 0) return;
            _annexB.SetSequenceHeader(header);
            _sequenceHeaderLoaded = true;
        }
        catch (SharpGenException)
        {
            // Some hardware transforms publish SPS/PPS only after their first output.
        }
    }

    private static bool IsKeyFrame(IMFSample sample)
    {
        try { return sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; }
        catch (SharpGenException) { return false; }
    }

    private static byte[] CopySampleBytes(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out nint pointer, out _, out int currentLength);
        try
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(currentLength);
            if (currentLength > 0) Marshal.Copy(pointer, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static IMFMediaType CreateOutputType(HardwareVideoEncoderParameters parameters)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            type.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)parameters.BitrateKbps * 1000)).CheckError();
            MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize,
                (uint)parameters.Width, (uint)parameters.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate,
                (uint)parameters.FramesPerSecond, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            type.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError();
            type.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)parameters.GopFrames).CheckError();
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    private static IMFMediaType CreateInputType(HardwareVideoEncoderParameters parameters)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize,
                (uint)parameters.Width, (uint)parameters.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate,
                (uint)parameters.FramesPerSecond, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    private void ConfigureCodecApi(HardwareVideoEncoderParameters parameters)
    {
        nint pointer = _transform!.QueryInterfaceOrNull(typeof(ICodecApiNative).GUID);
        if (pointer == 0)
            throw new InvalidOperationException("The selected hardware H.264 transform has no ICodecAPI rate-control surface.");

        ICodecApiNative codecApi;
        try { codecApi = (ICodecApiNative)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
        try
        {
            SetCodecValue(codecApi, CodecApiGuids.RateControlMode, 0u, required: true);
            SetCodecValue(codecApi, CodecApiGuids.MeanBitRate,
                checked((uint)parameters.BitrateKbps * 1000), required: true);
            SetCodecValue(codecApi, CodecApiGuids.MaxBitRate,
                checked((uint)parameters.MaximumBitrateKbps * 1000), required: true);
            SetCodecValue(codecApi, CodecApiGuids.BufferSize,
                parameters.H264BufferSizeBytes, required: true);
            SetCodecValue(codecApi, CodecApiGuids.GopSize, (uint)parameters.GopFrames, required: true);
            SetCodecValue(codecApi, CodecApiGuids.BPictureCount, 0u, required: true);
            SetCodecValue(codecApi, CodecApiGuids.LowLatency, true, required: true);
            SetCodecValue(codecApi, CodecApiGuids.AdaptiveMode, 0u, required: true);
            SetCodecValue(codecApi, CodecApiGuids.Cabac, true, required: true);
        }
        finally
        {
            Marshal.ReleaseComObject(codecApi);
        }
    }

    private static void SetCodecValue(
        ICodecApiNative codecApi,
        Guid property,
        object value,
        bool required)
    {
        int supported = codecApi.IsSupported(ref property);
        if (supported < 0)
        {
            if (required) Marshal.ThrowExceptionForHR(supported);
            return;
        }
        int result = codecApi.SetValue(ref property, ref value);
        if (result < 0 && required) Marshal.ThrowExceptionForHR(result);
    }

    private IMFActivate? FindEncoderActivation(string adapterLuid)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
        attributes.SetBlob(MftEnumAdapterLuid, AdapterLuidBytes(adapterLuid)).CheckError();
        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12,
        };
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
        MediaFactory.MFTEnum2(
            TransformCategoryGuids.VideoEncoder,
            flags,
            input,
            output,
            attributes,
            out nint activationArray,
            out uint activationCount);

        IMFActivate? selected = null;
        try
        {
            for (uint index = 0; index < activationCount; index++)
            {
                nint activationPointer = Marshal.ReadIntPtr(
                    activationArray,
                    checked((int)index * nint.Size));
                var activation = new IMFActivate(activationPointer);
                if (selected is null) selected = activation;
                else activation.Dispose();
            }
            return selected;
        }
        finally
        {
            Marshal.FreeCoTaskMem(activationArray);
        }
    }

    private static byte[] AdapterLuidBytes(string luid)
    {
        string[] parts = luid.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int high) ||
            !uint.TryParse(parts[1], out uint low))
        {
            throw new FormatException($"Adapter LUID '{luid}' is invalid.");
        }
        byte[] bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), low);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), high);
        return bytes;
    }

    private void EnsureMediaFoundationStarted()
    {
        if (_mfStarted) return;
        MediaFactory.MFStartup(useLightVersion: false).CheckError();
        _mfStarted = true;
    }

    private void ReleaseAll(FrameLeaseReturnReason reason)
    {
        while (_pending.TryDequeue(out PendingInput pending)) pending.Frame.Return(reason);
        while (_submitted.TryDequeue(out SubmittedInput submitted)) submitted.Frame.Return(reason);
    }

    private void ReleaseTransform()
    {
        _initialized = false;
        _events?.Dispose();
        _events = null;
        _transform?.Dispose();
        _transform = null;
        _deviceManager?.Dispose();
        _deviceManager = null;
        _deviceReference?.Dispose();
        _deviceReference = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_transform is not null)
            {
                try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); }
                catch { }
                try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); }
                catch { }
            }
            ReleaseAll(FrameLeaseReturnReason.Teardown);
            ReleaseTransform();
            try { _activation?.ShutdownObject(); } catch { }
            _activation?.Dispose();
            _activation = null;
        }
        finally
        {
            if (_mfStarted)
            {
                MediaFactory.MFShutdown();
                _mfStarted = false;
            }
        }
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private readonly record struct PendingInput(
        VideoFrameLease Frame,
        long PresentationTime100ns,
        long Duration100ns);

    private readonly record struct SubmittedInput(
        VideoFrameLease Frame,
        long PresentationTime100ns);

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApiNative
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api,
            [MarshalAs(UnmanagedType.Struct)] out object minimum,
            [MarshalAs(UnmanagedType.Struct)] out object maximum,
            [MarshalAs(UnmanagedType.Struct)] out object step);
        [PreserveSig] int GetParameterValues(ref Guid api, out nint values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    private static class CodecApiGuids
    {
        internal static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        internal static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        internal static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
        internal static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
        internal static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        internal static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
        internal static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        internal static readonly Guid AdaptiveMode = new("4419b185-da1f-4f53-bc76-097d0c1efb1e");
        internal static readonly Guid Cabac = new("ee6cad62-d305-4248-a50e-e1b255f7caf8");
    }
}
