using System.Diagnostics;
using System.IO.Pipes;

namespace Spectari.Audio;

/// <summary>Owns process-audio startup, fallback, and teardown for one stream.</summary>
internal sealed class AudioPipeline : IDisposable
{
    private readonly uint _audioPid;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();

    // Capture is created asynchronously after ffmpeg connects. The gate makes
    // disposal either find the instance or prevent it from being created.
    private readonly Lock _audioGate = new();
    private ProcessAudioCapture? _audioCapture;
    private CancellationTokenRegistration _sessionCancellation;
    private int _disposed;

    internal AudioPipeline(uint audioPid, int port)
    {
        ArgumentOutOfRangeException.ThrowIfZero(audioPid);

        _audioPid = audioPid;
        PipeName = FormatPipeName(port);
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal string PipeName { get; }

    internal static AudioPipeline? Create(uint audioPid, int port) =>
        audioPid == 0 ? null : new AudioPipeline(audioPid, port);

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
                _audioCapture = new ProcessAudioCapture(_audioPid, videoEpochTicks, WriteToPipe);
            }
            Console.WriteLine($"[audio] capturing audio from process {_audioPid}");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Console.Error.WriteLine($"[audio] process loopback failed ({ex.Message}); feeding silence instead.");
            var silence = new byte[ProcessAudioCapture.SampleRate * ProcessAudioCapture.Channels * 4 / 100];
            long fallbackStartTicks = Stopwatch.GetTimestamp();
            long leadInFrames = ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);
            long leadInMs = (leadInFrames * 1000 + ProcessAudioCapture.SampleRate / 2)
                / ProcessAudioCapture.SampleRate;
            Console.WriteLine($"[audio] aligned to video timeline (+{leadInMs} ms lead-in silence)");

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
