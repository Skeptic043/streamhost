using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spectari.Audio;

internal enum DefaultAudioDeviceChangeAction
{
    None,
    Bind,
    Unbind,
}

/// <summary>Decides when a live desktop capture must follow a new default endpoint.</summary>
internal sealed class DefaultAudioDeviceChangePolicy
{
    internal string? BoundDeviceId { get; private set; }

    internal DefaultAudioDeviceChangeAction Evaluate(string? defaultDeviceId)
    {
        if (string.IsNullOrEmpty(defaultDeviceId))
            return BoundDeviceId is null
                ? DefaultAudioDeviceChangeAction.None
                : DefaultAudioDeviceChangeAction.Unbind;

        return string.Equals(BoundDeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase)
            ? DefaultAudioDeviceChangeAction.None
            : DefaultAudioDeviceChangeAction.Bind;
    }

    internal void MarkBound(string deviceId) => BoundDeviceId = deviceId;
    internal void MarkUnbound() => BoundDeviceId = null;
}

internal sealed class DesktopAudioCapture : IDisposable
{
    private readonly WasapiEndpointAudioCapture _capture;

    internal DesktopAudioCapture(long videoEpochTicks, Action<byte[], int> onSamples) =>
        _capture = new WasapiEndpointAudioCapture(null, videoEpochTicks, onSamples);

    public void Dispose() => _capture.Dispose();
}

internal sealed class CaptureDeviceAudioCapture : IDisposable
{
    private readonly WasapiEndpointAudioCapture _capture;

    internal CaptureDeviceAudioCapture(
        string deviceId,
        long videoEpochTicks,
        Action<byte[], int> onSamples)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _capture = new WasapiEndpointAudioCapture(deviceId, videoEpochTicks, onSamples);
    }

    public void Dispose() => _capture.Dispose();
}

/// <summary>
/// Captures either the current default render endpoint through loopback or one
/// selected capture endpoint. Both modes emit the same 48 kHz stereo float32
/// stream and share timeline, queue, overflow, and silence-fill behavior.
/// </summary>
internal sealed class WasapiEndpointAudioCapture : IDisposable
{
    private const int SampleRate = ProcessAudioCapture.SampleRate;
    private const int Channels = ProcessAudioCapture.Channels;
    private const int BytesPerFrame = Channels * 4;
    private const long MaxCatchupBytes = SampleRate * BytesPerFrame * 2;
    private static readonly byte[] SilenceBlock = new byte[SampleRate * BytesPerFrame / 100];

    private readonly string? _audioInputDeviceId;
    private readonly Action<byte[], int> _onSamples;
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(256);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _captureThread;
    private readonly Thread _writerThread;
    private readonly long _captureStartTicks;
    private readonly long _leadInFrames;
    private long _deliveredFrames;
    private long _owedSilenceBytes;
    private int _disposed;

    internal WasapiEndpointAudioCapture(
        string? audioInputDeviceId,
        long videoEpochTicks,
        Action<byte[], int> onSamples)
    {
        _audioInputDeviceId = audioInputDeviceId;
        _onSamples = onSamples;
        _captureStartTicks = Stopwatch.GetTimestamp();
        _leadInFrames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, _captureStartTicks);

        int leadInBytes = checked((int)(_leadInFrames * BytesPerFrame));
        if (leadInBytes > 0)
        {
            _queue.Add((new byte[leadInBytes], leadInBytes));
            _deliveredFrames = _leadInFrames;
        }

        Console.WriteLine(ProcessAudioCapture.FormatLeadInLog(_leadInFrames));

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = audioInputDeviceId is null
                ? "desktop-audio-writer"
                : "capture-input-audio-writer",
            Priority = ThreadPriority.AboveNormal,
        };
        _writerThread.Start();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = audioInputDeviceId is null
                ? "desktop-audio-capture"
                : "capture-input-audio-capture",
            Priority = ThreadPriority.Highest,
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        const int COINIT_MULTITHREADED = 0;
        int initializeHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        bool comInitialized = initializeHr >= 0;
        nint enumerator = 0;
        WasapiAudioBinding? binding = null;
        IntPtr mmcss = IntPtr.Zero;

        try
        {
            Marshal.ThrowExceptionForHR(initializeHr);
            Marshal.ThrowExceptionForHR(CoreAudioInterop.CreateDeviceEnumerator(out enumerator));
            mmcss = ConfigureMmcss();
            string sourceLabel = _audioInputDeviceId is null
                ? "desktop loopback"
                : "capture device input";
            Console.WriteLine(
                $"[audio] capture started ({sourceLabel}, 48 kHz stereo{(mmcss != IntPtr.Zero ? ", MMCSS Pro Audio" : ", thread priority Highest")})");

            var devicePolicy = new DefaultAudioDeviceChangePolicy();
            bool endpointFailureLogged = false;
            bool readFailureLogged = false;
            bool overflowLogged = false;
            long nextDefaultCheckTicks = 0;
            long checkIntervalTicks = Stopwatch.Frequency / 10;
            long lastPacketTicks = _captureStartTicks;
            long idleAfterTicks = Stopwatch.Frequency * 150 / 1000;
            bool idle = false;
            long idleAnchorFrames = 0;
            long idleAnchorTicks = 0;

            while (!_cts.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (binding is null || now >= nextDefaultCheckTicks)
                {
                    nextDefaultCheckTicks = now + checkIntervalTicks;
                    string? targetDeviceId;
                    try
                    {
                        targetDeviceId = _audioInputDeviceId
                            ?? GetDefaultRenderDeviceId(enumerator);
                    }
                    catch (Exception ex)
                    {
                        if (!endpointFailureLogged)
                        {
                            endpointFailureLogged = true;
                            Console.Error.WriteLine(
                                $"[audio] default output device query failed ({ex.Message}); feeding silence and retrying.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        EmitSilenceToWallClock(now);
                        Thread.Sleep(10);
                        continue;
                    }

                    DefaultAudioDeviceChangeAction action = devicePolicy.Evaluate(targetDeviceId);
                    if (action != DefaultAudioDeviceChangeAction.None)
                    {
                        bool replacingDevice = binding is not null && targetDeviceId is not null;
                        ReleaseBinding(ref binding, devicePolicy);
                        EmitSilenceToWallClock(now);
                        idle = false;

                        if (replacingDevice && _audioInputDeviceId is null)
                            Console.WriteLine("[audio] default output device changed; switching desktop audio capture");

                        if (action == DefaultAudioDeviceChangeAction.Bind && targetDeviceId is not null)
                        {
                            try
                            {
                                binding = WasapiAudioBinding.Open(
                                    enumerator,
                                    targetDeviceId,
                                    loopback: _audioInputDeviceId is null);
                                EmitSilenceToWallClock(binding.StartTicks);
                                devicePolicy.MarkBound(targetDeviceId);
                                endpointFailureLogged = false;
                                readFailureLogged = false;
                                lastPacketTicks = binding.StartTicks;
                            }
                            catch (Exception ex)
                            {
                                if (!endpointFailureLogged)
                                {
                                    endpointFailureLogged = true;
                                    string failureSource = _audioInputDeviceId is null
                                        ? "desktop loopback device"
                                        : "selected capture input";
                                    Console.Error.WriteLine(
                                        $"[audio] {failureSource} open failed ({ex.Message}); feeding silence and retrying.");
                                }
                            }
                        }
                        else if (!endpointFailureLogged)
                        {
                            endpointFailureLogged = true;
                            Console.Error.WriteLine(
                                "[audio] no default output device is available; feeding silence until one appears.");
                        }
                    }
                }

                if (binding is null)
                {
                    EmitSilenceToWallClock(Stopwatch.GetTimestamp());
                    Thread.Sleep(10);
                    continue;
                }

                bool gotPacket = false;
                bool bindingFailed = false;
                while (true)
                {
                    int hr = CoreAudioInterop.GetNextPacketSize(
                        binding.Capture,
                        out uint packetFrames);
                    if (hr < 0)
                    {
                        if (!readFailureLogged)
                        {
                            readFailureLogged = true;
                            string recovery = _audioInputDeviceId is null
                                ? "rebinding the default output device"
                                : "reopening the selected capture input";
                            Console.Error.WriteLine(
                                $"[audio] {sourceLabel} read failed (0x{hr:X8}); {recovery}.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        bindingFailed = true;
                        break;
                    }
                    if (packetFrames == 0) break;

                    hr = CoreAudioInterop.GetCaptureBuffer(
                        binding.Capture,
                        out nint data,
                        out uint frames,
                        out uint flags);
                    if (hr < 0)
                    {
                        if (!readFailureLogged)
                        {
                            readFailureLogged = true;
                            string recovery = _audioInputDeviceId is null
                                ? "rebinding the default output device"
                                : "reopening the selected capture input";
                            Console.Error.WriteLine(
                                $"[audio] {sourceLabel} read failed (0x{hr:X8}); {recovery}.");
                        }
                        ReleaseBinding(ref binding, devicePolicy);
                        bindingFailed = true;
                        break;
                    }

                    try
                    {
                        gotPacket = true;
                        int bytes = checked((int)frames * BytesPerFrame);
                        var buffer = new byte[bytes];
                        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            Marshal.Copy(data, buffer, 0, bytes);
                        Emit(buffer, bytes, ref overflowLogged);
                    }
                    finally
                    {
                        if (binding is not null)
                            _ = CoreAudioInterop.ReleaseCaptureBuffer(binding.Capture, frames);
                    }
                }

                now = Stopwatch.GetTimestamp();
                if (bindingFailed)
                {
                    EmitSilenceToWallClock(now);
                }
                else if (gotPacket)
                {
                    lastPacketTicks = now;
                    idle = false;
                }
                else if (now - lastPacketTicks > idleAfterTicks)
                {
                    if (!idle)
                    {
                        idle = true;
                        idleAnchorFrames = _deliveredFrames + _owedSilenceBytes / BytesPerFrame;
                        idleAnchorTicks = lastPacketTicks;
                    }

                    long targetFrames = idleAnchorFrames
                        + (now - idleAnchorTicks) * SampleRate / Stopwatch.Frequency;
                    if (targetFrames > _deliveredFrames)
                        EmitSilence((targetFrames - _deliveredFrames) * BytesPerFrame);
                    if (_deliveredFrames >= targetFrames)
                        _owedSilenceBytes = 0;
                }

                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[audio] {(_audioInputDeviceId is null ? "desktop" : "capture input")} capture thread stopped on error ({ex.Message}); feeding silence instead.");
                while (!_cts.IsCancellationRequested)
                {
                    EmitSilenceToWallClock(Stopwatch.GetTimestamp());
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            binding?.Dispose();
            CoreAudioInterop.Release(ref enumerator);
            RevertMmcss(mmcss);
            if (comInitialized) CoUninitialize();
        }
    }

    private static void ReleaseBinding(
        ref WasapiAudioBinding? binding,
        DefaultAudioDeviceChangePolicy devicePolicy)
    {
        binding?.Dispose();
        binding = null;
        devicePolicy.MarkUnbound();
    }

    private static string? GetDefaultRenderDeviceId(nint enumerator)
    {
        const int E_RENDER = 0;
        const int E_CONSOLE = 0;
        const int HRESULT_NOT_FOUND = unchecked((int)0x80070490);

        nint device = 0;
        int hr = CoreAudioInterop.GetDefaultAudioEndpoint(
            enumerator,
            E_RENDER,
            E_CONSOLE,
            out device);
        if (hr == HRESULT_NOT_FOUND)
        {
            CoreAudioInterop.Release(ref device);
            return null;
        }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(CoreAudioInterop.GetDeviceId(device, out string deviceId));
            return deviceId;
        }
        finally
        {
            CoreAudioInterop.Release(ref device);
        }
    }

    private void EmitSilenceToWallClock(long nowTicks)
    {
        long elapsedTicks = Math.Max(0, nowTicks - _captureStartTicks);
        long targetFrames = _leadInFrames
            + elapsedTicks * SampleRate / Stopwatch.Frequency;
        if (targetFrames > _deliveredFrames)
            EmitSilence((targetFrames - _deliveredFrames) * BytesPerFrame);
        if (_deliveredFrames >= targetFrames)
            _owedSilenceBytes = 0;
    }

    private bool TryEnqueue((byte[] Buffer, int Length) item)
    {
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; }
    }

    private void OweSilence(long bytes) =>
        _owedSilenceBytes = Math.Min(_owedSilenceBytes + bytes, MaxCatchupBytes);

    private void RepayOwedSilence()
    {
        while (_owedSilenceBytes > 0)
        {
            int chunk = (int)Math.Min(_owedSilenceBytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk))) break;
            _owedSilenceBytes -= chunk;
            _deliveredFrames += chunk / BytesPerFrame;
        }
    }

    private void Emit(byte[] buffer, int length, ref bool overflowLogged)
    {
        if (TryEnqueue((buffer, length)))
        {
            _deliveredFrames += length / BytesPerFrame;
        }
        else
        {
            if (_queue.TryTake(out var stale))
            {
                _deliveredFrames -= stale.Length / BytesPerFrame;
                OweSilence(stale.Length);
            }

            if (TryEnqueue((buffer, length)))
                _deliveredFrames += length / BytesPerFrame;
            else
                OweSilence(length);

            if (!overflowLogged)
            {
                overflowLogged = true;
                Console.Error.WriteLine(
                    $"[audio] encoder is not draining {(_audioInputDeviceId is null ? "desktop audio" : "capture input audio")}; dropping stale audio to stay at the live edge; backfilling silence to resync.");
            }
        }

        RepayOwedSilence();
    }

    private void EmitSilence(long bytes)
    {
        while (bytes > 0 && !_cts.IsCancellationRequested)
        {
            int chunk = (int)Math.Min(bytes, SilenceBlock.Length);
            if (!TryEnqueue((SilenceBlock, chunk)))
            {
                OweSilence(bytes);
                return;
            }
            _deliveredFrames += chunk / BytesPerFrame;
            bytes -= chunk;
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (buffer, length) in _queue.GetConsumingEnumerable())
            {
                _onSamples(buffer, length);
                if (_cts.IsCancellationRequested) return;
            }
        }
        catch
        {
            // The pipe consumer can disappear during stream teardown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        bool captureStopped = _captureThread.Join(2000);
        _queue.CompleteAdding();
        _writerThread.Join(2000);
        // A delayed COM call may still return to the capture loop. Keep its
        // cancellation source alive until that thread is definitely gone.
        if (captureStopped) _cts.Dispose();
    }

    private sealed class WasapiAudioBinding : IDisposable
    {
        private nint _client;
        private int _disposed;

        private WasapiAudioBinding(
            nint client,
            nint capture,
            long startTicks)
        {
            _client = client;
            Capture = capture;
            StartTicks = startTicks;
        }

        internal nint Capture { get; private set; }
        internal long StartTicks { get; }

        internal static WasapiAudioBinding Open(
            nint enumerator,
            string deviceId,
            bool loopback)
        {
            nint device = 0;
            Marshal.ThrowExceptionForHR(CoreAudioInterop.GetDevice(
                enumerator,
                deviceId,
                out device));
            nint client = 0;
            nint capture = 0;
            bool started = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoreAudioInterop.Activate(
                    device,
                    CoreAudioInterop.AudioClientId,
                    out client));

                var format = new CoreAudioInterop.WaveFormatEx
                {
                    FormatTag = 3,
                    Channels = Channels,
                    SamplesPerSec = SampleRate,
                    AvgBytesPerSec = SampleRate * BytesPerFrame,
                    BlockAlign = BytesPerFrame,
                    BitsPerSample = 32,
                };

                const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
                const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
                const long BUFFER_DURATION_100NS = 5_000_000;
                uint streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                if (loopback) streamFlags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
                Marshal.ThrowExceptionForHR(CoreAudioInterop.InitializeAudioClient(
                    client,
                    streamFlags,
                    BUFFER_DURATION_100NS,
                    ref format));

                Marshal.ThrowExceptionForHR(CoreAudioInterop.GetAudioClientService(
                    client,
                    CoreAudioInterop.AudioCaptureClientId,
                    out capture));

                long startTicks = Stopwatch.GetTimestamp();
                Marshal.ThrowExceptionForHR(CoreAudioInterop.StartAudioClient(client));
                started = true;
                return new WasapiAudioBinding(client, capture, startTicks);
            }
            catch
            {
                if (started && client != 0)
                {
                    try { _ = CoreAudioInterop.StopAudioClient(client); } catch { }
                }
                CoreAudioInterop.Release(ref capture);
                CoreAudioInterop.Release(ref client);
                throw;
            }
            finally
            {
                CoreAudioInterop.Release(ref device);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _ = CoreAudioInterop.StopAudioClient(_client); } catch { }
            nint capture = Capture;
            Capture = 0;
            CoreAudioInterop.Release(ref capture);
            CoreAudioInterop.Release(ref _client);
        }
    }

    private static IntPtr ConfigureMmcss()
    {
        try
        {
            uint taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void RevertMmcss(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

}
