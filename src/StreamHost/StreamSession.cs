using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StreamHost.Capture;
using StreamHost.Encode;
using StreamHost.Mp4;
using StreamHost.Server;
using StreamHost.Util;

namespace StreamHost;

public sealed record SessionConfig
{
    public IntPtr MonitorHandle { get; init; }
    public IntPtr WindowHandle { get; init; }
    public string SourceName { get; init; } = "";
    public uint AudioPid { get; init; }
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 12000;
    public int Port { get; init; } = 8093;
    public int OutHeight { get; init; }          // 0 = native; else scale to this height (AR kept)
    public string Encoder { get; init; } = "auto";
    public int FragMs { get; init; } = 50;
    public bool NoCursor { get; init; }

    /// <summary>Monitor shares only: use DXGI desktop duplication instead of WGC.
    /// Sees exclusive-fullscreen games (which freeze under WGC) but omits the cursor.</summary>
    public bool CompatibilityCapture { get; init; }
}

/// <summary>
/// One running stream: capture → encode → split → serve. Owns all pipeline
/// pieces and its own pacing thread. Used by both the console path and the UI.
/// </summary>
public sealed class StreamSession
{
    private readonly SessionConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    // Audio capture is created asynchronously (when ffmpeg connects to the pipe),
    // so creation and disposal race on quick start→stop — the gate makes disposal
    // either find the instance or prevent it from being created at all.
    private readonly Lock _audioGate = new();
    private Audio.ProcessAudioCapture? _audioCapture;
    private volatile bool _encoderStalled;

    public Broadcaster? Broadcaster { get; private set; }
    public string? Description { get; private set; }
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>True when the server could only bind localhost (no URL ACL for this port).</summary>
    public bool LocalOnly { get; private set; }

    /// <summary>Fires when the pipeline ends for any reason (Stop, ffmpeg death, error).</summary>
    public event Action<string>? Stopped;

    public StreamSession(SessionConfig config) => _config = config;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            string reason = "stopped";
            try { reason = Run(); }
            catch (Exception ex)
            {
                reason = ex.Message;
                Console.Error.WriteLine($"[session] {ex}");
            }
            finally { Stopped?.Invoke(reason); }
        })
        { IsBackground = true, Name = "stream-session" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _thread?.Join(4000);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    private string Run()
    {
        // No sleeping mid-stream: system stays up and the captured display stays on.
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        try { return RunCore(); }
        finally { SetThreadExecutionState(ES_CONTINUOUS); }
    }

    private string RunCore()
    {
        var ct = _cts.Token;
        // Monitor shares are self-managing (standard capture with automatic
        // duplication fallback for exclusive-fullscreen apps and dead backends).
        // --compat-capture forces duplication-only, kept for diagnostics.
        using ICaptureSource capture = _config.WindowHandle != IntPtr.Zero
            ? ScreenCapture.ForWindow(_config.WindowHandle)
            : _config.CompatibilityCapture
                ? new DuplicationCapture(_config.MonitorHandle)
                : new AutoMonitorCapture(_config.MonitorHandle);
        if (_config.NoCursor) capture.CursorEnabled = false;
        Console.WriteLine($"[capture] {_config.SourceName} @ {capture.Width}x{capture.Height}, GPU vendor 0x{capture.GpuVendorId:X4}{(_config.NoCursor ? ", cursor off" : "")}");

        int outW, outH;
        if (_config.OutHeight > 0 && _config.OutHeight < capture.Height)
        {
            outH = _config.OutHeight & ~1;
            outW = (int)Math.Round((double)capture.Width * outH / capture.Height) & ~1;
        }
        else
        {
            outW = capture.Width & ~1;
            outH = capture.Height & ~1;
        }

        string encoder = FfmpegEncoder.PickEncoder(capture.GpuVendorId, _config.Encoder);

        string? audioPipeName = _config.AudioPid != 0 ? $"streamhost_audio_{_config.Port}" : null;
        NamedPipeServerStream? audioPipe = null;
        if (audioPipeName is not null)
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var ffmpeg = new FfmpegEncoder(capture.Width, capture.Height, _config.Fps,
            _config.BitrateKbps, outW, outH, encoder, audioPipeName, _config.FragMs);

        if (audioPipe is not null)
        {
            uint audioPid = _config.AudioPid;
            _ = audioPipe.WaitForConnectionAsync(ct).ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled) return;
                void WriteToPipe(byte[] buf, int len)
                {
                    try { audioPipe.Write(buf, 0, len); } catch { /* pipe closed on shutdown */ }
                }
                try
                {
                    lock (_audioGate)
                    {
                        if (ct.IsCancellationRequested) return; // stopped before we got here
                        _audioCapture = new Audio.ProcessAudioCapture(audioPid, WriteToPipe);
                    }
                    Console.WriteLine($"[audio] capturing audio from process {audioPid}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[audio] process loopback failed ({ex.Message}) — feeding silence instead.");
                    var silence = new byte[Audio.ProcessAudioCapture.SampleRate * Audio.ProcessAudioCapture.Channels * 4 / 100];
                    new Thread(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        long sent = 0;
                        while (!ct.IsCancellationRequested)
                        {
                            long expected = sw.ElapsedMilliseconds / 10;
                            while (sent < expected) { WriteToPipe(silence, silence.Length); sent++; }
                            Thread.Sleep(5);
                        }
                    })
                    { IsBackground = true, Name = "audio-silence" }.Start();
                }
            }, TaskScheduler.Default);
        }

        var broadcaster = new Broadcaster { Width = outW, Height = outH, Fps = _config.Fps, HasAudio = audioPipeName is not null };
        Broadcaster = broadcaster;
        var splitterTask = Task.Run(() => Mp4Splitter.RunAsync(ffmpeg.Output, broadcaster, ct), ct);

        using var server = new WebServer(_config.Port, broadcaster);
        var serverTask = Task.Run(() => server.RunAsync(ct), ct);
        LocalOnly = server.BoundPrefix.Contains("localhost");
        if (LocalOnly)
            Console.Error.WriteLine($"[http] THIS PC ONLY: remote viewers will get HTTP 400. Run setup.bat {_config.Port} as administrator, then restart the stream.");

        // First-frame gate: Windows capture always delivers the current contents
        // immediately on start, so "no frame within 5s" reliably means the capture
        // backend is broken on this machine — fail loudly instead of looking live.
        Description = $"{outW}x{outH}@{_config.Fps} ~{_config.BitrateKbps}kbps via {encoder}";
        var gateStart = Stopwatch.GetTimestamp();
        while (capture.FrameVersion == 0)
        {
            if (ct.IsCancellationRequested) return "stopped";
            if (capture.CaptureError is not null)
                return $"capture failed: {capture.CaptureError.Message} (HRESULT 0x{capture.CaptureError.HResult:X8})";
            if (Stopwatch.GetTimestamp() - gateStart > Stopwatch.Frequency * 5)
            {
                broadcaster.State = "failed";
                Console.Error.WriteLine("[capture] no frames received within 5 seconds.");
                Console.Error.WriteLine($"[capture] backend started but never delivered a frame — source: {_config.SourceName}, adapter: {capture.AdapterName}.");
                Console.Error.WriteLine("[capture] worth trying: a different source, the compatibility capture option, or a GPU driver update.");
                Console.Error.WriteLine("[capture] when reporting this, use Copy log in the app — the log file path is printed at startup.");
                return "no frames from screen capture — see log";
            }
            Thread.Sleep(100);
        }

        broadcaster.State = "live";
        Console.WriteLine($"[ready] first frame captured — streaming {Description}");
        Console.WriteLine($"[ready] watch at: http://localhost:{_config.Port}/");
        foreach (var ip in GetShareAddresses())
            Console.WriteLine($"[ready]           http://{ip}:{_config.Port}/");

        // Independent encoder-output watchdog. A stalled GPU encoder makes ffmpeg
        // stop reading stdin, which blocks the pacing loop itself — so the stall
        // check must run on its own thread and cancel the session from outside.
        long fragsBaseline = Interlocked.Read(ref broadcaster.FragmentsSent);
        var watchdog = new Thread(() =>
        {
            if (ct.WaitHandle.WaitOne(5000)) return; // stopped normally within 5s
            if (Interlocked.Read(ref broadcaster.FragmentsSent) - fragsBaseline < 5)
            {
                _encoderStalled = true;
                Console.Error.WriteLine($"[encoder] {ffmpeg.EncoderName} wrote the header but produced no video in 5s — the GPU encoder is stalling.");
                _cts.Cancel();
            }
        })
        { IsBackground = true, Name = "encoder-watchdog" };
        watchdog.Start();

        string exitReason = PacingLoop(capture, ffmpeg, broadcaster, ct);
        if (_encoderStalled) exitReason = "encoder-stall";
        broadcaster.State = _encoderStalled ? "failed" : "stopped";

        Console.WriteLine("[shutdown] stopping…");
        _cts.Cancel();
        lock (_audioGate)
        {
            _audioCapture?.Dispose();
            _audioCapture = null;
        }
        audioPipe?.Dispose();
        try { Task.WaitAll(new[] { splitterTask, serverTask }, 3000); } catch { }
        return exitReason;
    }

    private string PacingLoop(ICaptureSource capture, FfmpegEncoder ffmpeg, Broadcaster broadcaster, CancellationToken ct)
    {
        using var timer = new HighResTimer();
        if (!timer.IsHighResolution)
            Console.WriteLine("[pacing] WARNING: high-resolution timer unavailable, falling back to Sleep()");
        using var writer = new FrameWriter(ffmpeg, capture.Width * capture.Height * 4);

        int fps = _config.Fps;
        long ticksPerFrame = Stopwatch.Frequency / fps;
        int graceMs = Math.Max(1000 / fps * 2 / 5, 2);
        long next = Stopwatch.GetTimestamp() + ticksPerFrame;
        long lastVersion = 0;
        long freshCount = 0, dupCount = 0, lateTicks = 0, lastCompositorFrames = 0;
        long lastReport = Stopwatch.GetTimestamp();
        long reportInterval = Stopwatch.Frequency * 2;

        while (!ct.IsCancellationRequested)
        {
            timer.WaitUntil(next);

            if (ffmpeg.HasExited || writer.Failed)
            {
                Console.Error.WriteLine($"[encoder] ffmpeg exited unexpectedly (code {ffmpeg.ExitCode}) — stopping.");
                return $"encoder exited unexpectedly (code {ffmpeg.ExitCode})";
            }
            if (capture.CaptureError is not null)
            {
                return $"capture failed mid-stream: {capture.CaptureError.Message}";
            }

            bool fresh = capture.WaitForFreshFrame(lastVersion, graceMs);
            lastVersion = capture.FrameVersion;

            byte[] buffer;
            try { buffer = writer.RentBuffer(ct); }
            catch (OperationCanceledException) { break; }

            if (capture.TryReadFrame(buffer))
            {
                writer.Enqueue(buffer);
                if (fresh) freshCount++; else dupCount++;
            }
            else
            {
                writer.ReturnUnused(buffer);
            }

            next += ticksPerFrame;
            long now = Stopwatch.GetTimestamp();
            if (now > next + 5 * ticksPerFrame) { next = now + ticksPerFrame; lateTicks++; }

            if (now - lastReport > reportInterval)
            {
                reportInterval = Stopwatch.Frequency * 10;
                long total = freshCount + dupCount;
                double dupPct = total > 0 ? dupCount * 100.0 / total : 0;
                double windowSec = (double)(now - lastReport) / Stopwatch.Frequency;
                broadcaster.SourceFps = (int)Math.Round((capture.FramesArrived - lastCompositorFrames) / windowSec);
                broadcaster.DupPercent = (int)Math.Round(dupPct);
                lastCompositorFrames = capture.FramesArrived;
                Console.WriteLine($"[stats] fresh {freshCount} dup {dupCount} ({dupPct:F1}%), late resyncs {lateTicks}, source {broadcaster.SourceFps} fps, viewers {broadcaster.ViewerCount}");
                freshCount = dupCount = 0;
                lastReport = now;
            }
        }
        return "stopped";
    }

    /// <summary>Shareable IPv4s, best first: Tailscale (100.64/10), then private LAN ranges.</summary>
    public static List<string> GetShareAddresses()
    {
        static int Rank(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return 0; // Tailscale / CGNAT
            if (b[0] == 192 && b[1] == 168) return 1;
            if (b[0] == 10) return 1;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 1;
            if (b[0] == 169 && b[1] == 254) return 3;               // link-local junk
            return 2;
        }
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .OrderBy(Rank)
                .Select(a => a.ToString())
                .ToList();
        }
        catch { return []; }
    }
}
