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

    /// <summary>Display name shown to viewers (grid tiles, viewer tab title).
    /// Empty = fall back to the machine name.</summary>
    public string StreamName { get; init; } = "";
    public uint AudioPid { get; init; }
    public int Fps { get; init; } = 60;

    /// <summary>0 = pick automatically from the actual output resolution.</summary>
    public int BitrateKbps { get; init; } = 12000;
    public int Port { get; init; } = 8093;

    /// <summary>Per-session viewer secret; the viewer page and WebSocket require
    /// it as ?k=. Null = no key required. Survives CPU-fallback and source-switch
    /// restarts so links keep working within one streaming run.</summary>
    public string? ViewKey { get; init; }
    public int OutHeight { get; init; }          // 0 = native; else scale to this height (AR kept)

    /// <summary>Hard viewer cap handed to the Broadcaster. 0 = unlimited.</summary>
    public int MaxViewers { get; init; } = 24;
    public string Encoder { get; init; } = "auto";
    public int FragMs { get; init; } = 50;
    public bool NoCursor { get; init; }

    /// <summary>Monitor shares only: use DXGI desktop duplication instead of WGC.
    /// Sees exclusive-fullscreen games (which freeze under WGC) but omits the cursor.</summary>
    public bool CompatibilityCapture { get; init; }

    /// <summary>Random viewer key for the ?k= link parameter: 16 random bytes
    /// (~128-bit) encoded base64url with no padding, so it is URL-safe raw
    /// (no +, /, or = to encode) and round-trips through the link untouched.</summary>
    public static string NewViewKey()
    {
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
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
    // The session A/V epoch is produced exactly once by the pacing loop at its
    // first successful FrameWriter.Enqueue. Audio waits for this timestamp so
    // both raw ffmpeg inputs describe the same real-time origin.
    private readonly TaskCompletionSource<long> _videoEpoch =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _pipelineStalled;
    private volatile string? _stallStopReason;
    private volatile string _pacingStage = "not-started";
    private volatile string _teardownStage = "not-started";
    private readonly SessionTerminationGate _termination = new();
    private const int TeardownTimeoutMs = 6000;
    private volatile CaptureAdapterIdentity? _captureAdapter;

    public Broadcaster? Broadcaster { get; private set; }
    public string? Description { get; private set; }

    /// <summary>The encoder actually running (after auto-pick and probe), e.g. "h264_nvenc".</summary>
    public string? ActiveEncoder { get; private set; }

    /// <summary>Adapter identity captured from the source that selected the encoder.
    /// Published as one immutable reference so support diagnostics cannot mix fields
    /// from different initialization moments.</summary>
    public sealed record CaptureAdapterIdentity(uint VendorId, string Luid, string DriverVersion);
    public CaptureAdapterIdentity? CaptureAdapter => _captureAdapter;

    /// <summary>Resolved output pixel size (native honored), so a CPU-fallback site
    /// knows the real resolution even when the config asked for native (OutHeight==0).</summary>
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public string? ViewKey => _config.ViewKey;
    public bool IsRunning => !_termination.IsCompleted && _thread is { IsAlive: true };
    public bool IsStopping => _cts.IsCancellationRequested;

    internal static bool IsPipelineStallReason(string? reason) =>
        reason?.StartsWith("video pipeline stalled", StringComparison.Ordinal) ?? false;

    /// <summary>True when the server could only bind localhost (no URL ACL for this port).</summary>
    public bool LocalOnly { get; private set; }

    /// <summary>True once the first frame was captured and the broadcaster went
    /// live. Lets a stop handler tell a run that actually served viewers (rotate
    /// its key) from a start that failed before going live, e.g. a port bind
    /// failure (keep the key so an already-copied idle link still works on retry).</summary>
    public bool WentLive { get; private set; }

    /// <summary>Fires once the session has released the resources required for a restart.</summary>
    public event Action<string>? Stopped
    {
        add => _termination.Stopped += value;
        remove => _termination.Stopped -= value;
    }

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
            finally
            {
                _termination.CompleteFromSession(reason);
            }
        })
        { IsBackground = true, Name = "stream-session" };
        _thread.Start();
    }

    /// <summary>Non-blocking stop for the UI: cancel and let the Stopped event
    /// report when teardown (including port release) actually finished. A stall
    /// salvager may abandon native capture, but Stopped still means the port is
    /// free and ffmpeg is dead before the next session starts.</summary>
    public void RequestStop() => _cts.Cancel();

    /// <summary>Blocking stop for process exit paths: don't leave an orphaned
    /// ffmpeg behind just because the app window closed. Returns true when the
    /// session thread is no longer running (joined in time, already finished, or
    /// never started); false only when the 6 s join timed out with the thread
    /// still running (teardown wedged, the stream may still be live).</summary>
    public bool Stop()
    {
        _cts.Cancel();
        var t = _thread;
        return t is null || t.Join(TeardownTimeoutMs);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

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
        // Guard the race the MainForm pre-check can't cover: the window can close
        // between that check and here. A dead HWND makes the capture backend throw
        // a raw ArgumentException; surface one plain line through the normal stop
        // path instead of a stack dump.
        if (_config.WindowHandle != IntPtr.Zero && !IsWindow(_config.WindowHandle))
        {
            Console.Error.WriteLine("[capture] the selected window no longer exists; pick another source.");
            return "the selected window no longer exists";
        }
        // Monitor shares are self-managing (desktop duplication by default,
        // standard capture as fallback and for window shares — see
        // AutoMonitorCapture). --compat-capture forces duplication-only,
        // kept for diagnostics.
        ICaptureSource capture = _config.WindowHandle != IntPtr.Zero
            ? ScreenCapture.ForWindow(_config.WindowHandle)
            : _config.CompatibilityCapture
                ? new DuplicationCapture(_config.MonitorHandle)
                : new AutoMonitorCapture(_config.MonitorHandle);
        using var captureLifetime = TrackCleanup(capture.Dispose, "capture");
        _captureAdapter = new(capture.GpuVendorId, capture.AdapterLuid, capture.DriverVersion);
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

        string encoder = FfmpegEncoder.PickEncoder(
            capture.GpuVendorId, capture.AdapterLuid, capture.DriverVersion, _config.Encoder);
        ActiveEncoder = encoder;
        OutputWidth = outW;
        OutputHeight = outH;

        // 0 = auto: scale bitrate with what actually goes out. A "Native" preset
        // used to hardcode 12 Mbps even when native meant 1440p or 4K.
        int bitrateKbps = _config.BitrateKbps > 0 ? _config.BitrateKbps : AutoBitrate(outW, outH, _config.Fps);
        if (_config.BitrateKbps <= 0)
            Console.WriteLine($"[encoder] auto bitrate for {outH}p{_config.Fps}: {bitrateKbps} kbps");

        string? audioPipeName = _config.AudioPid != 0 ? $"streamhost_audio_{_config.Port}" : null;
        NamedPipeServerStream? audioPipe = null;
        if (audioPipeName is not null)
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Every exit path — including a failed port bind throwing out of the
        // WebServer constructor below — must cancel and release the audio
        // machinery: the pipe-connection continuation otherwise creates a
        // capture into a dead session and leaks it (busy thread, WASAPI
        // session, the pipe itself). Declared before the ffmpeg/server usings
        // so it runs last, after they are gone.
        using var audioTeardown = new SalvageableCleanup(() =>
        {
            SetTeardownStage("stopping audio");
            try
            {
                _cts.Cancel();
                // Dispose the pipe first: it unblocks the capture's writer thread if
                // it is parked on a Write to a now-dead ffmpeg, so the Dispose join
                // below returns immediately instead of eating its full timeout.
                audioPipe?.Dispose();
                lock (_audioGate)
                {
                    _audioCapture?.Dispose();
                    _audioCapture = null;
                }
            }
            finally { SetTeardownStage("audio stopped"); }
        });

        var ffmpeg = new FfmpegEncoder(capture.Width, capture.Height, _config.Fps,
            bitrateKbps, outW, outH, encoder, audioPipeName, _config.FragMs);
        using var ffmpegLifetime = TrackCleanup(ffmpeg.Dispose, "ffmpeg");

        if (audioPipe is not null)
        {
            uint audioPid = _config.AudioPid;
            _ = StartAudioAsync(audioPipe, audioPid, ct);
        }

        var broadcaster = new Broadcaster
        {
            Width = outW,
            Height = outH,
            Fps = _config.Fps,
            MaxViewers = _config.MaxViewers,
            HasAudio = audioPipeName is not null,
            StreamName = string.IsNullOrWhiteSpace(_config.StreamName)
                ? Environment.MachineName : _config.StreamName.Trim(),
        };
        Broadcaster = broadcaster;
        var splitterTask = Task.Run(() => Mp4Splitter.RunAsync(ffmpeg.Output, broadcaster, ct), ct);

        var server = new WebServer(_config.Port, broadcaster, _config.ViewKey);
        using var serverLifetime = TrackCleanup(server.Dispose, "web server");
        var serverTask = Task.Run(() => server.RunAsync(ct), ct);
        LocalOnly = server.BoundPrefix.Contains("localhost");
        if (LocalOnly)
            Console.Error.WriteLine($"[http] THIS PC ONLY: remote viewers will get HTTP 400. Run setup.bat {_config.Port} as administrator, then restart the stream.");

        // First-frame gate: Windows capture always delivers the current contents
        // immediately on start, so "no frame within 5s" reliably means the capture
        // backend is broken on this machine — fail loudly instead of looking live.
        Description = $"{outW}x{outH}@{_config.Fps} ~{bitrateKbps}kbps";
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
                Console.Error.WriteLine($"[capture] backend started but never delivered a frame; source: {_config.SourceName}, adapter: {capture.AdapterName}.");
                Console.Error.WriteLine("[capture] worth trying: a different source, the compatibility capture option, or a GPU driver update.");
                Console.Error.WriteLine("[capture] when reporting this, use Copy log in the app; the log file path is printed at startup.");
                return "no frames from screen capture; see log";
            }
            Thread.Sleep(100);
        }

        broadcaster.State = "live";
        WentLive = true;
        // Console users have no other way to get the link, so the key is printed
        // (and therefore lands in the log file) — the support bundle strips it.
        string keySuffix = _config.ViewKey is null ? "" : $"?k={_config.ViewKey}";
        Console.WriteLine($"[ready] first frame captured; streaming {Description} via {encoder}");
        Console.WriteLine($"[ready] watch at: http://localhost:{_config.Port}/{keySuffix}");
        // Tailscale addresses are in scope in every firewall config, so they are
        // always live. LAN addresses only work if LAN access was actually opened;
        // console mode has no persisted scope, so the honest form is a caveat.
        var shareAddrs = GetShareAddresses();
        foreach (var ip in shareAddrs.Where(IsTailscaleAddress))
            Console.WriteLine($"[ready]           http://{ip}:{_config.Port}/{keySuffix}");
        var lanAddrs = shareAddrs.Where(ip => !IsTailscaleAddress(ip)).ToList();
        if (lanAddrs.Count > 0)
        {
            Console.WriteLine($"[ready] the LAN links below only work if you allowed LAN access (setup.bat or Open port with Allow LAN):");
            foreach (var ip in lanAddrs)
                Console.WriteLine($"[ready]           http://{ip}:{_config.Port}/{keySuffix}");
        }

        // Independent output watchdog. It runs for the whole session because a
        // capture, pacing, ffmpeg-input, or encoder/output stage can stop later.
        // Frames are fed at a fixed rate regardless of screen activity, so a
        // zero-fragment window proves a stall but not which stage caused it.
        var writer = new FrameWriter(ffmpeg, capture.Width * capture.Height * 4);
        using var writerLifetime = TrackCleanup(writer.Dispose, "ffmpeg frame writer");
        var stallTeardown = new StallTeardownCoordinator(
            _termination, TeardownTimeoutMs, _config.Port, ffmpeg, ffmpegLifetime,
            server.BoundPrefix, serverLifetime, audioTeardown, writerLifetime,
            captureLifetime);
        var watchdog = new Thread(() =>
        {
            var baseline = TakePipelineBaseline(capture, writer, broadcaster);
            // Wait for the header first: fragments cannot exist before it, and
            // on a machine pegged by the game ffmpeg's startup alone can eat
            // seconds — the old fixed window (counted from the first captured
            // frame) tripped on slow starts and blamed the encoder.
            if (!broadcaster.WaitForInit(TimeSpan.FromSeconds(10), ct))
            {
                if (ct.IsCancellationRequested) return;
                ReportPipelineStall(capture, writer, broadcaster, ffmpeg, stallTeardown, baseline,
                    $"[pipeline] ffmpeg produced no init segment in 10s while using {ffmpeg.EncoderName}; the live video pipeline is stalled.");
                return;
            }
            baseline = TakePipelineBaseline(capture, writer, broadcaster);
            bool firstWindow = true;
            while (!ct.WaitHandle.WaitOne(5000))
            {
                long total = Interlocked.Read(ref broadcaster.FragmentsSent);
                long produced = total - baseline.Fragments;
                if (firstWindow ? produced < 5 : produced == 0)
                {
                    ReportPipelineStall(capture, writer, broadcaster, ffmpeg, stallTeardown, baseline, firstWindow
                        ? $"[pipeline] ffmpeg wrote the init segment but produced fewer than five fragments in 5s while using {ffmpeg.EncoderName}; the live video pipeline is stalled."
                        : $"[pipeline] ffmpeg output stopped for 5s while using {ffmpeg.EncoderName}; the live video pipeline is stalled.");
                    return;
                }
                firstWindow = false;
                baseline = TakePipelineBaseline(capture, writer, broadcaster);
            }
        })
        { IsBackground = true, Name = "encoder-watchdog" };
        watchdog.Start();

        string exitReason;
        try
        {
            exitReason = PacingLoop(capture, ffmpeg, writer, broadcaster, splitterTask, serverTask, ct);
        }
        finally
        {
            _pacingStage = "stopped";
            writerLifetime.Dispose();
        }
        SetTeardownStage("pacing loop stopped");
        if (_stallStopReason is { } stallReason) exitReason = stallReason;
        broadcaster.State = _pipelineStalled ? "failed" : "stopped";

        Console.WriteLine("[shutdown] stopping…");
        SetTeardownStage("stopping background tasks");
        _cts.Cancel();
        try { Task.WaitAll(new[] { splitterTask, serverTask }, 3000); } catch { }
        SetTeardownStage("background tasks stopped");
        return exitReason; // audioTeardown finishes the audio cleanup on the way out
    }

    private void ReportPipelineStall(ICaptureSource capture, FrameWriter writer,
        Broadcaster broadcaster, FfmpegEncoder ffmpeg,
        StallTeardownCoordinator stallTeardown, PipelineBaseline baseline, string diagnostic)
    {
        _pipelineStalled = true;
        Console.Error.WriteLine(diagnostic);

        var currentWriter = writer.GetProgressSnapshot();
        long now = Stopwatch.GetTimestamp();
        long completedInputs = currentWriter.WritesCompleted - baseline.Writer.WritesCompleted;
        // One trailing pipe write can finish just as capture stops. Require the
        // same five-frame floor as the first output window before calling input
        // sustained; a write blocked for a full second is independently decisive.
        bool sustainedInput = completedInputs >= 5;
        bool inputWriteBlocked = currentWriter.WriteInProgress
            && now - currentWriter.LastWriteStartedTicks >= Stopwatch.Frequency;
        bool reachedFfmpeg = sustainedInput || inputWriteBlocked;
        string stopReason = sustainedInput
            ? "video pipeline stalled after sustained ffmpeg stdin writes; see log"
            : inputWriteBlocked
                ? "video pipeline stalled at ffmpeg stdin; see log"
                : "video pipeline stalled before sustained ffmpeg stdin writes; see log";
        _stallStopReason = stopReason;

        LogPipelineProgress(capture, writer, broadcaster, baseline);
        Console.Error.WriteLine(reachedFfmpeg
            ? "[pipeline] sustained ffmpeg stdin activity or a blocked stdin write places the stall at ffmpeg input/output; the cached hardware verdict is no longer trusted."
            : "[pipeline] ffmpeg stdin did not advance continuously during the watchdog window; this is an upstream capture, readback, or pacing stall, not a confirmed encoder failure.");

        if (ffmpeg.EncoderName != "libx264")
        {
            Console.Error.WriteLine("[pipeline] Starting a one-time CPU recovery session (libx264); capture and encoder state will be recreated.");
            // A positive hardware verdict is disproven only when this watchdog
            // window shows sustained ffmpeg input or a blocked input write while
            // output did not advance.
            if (reachedFfmpeg) FfmpegEncoder.InvalidateProbeCache();
        }
        else
            Console.Error.WriteLine("[pipeline] CPU recovery is already active; stopping with the stage reason above.");

        stallTeardown.Arm(stopReason, () => FormatActivePipelineStages(capture, writer));
        SetTeardownStage("stall cancellation requested");
        _cts.Cancel();
        // Killing the stalled child breaks a blocked stdin write. The deadline
        // retries and confirms termination before it releases the remaining pipeline.
        ffmpeg.AbortForStall();
    }

    private static PipelineBaseline TakePipelineBaseline(ICaptureSource capture,
        FrameWriter writer, Broadcaster broadcaster) => new(
            Stopwatch.GetTimestamp(),
            capture is ICaptureDiagnostics diagnostics ? diagnostics.GetProgressSnapshot() : null,
            writer.GetProgressSnapshot(),
            Interlocked.Read(ref broadcaster.FragmentsSent));

    private void LogPipelineProgress(ICaptureSource capture, FrameWriter writer,
        Broadcaster broadcaster, PipelineBaseline baseline)
    {
        long now = Stopwatch.GetTimestamp();
        var currentWriter = writer.GetProgressSnapshot();
        CaptureProgressSnapshot? currentCapture = capture is ICaptureDiagnostics diagnostics
            ? diagnostics.GetProgressSnapshot()
            : null;
        string captureDelta = currentCapture is { } current && baseline.Capture is { } prior
            ? $"capture-arrival +{current.CallbacksStarted - prior.CallbacksStarted}, gpu-frame-ready +{current.FramesReady - prior.FramesReady}, gpu-readback-start +{current.ReadbacksStarted - prior.ReadbacksStarted}, gpu-readback-complete +{current.ReadbacksCompleted - prior.ReadbacksCompleted}, "
            : "capture-stage counters unavailable for this backend, ";
        long fragments = Interlocked.Read(ref broadcaster.FragmentsSent);
        double elapsed = Math.Max(0, now - baseline.StartedTicks) / (double)Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[pipeline] progress over {elapsed:F1}s: {captureDelta}frame-enqueue +{currentWriter.FramesEnqueued - baseline.Writer.FramesEnqueued}, ffmpeg-stdin-start +{currentWriter.WritesStarted - baseline.Writer.WritesStarted}, ffmpeg-stdin-complete +{currentWriter.WritesCompleted - baseline.Writer.WritesCompleted}, ffmpeg-fragment +{fragments - baseline.Fragments}.");

        string captureAges = currentCapture is { } captureProgress
            ? $"capture-arrival {ProgressAge(captureProgress.LastCallbackTicks, now)}, gpu-frame-ready {ProgressAge(captureProgress.LastFrameReadyTicks, now)}, gpu-readback {ProgressAge(captureProgress.LastReadbackCompletedTicks, now)}, "
            : "";
        Console.Error.WriteLine(
            $"[pipeline] last completed: {captureAges}frame-enqueue {ProgressAge(currentWriter.LastEnqueueTicks, now)}, ffmpeg-stdin-write {ProgressAge(currentWriter.LastWriteCompletedTicks, now)}, ffmpeg-init {ProgressAge(broadcaster.InitReadyTicks, now)}, ffmpeg-fragment {ProgressAge(broadcaster.LastFragmentTicks, now)}.");
        Console.Error.WriteLine($"[pipeline] active stages: {FormatActivePipelineStages(capture, writer)}");
    }

    private string FormatActivePipelineStages(ICaptureSource capture, FrameWriter writer)
    {
        long now = Stopwatch.GetTimestamp();
        CaptureProgressSnapshot? captureProgress = capture is ICaptureDiagnostics diagnostics
            ? diagnostics.GetProgressSnapshot()
            : null;
        string captureStages = captureProgress is { } progress
            ? $"capture-callback={ActiveStage(progress.CallbackStage, progress.LastCallbackTicks, now)}, gpu-readback={ActiveStage(progress.ReadbackStage, progress.LastReadbackStartedTicks, now)}"
            : "capture-callback=unavailable, gpu-readback=unavailable";
        var writerProgress = writer.GetProgressSnapshot();
        string inputStage = writerProgress.WriteInProgress ? "writing"
            : writerProgress.Failed ? "failed"
            : "idle";
        return $"{captureStages}, pacing={_pacingStage}, ffmpeg-stdin={inputStage}, teardown={_teardownStage}";
    }

    private static string ActiveStage(string stage, long enteredTicks, long now) =>
        stage == "idle" ? "idle" : $"{stage} (entered {ProgressAge(enteredTicks, now)})";

    private static string ProgressAge(long then, long now)
    {
        if (then == 0) return "never";
        long ms = (long)(Math.Max(0, now - then) * 1000.0 / Stopwatch.Frequency);
        return ms < 1000 ? $"{ms}ms ago" : $"{ms / 1000.0:F1}s ago";
    }

    private void SetTeardownStage(string stage)
    {
        _teardownStage = stage;
        if (_pipelineStalled) Console.WriteLine($"[shutdown] {stage}");
    }

    private readonly record struct PipelineBaseline(
        long StartedTicks,
        CaptureProgressSnapshot? Capture,
        FrameWriterProgress Writer,
        long Fragments);

    private async Task StartAudioAsync(NamedPipeServerStream audioPipe, uint audioPid,
        CancellationToken ct)
    {
        long videoEpochTicks;
        try
        {
            await audioPipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            // Consume the epoch only after ffmpeg connects. Cancellation must win
            // if teardown begins before the first video frame is enqueued.
            videoEpochTicks = await _videoEpoch.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Connection failure and session teardown both leave audio inactive.
            return;
        }

        void WriteToPipe(byte[] buf, int len)
        {
            try { audioPipe.Write(buf, 0, len); } catch { /* pipe closed on shutdown */ }
        }

        try
        {
            lock (_audioGate)
            {
                if (ct.IsCancellationRequested) return; // stopped while waiting for the epoch
                _audioCapture = new Audio.ProcessAudioCapture(audioPid, videoEpochTicks, WriteToPipe);
            }
            Console.WriteLine($"[audio] capturing audio from process {audioPid}");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Console.Error.WriteLine($"[audio] process loopback failed ({ex.Message}); feeding silence instead.");
            var silence = new byte[Audio.ProcessAudioCapture.SampleRate * Audio.ProcessAudioCapture.Channels * 4 / 100];
            long fallbackStartTicks = Stopwatch.GetTimestamp();
            long leadInFrames = Audio.ProcessAudioCapture.GetLeadInFrames(videoEpochTicks, fallbackStartTicks);
            long leadInMs = (leadInFrames * 1000 + Audio.ProcessAudioCapture.SampleRate / 2)
                / Audio.ProcessAudioCapture.SampleRate;
            Console.WriteLine($"[audio] aligned to video timeline (+{leadInMs} ms lead-in silence)");

            new Thread(() =>
            {
                const int bytesPerFrame = Audio.ProcessAudioCapture.Channels * 4;
                long sentBytes = 0;
                while (!ct.IsCancellationRequested)
                {
                    long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - fallbackStartTicks);
                    long expectedFrames = leadInFrames
                        + elapsedTicks * Audio.ProcessAudioCapture.SampleRate / Stopwatch.Frequency;
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

    private string PacingLoop(ICaptureSource capture, FfmpegEncoder ffmpeg, FrameWriter writer,
        Broadcaster broadcaster, Task splitterTask, Task serverTask, CancellationToken ct)
    {
        using var timer = new HighResTimer();
        if (!timer.IsHighResolution)
            Console.WriteLine("[pacing] WARNING: high-resolution timer unavailable, falling back to Sleep()");
        int fps = _config.Fps;
        long ticksPerFrame = Stopwatch.Frequency / fps;
        int graceMs = Math.Max(1000 / fps * 2 / 5, 2);
        long next = Stopwatch.GetTimestamp() + ticksPerFrame;
        long lastVersion = 0;
        bool videoEpochProduced = false;
        long freshCount = 0, dupCount = 0, pacingSlips = 0, lastCompositorFrames = 0;
        long lastReport = Stopwatch.GetTimestamp();
        long reportInterval = Stopwatch.Frequency * 2;

        while (!ct.IsCancellationRequested)
        {
            _pacingStage = "waiting-for-sample-time";
            timer.WaitUntil(next);

            _pacingStage = "supervising-pipeline";
            if (ffmpeg.HasExited || writer.Failed)
            {
                Console.Error.WriteLine($"[encoder] ffmpeg exited unexpectedly (code {ffmpeg.ExitCode}); stopping.");
                return $"encoder exited unexpectedly (code {ffmpeg.ExitCode})";
            }
            if (capture.CaptureError is not null)
            {
                return $"capture failed mid-stream: {capture.CaptureError.Message}";
            }
            // Supervise the background halves: a dead splitter otherwise shows up
            // later as a bogus "encoder stall", a dead server as a silent stream.
            if (splitterTask.IsFaulted)
            {
                Console.Error.WriteLine($"[mp4] splitter failed: {splitterTask.Exception?.GetBaseException().Message}");
                return "mp4 splitter failed; see log";
            }
            if (serverTask.IsFaulted)
            {
                Console.Error.WriteLine($"[http] server failed: {serverTask.Exception?.GetBaseException().Message}");
                return "web server failed; see log";
            }

            _pacingStage = "waiting-for-capture";
            bool fresh = capture.WaitForFreshFrame(lastVersion, graceMs);
            lastVersion = capture.FrameVersion;

            byte[] buffer;
            _pacingStage = "waiting-for-frame-buffer";
            try { buffer = writer.RentBuffer(ct); }
            catch (OperationCanceledException) { break; }

            _pacingStage = "gpu-readback";
            bool frameReady = capture.TryReadFrame(buffer);
            // A stalled native readback can return after external salvage released
            // the downstream pipeline. Cancellation fences that late result off.
            if (ct.IsCancellationRequested) break;
            if (frameReady)
            {
                // Produce the A/V epoch at the first enqueue toward ffmpeg. The
                // audio continuation cannot construct capture until this succeeds.
                long enqueueTicks = videoEpochProduced ? 0 : Stopwatch.GetTimestamp();
                _pacingStage = "frame-enqueue";
                writer.Enqueue(buffer);
                if (!videoEpochProduced)
                {
                    _videoEpoch.TrySetResult(enqueueTicks);
                    videoEpochProduced = true;
                }
                if (fresh) freshCount++; else dupCount++;
            }
            else
            {
                writer.ReturnUnused(buffer);
            }
            _pacingStage = "sample-complete";

            next += ticksPerFrame;
            long now = Stopwatch.GetTimestamp();
            if (now > next + 5 * ticksPerFrame) { next = now + ticksPerFrame; pacingSlips++; }

            if (now - lastReport > reportInterval)
            {
                reportInterval = Stopwatch.Frequency * 10;
                long total = freshCount + dupCount;
                double dupPct = total > 0 ? dupCount * 100.0 / total : 0;
                double windowSec = (double)(now - lastReport) / Stopwatch.Frequency;
                broadcaster.SourceFps = (int)Math.Round((capture.FramesArrived - lastCompositorFrames) / windowSec);
                broadcaster.DupPercent = (int)Math.Round(dupPct);
                lastCompositorFrames = capture.FramesArrived;
                freshCount = dupCount = 0;
                lastReport = now;
            }
        }
        return "stopped";
    }

    /// <summary>Low/Medium/High bitrate options for an output size. Classified by
    /// pixel AREA, not height — a 1080x1871 portrait window has 1080p-class pixels
    /// and must not be billed as 4K just because it is tall. Boundaries are the
    /// midpoints between the 16:9 standard sizes, 30 fps gets two thirds (rounded
    /// to 0.5 Mbps), and High at 4K is the ceiling: nothing ever exceeds 35 Mbps.
    /// Public because the app's bitrate dropdown shows exactly these numbers.</summary>
    public static (int Low, int Medium, int High) BitrateTiers(int width, int height, int fps)
    {
        long px = (long)width * height;
        var (lo, med, hi) = px <= 1_400_000 ? (4000, 6000, 8000)        // 720p-class
                          : px <= 2_900_000 ? (8000, 12000, 16000)      // 1080p-class
                          : px <= 5_500_000 ? (12000, 18000, 24000)     // 1440p-class
                          : (18000, 25000, 35000);                      // 4K-class
        if (fps < 60)
        {
            static int Third(int k) => (int)Math.Round(k * 2.0 / 3 / 500) * 500;
            (lo, med, hi) = (Third(lo), Third(med), Third(hi));
        }
        return (lo, med, hi);
    }

    /// <summary>What BitrateKbps=0 resolves to: the Medium tier for the real output size.</summary>
    public static int AutoBitrate(int width, int height, int fps) => BitrateTiers(width, height, fps).Medium;

    /// <summary>Owns one pipeline resource and records the exact disposal boundary.
    /// A blocked Dispose leaves the stage at "stopping X" for the deadline log.</summary>
    private SalvageableCleanup TrackCleanup(Action cleanup, string name)
    {
        return new SalvageableCleanup(() =>
        {
            SetTeardownStage($"stopping {name}");
            try { cleanup(); }
            finally { SetTeardownStage($"{name} stopped"); }
        });
    }

    /// <summary>True iff <paramref name="ip"/> is a Tailscale CGNAT address
    /// (100.64.0.0/10): a dotted quad whose first octet is 100 and second octet
    /// 64..127. These are in scope in every firewall config, so a Tailscale
    /// address is always safe to advertise.</summary>
    public static bool IsTailscaleAddress(string ip)
    {
        string[] parts = ip.Split('.');
        return parts.Length == 4
            && byte.TryParse(parts[0], out byte a) && a == 100
            && byte.TryParse(parts[1], out byte b) && b >= 64 && b <= 127
            && byte.TryParse(parts[2], out _)
            && byte.TryParse(parts[3], out _);
    }

    /// <summary>Shareable IPv4s, best first. Ranks by the owning adapter, not just
    /// the address range: an active Tailscale interface wins, then physical private
    /// LAN adapters with a default route. Hyper-V/WSL/Docker/VM adapters and
    /// link-local addresses are excluded entirely — copying one of those produced
    /// links that worked for the streamer and nobody else. When
    /// <paramref name="includeLan"/> is false, only Tailscale addresses are
    /// returned (LAN links aren't reachable unless LAN access was applied).</summary>
    public static List<string> GetShareAddresses(bool includeLan = true)
    {
        var ranked = new List<(int Rank, string Addr)>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                string label = $"{nic.Name} {nic.Description}";
                bool isTailscale = label.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
                bool isVirtual =
                    label.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                    label.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

                var props = nic.GetIPProperties();
                bool hasGateway = props.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local
                    bool cgnat = b[0] == 100 && b[1] >= 64 && b[1] <= 127; // Tailscale range
                    bool privateRange = b[0] == 10 || (b[0] == 192 && b[1] == 168) ||
                                        (b[0] == 172 && b[1] >= 16 && b[1] <= 31);

                    // Only ranges viewers can actually reach: the firewall rule
                    // admits Tailscale + private LAN, so a Radmin-VPN-style 26.x
                    // or public address would be a dead link.
                    if (!cgnat && !privateRange) continue;
                    if (isVirtual && !isTailscale && !cgnat) continue;
                    int rank = isTailscale || cgnat ? 0
                             : hasGateway ? 1
                             : 2;
                    ranked.Add((rank, ua.Address.ToString()));
                }
            }
        }
        catch { }

        if (ranked.Count > 0)
        {
            var result = ranked.OrderBy(r => r.Rank).Select(r => r.Addr).Distinct().ToList();
            return includeLan ? result : result.Where(IsTailscaleAddress).ToList();
        }

        // Fallback: the old DNS-based list, better than handing out nothing.
        try
        {
            var result = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
            return includeLan ? result : result.Where(IsTailscaleAddress).ToList();
        }
        catch { return []; }
    }
}
