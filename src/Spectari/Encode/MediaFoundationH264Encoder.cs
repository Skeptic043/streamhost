using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Spectari.Encode;

/// <summary>Asynchronous Media Foundation H.264 encoder for pooled DXGI surfaces.</summary>
internal sealed class MediaFoundationH264Encoder : IHardwareVideoEncoder
{
    internal const string DefaultFriendlyName = "Media Foundation H.264";
    private const int MfEventFlagNoWait = 1;
    private static readonly Guid MftEnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    private readonly Dictionary<nint, SubmittedInput> _inFlightBySample = new();
    private readonly AnnexBAccessUnitAssembler _annexB = new();

    private IMFActivate? _activation;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private IMFDXGIDeviceManager? _deviceManager;
    private ID3D11Device? _deviceReference;
    private string _friendlyName = DefaultFriendlyName;
    private int _inputCredits;
    private bool _mfStarted;
    private bool _initialized;
    private bool _sequenceHeaderLoaded;
    private bool _draining;
    private bool _streamEnded;
    private bool _drainCompleted;
    private bool _shutdownCompleted;
    private bool _disposed;
    private long _submittedFrameCount;
    private long _lastNeedInputEventTicks;
    private long _lastHaveOutputEventTicks;
    private long _releasedInputSamples;
    private long _maximumInputHoldFrames;
    private int _inputHoldLogged;
    private int _reorderingLogged;
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

            try { _friendlyName = NormalizeFriendlyName(_activation.FriendlyName); }
            catch { _friendlyName = DefaultFriendlyName; }
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
            Console.WriteLine(OrdinarySamplesExposeTracking()
                ? "[mf-encode] input sample tracking: ordinary samples expose IMFTrackedSample."
                : "[mf-encode] input sample tracking: ordinary samples are untracked; using an explicit sample-identity lease list.");
            PumpEvents([]);
        }
        catch
        {
            ReleaseAll(FrameLeaseReturnReason.Failure);
            try { _activation?.ShutdownObject(); } catch { }
            ReleaseTransform();
            throw;
        }
    }

    public bool TrySubmit(
        IHardwareEncodeFrame frame,
        long presentationTime100ns,
        long duration100ns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _transform is null)
        {
            frame.Return(FrameLeaseReturnReason.InputRejected);
            throw new InvalidOperationException("Hardware encoder is not initialized.");
        }
        if (_inputCredits <= 0) return false;
        if (frame is not GpuHardwareEncodeFrame gpuFrame)
        {
            frame.Return(FrameLeaseReturnReason.InputRejected);
            throw new ArgumentException(
                "Media Foundation requires a GPU texture frame.",
                nameof(frame));
        }

        VideoFrameLease lease = gpuFrame.Lease;
        ID3D11Texture2D texture = lease.NativeTexture
            ?? throw new InvalidOperationException("NV12 encoder lease lost its texture.");
        using IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID,
            texture,
            0,
            false);
        IMFSample sample = MediaFactory.MFCreateSample();
        try
        {
            sample.SampleTime = presentationTime100ns;
            sample.SampleDuration = duration100ns;
            sample.AddBuffer(buffer);
            var submittedInput = new SubmittedInput(gpuFrame, sample);
            _inFlightBySample.Add(sample.NativePointer, submittedInput);
            _transform.ProcessInput(0, sample, 0);
            _inputCredits--;
            long submittedFrame = Interlocked.Increment(ref _submittedFrameCount);
            submittedInput.SubmittedFrameNumber = submittedFrame;
            return true;
        }
        catch
        {
            _inFlightBySample.Remove(sample.NativePointer);
            sample.Dispose();
            frame.Return(FrameLeaseReturnReason.InputRejected);
            throw;
        }
    }

    public IReadOnlyList<EncodedAccessUnit> Poll(long nowTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _transform is null) return [];

        var output = new List<EncodedAccessUnit>();
        ReleaseFinishedInputSamples();
        PumpEvents(output);
        ReleaseFinishedInputSamples();
        return output;
    }

    public HardwarePullEncoderProgress GetProgressSnapshot() => new(
        _inFlightBySample.Count,
        Volatile.Read(ref _inputCredits),
        Interlocked.Read(ref _lastNeedInputEventTicks),
        Interlocked.Read(ref _lastHaveOutputEventTicks));

    public IReadOnlyList<EncodedAccessUnit> Shutdown()
    {
        if (_shutdownCompleted || !_initialized || _transform is null) return [];
        try
        {
            return HardwareEncoderShutdownSequence.Execute(
                SignalEndOfStream,
                () =>
                {
                    var output = new List<EncodedAccessUnit>();
                    DrainTransform(output);
                    return output;
                },
                () => _transform.ProcessMessage(
                    TMessageType.MessageNotifyEndStreaming,
                    UIntPtr.Zero),
                () => _activation?.ShutdownObject(),
                () =>
                {
                    ReleaseAll(FrameLeaseReturnReason.Teardown);
                    ReleaseTransform();
                    _shutdownCompleted = true;
                });
        }
        catch
        {
            try { _activation?.ShutdownObject(); } catch { }
            ReleaseAll(FrameLeaseReturnReason.Failure);
            ReleaseTransform();
            _shutdownCompleted = true;
            throw;
        }
    }

    public void Flush()
    {
        if (_transform is not null)
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _draining = false;
        _drainCompleted = false;
        ReleaseAll(FrameLeaseReturnReason.Flush);
        DiscardQueuedEvents();
        _inputCredits = 0;
        _streamEnded = false;
        if (_initialized && _transform is not null)
            _transform.ProcessMessage(
                TMessageType.MessageNotifyStartOfStream,
                UIntPtr.Zero);
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
                        Interlocked.Exchange(ref _lastNeedInputEventTicks, Stopwatch.GetTimestamp());
                        if (!_streamEnded) _inputCredits++;
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        Interlocked.Exchange(ref _lastHaveOutputEventTicks, Stopwatch.GetTimestamp());
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

    private void DiscardQueuedEvents()
    {
        while (TryGetEvent(out IMFMediaEvent? mediaEvent))
            mediaEvent?.Dispose();
    }

    private void SignalEndOfStream()
    {
        if (_streamEnded || _transform is null) return;
        _transform.ProcessMessage(
            TMessageType.MessageNotifyEndOfStream,
            UIntPtr.Zero);
        _inputCredits = 0;
        _streamEnded = true;
    }

    private void DrainTransform(List<EncodedAccessUnit> output)
    {
        if (_drainCompleted || _transform is null) return;
        _draining = true;
        _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        while (_draining && Stopwatch.GetTimestamp() < deadline)
        {
            int before = output.Count;
            PumpEvents(output);
            ReleaseFinishedInputSamples();
            if (_draining && before == output.Count) Thread.Sleep(1);
        }

        if (_draining)
        {
            Console.Error.WriteLine(
                "[mf-encode] drain timed out after 2 seconds; flushing outstanding surfaces.");
            _transform.ProcessMessage(
                TMessageType.MessageCommandFlush,
                UIntPtr.Zero);
        }
        ReleaseFinishedInputSamples();
        _draining = false;
        _drainCompleted = true;
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
                long sampleTime = sample.SampleTime;
                long? decodeTimestamp = TryGetDecodeTimestamp(sample);
                if (SignalsReordering(sampleTime, decodeTimestamp))
                {
                    if (Interlocked.Exchange(ref _reorderingLogged, 1) == 0)
                    {
                        Console.Error.WriteLine(
                            $"[mf-encode] REORDERING DETECTED: output DecodeTimestamp {decodeTimestamp} differs from SampleTime {sampleTime}; the H.264 pipe contract does not support B-frames.");
                    }
                    throw new InvalidOperationException(
                        "Media Foundation output uses B-frame reordering; GPU encoding stopped before writing an invalid H.264 timeline.");
                }
                bool keyFrame = IsKeyFrame(sample);
                byte[] bytes = CopySampleBytes(sample);
                if (bytes.Length > 0)
                    output.Add(_annexB.Assemble(bytes, keyFrame));
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

    private static long? TryGetDecodeTimestamp(IMFSample sample)
    {
        try { return unchecked((long)sample.GetUInt64(SampleAttributeKeys.DecodeTimestamp)); }
        catch (SharpGenException) { return null; }
    }

    internal static bool SignalsReordering(
        long sampleTime,
        long? decodeTimestamp) =>
        decodeTimestamp.HasValue && decodeTimestamp.Value != sampleTime;

    private static bool OrdinarySamplesExposeTracking()
    {
        using IMFSample sample = MediaFactory.MFCreateSample();
        using IMFTrackedSample? tracked = sample.QueryInterfaceOrNull<IMFTrackedSample>();
        return tracked is not null;
    }

    private void ReleaseFinishedInputSamples()
    {
        if (_inFlightBySample.Count == 0) return;
        foreach ((nint identity, SubmittedInput input) in _inFlightBySample.ToArray())
        {
            if (TransformStillHolds(input.Sample)) continue;
            _inFlightBySample.Remove(identity);
            input.Sample.Dispose();
            input.Frame.Return(FrameLeaseReturnReason.InputReleased);

            long holdFrames = Math.Max(
                0,
                SubmittedFrameCount - input.SubmittedFrameNumber);
            _maximumInputHoldFrames = Math.Max(
                _maximumInputHoldFrames,
                holdFrames);
            long released = ++_releasedInputSamples;
            if (released >= 120 &&
                Interlocked.Exchange(ref _inputHoldLogged, 1) == 0)
            {
                Console.WriteLine(
                    $"[mf-encode] input sample hold: maximum {_maximumInputHoldFrames} later submissions across the first {released} releases; NV12 pool capacity {Nv12TexturePool.DefaultCapacity}.");
            }
        }
    }

    private static bool TransformStillHolds(IMFSample sample)
    {
        nint identity = sample.NativePointer;
        int referencesWithProbe = Marshal.AddRef(identity);
        Marshal.Release(identity);
        return referencesWithProbe > 2;
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

        var skippedProperties = new List<string>();
        ICodecApiNative codecApi;
        try { codecApi = (ICodecApiNative)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
        try
        {
            SetCodecValue(
                codecApi,
                "CODECAPI_AVEncCommonRateControlMode",
                CodecApiGuids.RateControlMode,
                0u,
                required: true);
            SetCodecValue(
                codecApi,
                "CODECAPI_AVEncCommonMeanBitRate",
                CodecApiGuids.MeanBitRate,
                checked((uint)parameters.BitrateKbps * 1000), required: true);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncCommonMaxBitRate",
                CodecApiGuids.MaxBitRate,
                checked((uint)parameters.MaximumBitrateKbps * 1000),
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncCommonBufferSize",
                CodecApiGuids.BufferSize,
                parameters.H264BufferSizeBytes,
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncMPVGOPSize",
                CodecApiGuids.GopSize,
                (uint)parameters.GopFrames,
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncMPVDefaultBPictureCount",
                CodecApiGuids.BPictureCount,
                0u,
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVLowLatencyMode",
                CodecApiGuids.LowLatency,
                true,
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncAdaptiveMode",
                CodecApiGuids.AdaptiveMode,
                0u,
                skippedProperties);
            SetOptionalCodecValue(
                codecApi,
                "CODECAPI_AVEncH264CABACEnable",
                CodecApiGuids.Cabac,
                true,
                skippedProperties);
        }
        finally
        {
            Console.WriteLine(
                $"[mf-encode] skipped optional ICodecAPI properties: {(skippedProperties.Count == 0 ? "none" : string.Join(", ", skippedProperties))}.");
            Marshal.ReleaseComObject(codecApi);
        }
    }

    private static void SetOptionalCodecValue(
        ICodecApiNative codecApi,
        string propertyName,
        Guid property,
        object value,
        ICollection<string> skippedProperties)
    {
        if (!SetCodecValue(codecApi, propertyName, property, value, required: false))
            skippedProperties.Add(propertyName);
    }

    private static bool SetCodecValue(
        ICodecApiNative codecApi,
        string propertyName,
        Guid property,
        object value,
        bool required)
    {
        try
        {
            int supported = codecApi.IsSupported(ref property);
            if (supported != 0)
                return RejectCodecProperty(propertyName, "support check", supported, required);

            int result = codecApi.SetValue(ref property, ref value);
            return result == 0 || RejectCodecProperty(
                propertyName,
                "value set",
                result,
                required);
        }
        catch (Exception) when (!required)
        {
            return false;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith($"ICodecAPI property {propertyName} ", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ICodecAPI property {propertyName} configuration failed: {SingleLine(ex.Message)}",
                ex);
        }
    }

    private static bool RejectCodecProperty(
        string propertyName,
        string action,
        int result,
        bool required)
    {
        if (!required) return false;

        Exception? error = Marshal.GetExceptionForHR(result);
        string detail = error is null
            ? $"HRESULT 0x{result:X8}"
            : SingleLine(error.Message);
        throw new InvalidOperationException(
            $"ICodecAPI property {propertyName} {action} failed: {detail}",
            error);
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
        foreach (SubmittedInput input in _inFlightBySample.Values)
        {
            input.Sample.Dispose();
            input.Frame.Return(reason);
        }
        _inFlightBySample.Clear();
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
            if (!_shutdownCompleted)
            {
                try { _ = Shutdown(); }
                catch
                {
                    try { _activation?.ShutdownObject(); } catch { }
                    ReleaseAll(FrameLeaseReturnReason.Teardown);
                    ReleaseTransform();
                    _shutdownCompleted = true;
                }
            }
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

    internal static string NormalizeFriendlyName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultFriendlyName : value.Trim();

    private sealed class SubmittedInput(
        GpuHardwareEncodeFrame frame,
        IMFSample sample)
    {
        internal GpuHardwareEncodeFrame Frame { get; } = frame;
        internal IMFSample Sample { get; } = sample;
        internal long SubmittedFrameNumber { get; set; }
    }

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
