using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spectari.Util;

namespace Spectari.Capture;

internal sealed class MediaFoundationCapture : ICaptureSource, ICaptureDiagnostics
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(10);
    private const int FlushTimeoutMs = 1500;
    private const int WorkerJoinTimeoutMs = 3000;

    private readonly string _symbolicLink;
    private readonly object _readerGate = new();
    private readonly object _frameGate = new();
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly ManualResetEventSlim _flushCompleted = new(false);
    private readonly AutoResetEvent _frameSignal = new(false);
    private readonly Thread _worker;
    private nint _mediaSource;
    private nint _sourceReader;
    private SourceReaderCallback? _readerCallback;
    private byte[]? _latestFrame;
    private Exception? _initializationError;
    private Exception? _captureError;
    private int _defaultStride;
    private int _disposed;
    private long _frameVersion;
    private long _framesArrived;
    private long _readsStarted;
    private long _framesReady;
    private long _copiesStarted;
    private long _copiesCompleted;
    private long _lastReadStartedTicks;
    private long _lastFrameReadyTicks;
    private long _lastCopyStartedTicks;
    private long _lastCopyCompletedTicks;
    private volatile string _readStage = "idle";
    private volatile string _copyStage = "idle";

    internal MediaFoundationCapture(string symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink))
            throw new ArgumentException("A capture device symbolic link is required.", nameof(symbolicLink));

        _symbolicLink = symbolicLink;
        _worker = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "capture-device-reader",
        };
        _worker.Start();

        if (!_initialized.Wait(InitializationTimeout))
        {
            Dispose();
            throw new TimeoutException("The capture device did not finish opening within 10 seconds.");
        }
        if (_initializationError is { } error)
        {
            Dispose();
            throw error;
        }
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int? FrameRate { get; private set; }
    public uint GpuVendorId => 0;
    public string AdapterName => "Media Foundation";
    public string AdapterLuid => "";
    public string DriverVersion => "";
    public long FrameVersion => Interlocked.Read(ref _frameVersion);
    public long FramesArrived => Interlocked.Read(ref _framesArrived);
    public Exception? CaptureError => Volatile.Read(ref _captureError);
    public bool CursorEnabled { set { } }

    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        if (FrameVersion > sinceVersion) return true;
        if (Volatile.Read(ref _disposed) != 0) return false;
        _frameSignal.WaitOne(Math.Max(0, timeoutMs));
        return FrameVersion > sinceVersion;
    }

    public bool TryReadFrame(byte[] buffer)
    {
        lock (_frameGate)
        {
            if (_latestFrame is null || buffer.Length < _latestFrame.Length) return false;
            _latestFrame.CopyTo(buffer, 0);
            return true;
        }
    }

    public CaptureProgressSnapshot GetProgressSnapshot() => new(
        Interlocked.Read(ref _readsStarted),
        Interlocked.Read(ref _framesReady),
        Interlocked.Read(ref _copiesStarted),
        Interlocked.Read(ref _copiesCompleted),
        Interlocked.Read(ref _lastReadStartedTicks),
        Interlocked.Read(ref _lastFrameReadyTicks),
        Interlocked.Read(ref _lastCopyStartedTicks),
        Interlocked.Read(ref _lastCopyCompletedTicks),
        _readStage,
        _copyStage);

    private void CaptureLoop()
    {
        bool comInitialized = false;
        bool mediaFoundationStarted = false;
        try
        {
            int coHr = MediaFoundationInterop.CoInitializeEx(0, 0);
            MediaFoundationInterop.ThrowIfFailed(coHr, "Capture device COM initialization");
            comInitialized = true;
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, 0),
                "Media Foundation startup");
            mediaFoundationStarted = true;

            InitializeSourceReader();
            RequestNextFrame();
            _initialized.Set();
            _stopRequested.Wait();
            FlushSourceReader();
        }
        catch (Exception ex)
        {
            if (!_initialized.IsSet)
                _initializationError = ex;
            else if (Volatile.Read(ref _disposed) == 0)
                Volatile.Write(ref _captureError, ex);
        }
        finally
        {
            _initialized.Set();
            CleanupSourceReader();
            if (mediaFoundationStarted) _ = MediaFoundationInterop.MFShutdown();
            if (comInitialized) MediaFoundationInterop.CoUninitialize();
            _readStage = "stopped";
            _copyStage = "stopped";
        }
    }

    private void InitializeSourceReader()
    {
        nint sourceAttributes = 0;
        nint readerAttributes = 0;
        nint mediaSource = 0;
        nint sourceReader = 0;
        SourceReaderCallback? readerCallback = null;
        try
        {
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateAttributes(out sourceAttributes, 2),
                "Capture device source setup");
            MediaFoundationInterop.SetGuid(
                sourceAttributes,
                MediaFoundationInterop.MfDevSourceAttributeSourceType,
                MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapGuid);
            MediaFoundationInterop.SetString(
                sourceAttributes,
                MediaFoundationInterop.MfDevSourceAttributeSourceTypeVidcapSymbolicLink,
                _symbolicLink);

            int sourceHr = MediaFoundationInterop.MFCreateDeviceSource(sourceAttributes, out mediaSource);
            if (sourceHr < 0)
            {
                throw new CaptureTargetUnavailableException(
                    $"The selected capture device is no longer available or could not be opened (HRESULT 0x{sourceHr:X8}).");
            }

            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateAttributes(out readerAttributes, 2),
                "Capture device reader setup");
            readerCallback = new SourceReaderCallback(this);
            MediaFoundationInterop.SetUnknown(
                readerAttributes,
                MediaFoundationInterop.MfSourceReaderAsyncCallback,
                readerCallback.Pointer);
            MediaFoundationInterop.SetUInt32(
                readerAttributes,
                MediaFoundationInterop.MfSourceReaderEnableVideoProcessing,
                1);
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    readerAttributes,
                    out sourceReader),
                "Capture device reader creation");

            ConfigureFormat(sourceReader);
            lock (_readerGate)
            {
                _mediaSource = mediaSource;
                _sourceReader = sourceReader;
                _readerCallback = readerCallback;
                mediaSource = 0;
                sourceReader = 0;
                readerCallback = null;
            }
        }
        finally
        {
            readerCallback?.Dispose();
            MediaFoundationInterop.Release(ref sourceReader);
            if (mediaSource != 0)
            {
                _ = MediaFoundationInterop.ShutdownMediaSource(mediaSource);
                MediaFoundationInterop.Release(ref mediaSource);
            }
            MediaFoundationInterop.Release(ref readerAttributes);
            MediaFoundationInterop.Release(ref sourceAttributes);
        }
    }

    private void ConfigureFormat(nint sourceReader)
    {
        var nativeTypes = new List<(CaptureDeviceFormat Format, nint MediaType)>();
        nint sourceReaderEx = 0;
        nint outputType = 0;
        nint currentType = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                int hr = MediaFoundationInterop.GetNativeMediaType(
                    sourceReader,
                    MediaFoundationInterop.FirstVideoStream,
                    index,
                    out nint mediaType);
                if (hr == MediaFoundationInterop.MfENoMoreTypes) break;
                MediaFoundationInterop.ThrowIfFailed(hr, "Capture device format enumeration");
                if (TryDescribeFormat(mediaType, out CaptureDeviceFormat? format))
                    nativeTypes.Add((format!, mediaType));
                else
                    MediaFoundationInterop.Release(ref mediaType);
            }

            CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(
                nativeTypes.Select(item => item.Format));
            if (selected is null)
            {
                throw new CaptureTargetUnavailableException(
                    "The selected capture device does not offer MJPEG, NV12, or YUY2 video.");
            }

            nint selectedType = nativeTypes.First(item => item.Format == selected).MediaType;
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.QueryInterface(
                    sourceReader,
                    MediaFoundationInterop.IidSourceReaderEx,
                    out sourceReaderEx),
                "Capture device native format selection");
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.SetNativeMediaType(
                    sourceReaderEx,
                    MediaFoundationInterop.FirstVideoStream,
                    selectedType,
                    out _),
                "Capture device native format selection");

            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.MFCreateMediaType(out outputType),
                "Capture device output format setup");
            MediaFoundationInterop.SetGuid(
                outputType,
                MediaFoundationInterop.MfMtMajorType,
                MediaFoundationInterop.MfMediaTypeVideo);
            MediaFoundationInterop.SetGuid(
                outputType,
                MediaFoundationInterop.MfMtSubtype,
                MediaFoundationInterop.MfVideoFormatRgb32);
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.SetCurrentMediaType(
                    sourceReader,
                    MediaFoundationInterop.FirstVideoStream,
                    outputType),
                "Capture device BGRA conversion setup");
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.GetCurrentMediaType(
                    sourceReader,
                    MediaFoundationInterop.FirstVideoStream,
                    out currentType),
                "Capture device negotiated format read");

            CaptureDeviceFormat negotiated = ReadNegotiatedFormat(currentType, selected.PixelFormat);
            Width = negotiated.Width;
            Height = negotiated.Height;
            FrameRate = negotiated.RoundedFramesPerSecond;
            _defaultStride = MediaFoundationInterop.GetUInt32(
                currentType,
                MediaFoundationInterop.MfMtDefaultStride,
                out uint stride) >= 0
                    ? unchecked((int)stride)
                    : checked(Width * 4);
            _latestFrame = GC.AllocateUninitializedArray<byte>(checked(Width * Height * 4));
            ConsoleMirror.WriteDiagnosticLine(
                DiagnosticLogEventText.CaptureDeviceNativeMode(selected));
        }
        finally
        {
            MediaFoundationInterop.Release(ref currentType);
            MediaFoundationInterop.Release(ref outputType);
            MediaFoundationInterop.Release(ref sourceReaderEx);
            foreach ((_, nint mediaType) in nativeTypes)
            {
                nint value = mediaType;
                MediaFoundationInterop.Release(ref value);
            }
        }
    }

    private static bool TryDescribeFormat(nint mediaType, out CaptureDeviceFormat? format)
    {
        format = null;
        if (MediaFoundationInterop.GetGuid(
                mediaType,
                MediaFoundationInterop.MfMtSubtype,
                out Guid subtype) < 0 ||
            MediaFoundationInterop.GetUInt64(
                mediaType,
                MediaFoundationInterop.MfMtFrameSize,
                out ulong frameSize) < 0 ||
            MediaFoundationInterop.GetUInt64(
                mediaType,
                MediaFoundationInterop.MfMtFrameRate,
                out ulong frameRate) < 0)
        {
            return false;
        }

        CaptureDevicePixelFormat? pixelFormat = subtype == MediaFoundationInterop.MfVideoFormatNv12
            ? CaptureDevicePixelFormat.Nv12
            : subtype == MediaFoundationInterop.MfVideoFormatYuy2
                ? CaptureDevicePixelFormat.Yuy2
                : subtype == MediaFoundationInterop.MfVideoFormatMjpeg
                    ? CaptureDevicePixelFormat.Mjpeg
                    : null;
        if (pixelFormat is null) return false;

        var (width, height) = MediaFoundationInterop.UnpackRatio(frameSize);
        var (numerator, denominator) = MediaFoundationInterop.UnpackRatio(frameRate);
        if (width == 0 || height == 0 || numerator == 0 || denominator == 0) return false;
        format = new CaptureDeviceFormat(
            pixelFormat.Value,
            checked((int)width),
            checked((int)height),
            numerator,
            denominator);
        return true;
    }

    private static CaptureDeviceFormat ReadNegotiatedFormat(
        nint mediaType,
        CaptureDevicePixelFormat nativePixelFormat)
    {
        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.GetGuid(mediaType, MediaFoundationInterop.MfMtSubtype, out Guid subtype),
            "Capture device negotiated subtype read");
        if (subtype != MediaFoundationInterop.MfVideoFormatRgb32)
            throw new InvalidOperationException("The capture device could not negotiate RGB32 output.");
        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.GetUInt64(mediaType, MediaFoundationInterop.MfMtFrameSize, out ulong frameSize),
            "Capture device negotiated size read");
        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.GetUInt64(mediaType, MediaFoundationInterop.MfMtFrameRate, out ulong frameRate),
            "Capture device negotiated frame rate read");
        var (width, height) = MediaFoundationInterop.UnpackRatio(frameSize);
        var (numerator, denominator) = MediaFoundationInterop.UnpackRatio(frameRate);
        if (width == 0 || height == 0 || numerator == 0 || denominator == 0)
            throw new InvalidOperationException("The capture device negotiated an invalid video format.");
        return new CaptureDeviceFormat(
            nativePixelFormat,
            checked((int)width),
            checked((int)height),
            numerator,
            denominator);
    }

    private void RequestNextFrame()
    {
        lock (_readerGate)
        {
            if (_sourceReader == 0 ||
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _captureError) is not null)
            {
                return;
            }

            _readStage = "waiting-for-device";
            Interlocked.Increment(ref _readsStarted);
            Interlocked.Exchange(ref _lastReadStartedTicks, Stopwatch.GetTimestamp());
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.ReadSampleAsync(
                    _sourceReader,
                    MediaFoundationInterop.FirstVideoStream),
                "Capture device frame request");
        }
    }

    private void HandleReadSample(int hr, uint flags, nint sample)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            _readStage = "processing-device-frame";
            MediaFoundationInterop.ThrowIfFailed(hr, "Capture device frame read");
            if ((flags & MediaFoundationInterop.SourceReaderError) != 0)
                throw new InvalidOperationException("The capture device reported a frame read error.");
            if ((flags & MediaFoundationInterop.SourceReaderEndOfStream) != 0)
                throw new CaptureTargetUnavailableException("The capture device stopped delivering video.");
            if ((flags & (MediaFoundationInterop.SourceReaderNativeMediaTypeChanged |
                          MediaFoundationInterop.SourceReaderCurrentMediaTypeChanged)) != 0)
            {
                throw new InvalidOperationException("The capture device changed video format while streaming.");
            }

            if (sample != 0)
            {
                CopySample(sample);
                Interlocked.Increment(ref _framesReady);
                Interlocked.Exchange(ref _lastFrameReadyTicks, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _framesArrived);
                Interlocked.Increment(ref _frameVersion);
                _frameSignal.Set();
            }

            RequestNextFrame();
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Volatile.Write(ref _captureError, ex);
                _readStage = "failed";
                _frameSignal.Set();
                _stopRequested.Set();
            }
        }
    }

    private void HandleFlush() => _flushCompleted.Set();

    private void FlushSourceReader()
    {
        int hr;
        lock (_readerGate)
        {
            if (_sourceReader == 0) return;
            hr = MediaFoundationInterop.Flush(
                _sourceReader,
                MediaFoundationInterop.AllStreams);
        }

        if (hr < 0)
        {
            Console.Error.WriteLine($"[capture-device] reader flush failed (HRESULT 0x{hr:X8}).");
            return;
        }

        if (!_flushCompleted.Wait(FlushTimeoutMs))
        {
            Console.Error.WriteLine(
                "[capture-device] reader flush did not finish within 1.5 seconds; shutting down the media source.");
        }
    }

    private void CopySample(nint sample)
    {
        nint buffer = 0;
        nint buffer2D = 0;
        bool bufferLocked = false;
        bool buffer2DLocked = false;
        try
        {
            _copyStage = "locking-frame";
            Interlocked.Increment(ref _copiesStarted);
            Interlocked.Exchange(ref _lastCopyStartedTicks, Stopwatch.GetTimestamp());
            MediaFoundationInterop.ThrowIfFailed(
                MediaFoundationInterop.ConvertToContiguousBuffer(sample, out buffer),
                "Capture device sample buffer conversion");

            if (MediaFoundationInterop.QueryInterface(
                    buffer,
                    MediaFoundationInterop.Iid2DBuffer,
                    out buffer2D) >= 0)
            {
                MediaFoundationInterop.ThrowIfFailed(
                    MediaFoundationInterop.Lock2DBuffer(buffer2D, out nint scanline0, out int pitch),
                    "Capture device frame lock");
                buffer2DLocked = true;
                CopyRows(scanline0, pitch);
            }
            else
            {
                MediaFoundationInterop.ThrowIfFailed(
                    MediaFoundationInterop.LockMediaBuffer(buffer, out nint data, out uint currentLength),
                    "Capture device frame lock");
                bufferLocked = true;
                int pitch = _defaultStride == 0 ? checked(Width * 4) : _defaultStride;
                long required = (long)Math.Abs(pitch) * Height;
                if (currentLength < required)
                    throw new InvalidOperationException("The capture device returned a short RGB32 frame.");
                nint scanline0 = pitch >= 0
                    ? data
                    : data + checked((Height - 1) * Math.Abs(pitch));
                CopyRows(scanline0, pitch);
            }

            _copyStage = "idle";
            Interlocked.Increment(ref _copiesCompleted);
            Interlocked.Exchange(ref _lastCopyCompletedTicks, Stopwatch.GetTimestamp());
        }
        finally
        {
            if (buffer2DLocked) _ = MediaFoundationInterop.Unlock2DBuffer(buffer2D);
            if (bufferLocked) _ = MediaFoundationInterop.UnlockMediaBuffer(buffer);
            MediaFoundationInterop.Release(ref buffer2D);
            MediaFoundationInterop.Release(ref buffer);
        }
    }

    private unsafe void CopyRows(nint scanline0, int pitch)
    {
        int rowBytes = checked(Width * 4);
        if (scanline0 == 0 || Math.Abs((long)pitch) < rowBytes)
            throw new InvalidOperationException("The capture device returned an invalid RGB32 frame layout.");
        lock (_frameGate)
        {
            byte[] destination = _latestFrame
                ?? throw new InvalidOperationException("The capture device frame buffer is not initialized.");
            for (int row = 0; row < Height; row++)
            {
                nint sourceRow = scanline0 + checked(row * pitch);
                new ReadOnlySpan<byte>((void*)sourceRow, rowBytes)
                    .CopyTo(destination.AsSpan(checked(row * rowBytes), rowBytes));
            }
            for (int alpha = 3; alpha < destination.Length; alpha += 4)
                destination[alpha] = 255;
        }
    }

    private void CleanupSourceReader()
    {
        SourceReaderCallback? readerCallback;
        lock (_readerGate)
        {
            if (_mediaSource != 0) _ = MediaFoundationInterop.ShutdownMediaSource(_mediaSource);
            MediaFoundationInterop.Release(ref _sourceReader);
            MediaFoundationInterop.Release(ref _mediaSource);
            readerCallback = _readerCallback;
            _readerCallback = null;
        }
        readerCallback?.Dispose();
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed unsafe class SourceReaderCallback : IDisposable
    {
        private const int ENoInterface = unchecked((int)0x80004002);
        private const int EPointer = unchecked((int)0x80004003);
        private const int EFail = unchecked((int)0x80004005);
        private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly nint Vtable = CreateVtable();

        private readonly MediaFoundationCapture _owner;
        private nint _instance;

        internal SourceReaderCallback(MediaFoundationCapture owner)
        {
            _owner = owner;
            GCHandle handle = GCHandle.Alloc(this);
            try
            {
                var instance = (NativeInstance*)NativeMemory.AllocZeroed((nuint)sizeof(NativeInstance));
                if (instance is null) throw new OutOfMemoryException();
                instance->Vtable = Vtable;
                instance->Handle = GCHandle.ToIntPtr(handle);
                instance->RefCount = 1;
                _instance = (nint)instance;
            }
            catch
            {
                handle.Free();
                throw;
            }
        }

        internal nint Pointer => Volatile.Read(ref _instance);

        public void Dispose()
        {
            nint instance = Interlocked.Exchange(ref _instance, 0);
            if (instance != 0) _ = ReleaseCore(instance);
        }

        private static nint CreateVtable()
        {
            nint vtable = RuntimeHelpers.AllocateTypeAssociatedMemory(
                typeof(SourceReaderCallback),
                checked(6 * nint.Size));
            var entries = (nint*)vtable;
            entries[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
            entries[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            entries[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            entries[3] = (nint)(delegate* unmanaged[Stdcall]<nint, int, uint, uint, long, nint, int>)&OnReadSample;
            entries[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&OnFlush;
            entries[5] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&OnEvent;
            return vtable;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryInterface(nint self, Guid* iid, nint* result)
        {
            if (result is null) return EPointer;
            *result = 0;
            if (iid is null) return EPointer;
            if (*iid != IidUnknown && *iid != MediaFoundationInterop.IidSourceReaderCallback)
                return ENoInterface;
            *result = self;
            _ = AddRefCore(self);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint AddRef(nint self) => AddRefCore(self);

        private static uint AddRefCore(nint self) =>
            unchecked((uint)Interlocked.Increment(ref ((NativeInstance*)self)->RefCount));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint Release(nint self) => ReleaseCore(self);

        private static uint ReleaseCore(nint self)
        {
            var instance = (NativeInstance*)self;
            int count = Interlocked.Decrement(ref instance->RefCount);
            if (count != 0) return unchecked((uint)count);

            nint handle = instance->Handle;
            instance->Handle = 0;
            if (handle != 0) GCHandle.FromIntPtr(handle).Free();
            NativeMemory.Free(instance);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int OnReadSample(
            nint self,
            int hrStatus,
            uint streamIndex,
            uint streamFlags,
            long timestamp,
            nint sample)
        {
            try
            {
                GetCallback(self)._owner.HandleReadSample(hrStatus, streamFlags, sample);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[capture-device] source reader frame callback failed: {SingleLine(ex.Message)}");
                return EFail;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int OnFlush(nint self, uint streamIndex)
        {
            try
            {
                GetCallback(self)._owner.HandleFlush();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[capture-device] source reader flush callback failed: {SingleLine(ex.Message)}");
                return EFail;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int OnEvent(nint self, uint streamIndex, nint mediaEvent) => 0;

        private static SourceReaderCallback GetCallback(nint self)
        {
            nint handle = ((NativeInstance*)self)->Handle;
            return (SourceReaderCallback)GCHandle.FromIntPtr(handle).Target!;
        }

        private struct NativeInstance
        {
            internal nint Vtable;
            internal nint Handle;
            internal int RefCount;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _frameSignal.Set();
        _stopRequested.Set();
        if (_worker.IsAlive && !_worker.Join(WorkerJoinTimeoutMs))
        {
            Console.Error.WriteLine(
                "[capture-device] reader shutdown did not finish within 3 seconds; cleanup remains on the background reader.");
            return;
        }
        _frameSignal.Dispose();
        _initialized.Dispose();
        _stopRequested.Dispose();
        _flushCompleted.Dispose();
    }
}
