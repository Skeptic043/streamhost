using System.Diagnostics;
using System.IO.Pipes;

namespace Spectari.Audio;

/// <summary>Owns audio startup, fallback, and teardown for one stream.</summary>
internal sealed class AudioPipeline : IDisposable
{
    private readonly uint _audioPid;
    private readonly bool _captureDesktopAudio;
    private readonly string? _audioInputDeviceId;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();

    // Capture is created asynchronously after ffmpeg connects. The gate makes
    // disposal either find the instance or prevent it from being created.
    private readonly Lock _audioGate = new();
    private IDisposable? _audioCapture;
    private CancellationTokenRegistration _sessionCancellation;
    private int _disposed;

    internal AudioPipeline(uint audioPid, bool captureDesktopAudio, int port)
        : this(audioPid, captureDesktopAudio, null, port)
    {
    }

    internal AudioPipeline(
        uint audioPid,
        bool captureDesktopAudio,
        string? audioInputDeviceId,
        int port)
    {
        int selectedModes = (audioPid != 0 ? 1 : 0)
            + (captureDesktopAudio ? 1 : 0)
            + (!string.IsNullOrEmpty(audioInputDeviceId) ? 1 : 0);
        if (selectedModes != 1)
            throw new ArgumentException("Exactly one audio capture mode must be selected.");

        _audioPid = audioPid;
        _captureDesktopAudio = captureDesktopAudio;
        // An empty id means no device selection; storing it raw would route the
        // is-not-null branches below to device capture and silence process audio.
        _audioInputDeviceId = string.IsNullOrEmpty(audioInputDeviceId) ? null : audioInputDeviceId;
        PipeName = FormatPipeName(port);
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal string PipeName { get; }

    internal static AudioPipeline? Create(uint audioPid, bool captureDesktopAudio, int port) =>
        Create(audioPid, captureDesktopAudio, null, port);

    internal static AudioPipeline? Create(
        uint audioPid,
        bool captureDesktopAudio,
        string? audioInputDeviceId,
        int port) =>
        audioPid == 0 && !captureDesktopAudio && string.IsNullOrEmpty(audioInputDeviceId)
            ? null
            : new AudioPipeline(audioPid, captureDesktopAudio, audioInputDeviceId, port);

    internal static string FormatPipeName(int port) => $"spectari_audio_{port}";

    internal void Start(Task<long> videoEpoch, CancellationToken ct)
    {
        _sessionCancellation = ct.Register(
            static state => ((CancellationTokenSource)state!).Cancel(), _cts);
        _ = StartAsync(videoEpoch, _cts.Token);
    }

    private async Task StartAsync(Task<long> videoEpoch, CancellationToken ct)
    {
        long videoEpochTicks;
        try
        {
            await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            // Consume the epoch only after ffmpeg connects. Cancellation must win
            // if teardown begins before the first video frame is enqueued.
            videoEpochTicks = await videoEpoch.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Connection failure and session teardown both leave audio inactive.
            return;
        }

        void WriteToPipe(byte[] buf, int len)
        {
            // Pipe closure during shutdown is expected and unblocks this writer.
            try { _pipe.Write(buf, 0, len); } catch { }
        }

        try
        {
            lock (_audioGate)
            {
                // Cancellation inside the gate prevents creation after disposal.
                if (ct.IsCancellationRequested) return;
                _audioCapture = _captureDesktopAudio
                    ? new DesktopAudioCapture(videoEpochTicks, WriteToPipe)
                    : _audioInputDeviceId is not null
                        ? new CaptureDeviceAudioCapture(
                            _audioInputDeviceId,
                            videoEpochTicks,
                            WriteToPipe)
                        : new ProcessAudioCapture(_audioPid, videoEpochTicks, WriteToPipe);
            }
            if (_captureDesktopAudio)
                Console.WriteLine("[audio] desktop audio follows the default output device");
            else if (_audioInputDeviceId is not null)
                Console.WriteLine("[audio] capturing audio from the selected capture input");
            else
                Console.WriteLine($"[audio] capturing audio from process {_audioPid}");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            if (_captureDesktopAudio)
                Console.Error.WriteLine($"[audio] desktop loopback failed ({ex.Message}); feeding silence instead.");
            else if (_audioInputDeviceId is not null)
                Console.Error.WriteLine($"[audio] capture input failed ({ex.Message}); feeding silence instead.");
            else
                Console.Error.WriteLine($"[audio] process loopback failed ({ex.Message}); feeding silence instead.");
            var silence = new byte[ProcessAudioCapture.SampleRate * ProcessAudioCapture.Channels * 4 / 100];
            long fallbackStartTicks = Stopwatch.GetTimestamp();
            long leadInFrames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);
            Console.WriteLine(ProcessAudioCapture.FormatLeadInLog(leadInFrames));

            new Thread(() =>
            {
                const int bytesPerFrame = ProcessAudioCapture.Channels * 4;
                long sentBytes = 0;
                while (!ct.IsCancellationRequested)
                {
                    long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - fallbackStartTicks);
                    long expectedFrames = leadInFrames
                        + elapsedTicks * ProcessAudioCapture.SampleRate / Stopwatch.Frequency;
                    long expectedBytes = expectedFrames * bytesPerFrame;
                    while (sentBytes < expectedBytes)
                    {
                        int chunk = (int)Math.Min(silence.Length, expectedBytes - sentBytes);
                        WriteToPipe(silence, chunk);
                        sentBytes += chunk;
                    }
                    Thread.Sleep(5);
                }
            })
            { IsBackground = true, Name = "audio-silence" }.Start();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        // Closing the pipe first unblocks a writer parked on a dead ffmpeg before
        // capture disposal waits for its writer thread.
        _pipe.Dispose();
        lock (_audioGate)
        {
            _audioCapture?.Dispose();
            _audioCapture = null;
        }
        _sessionCancellation.Dispose();
        _cts.Dispose();
    }
}
