using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectari.Audio;
using Spectari.Capture;
using Spectari.Encode;
using Spectari.Mp4;
using Spectari.Server;
using Spectari.Util;

namespace Spectari;

public sealed record SessionConfig
{
    public IntPtr MonitorHandle { get; init; }
    public IntPtr WindowHandle { get; init; }
    public string WindowProcessName { get; init; } = "";
    public uint WindowProcessId { get; init; }
    public string CaptureDeviceSymbolicLink { get; init; } = "";
    public string SourceName { get; init; } = "";

    /// <summary>Display name shown to viewers (grid tiles, viewer tab title).
    /// Empty = fall back to the machine name.</summary>
    public string StreamName { get; init; } = "";
    public uint AudioPid { get; init; }
    public bool CaptureDesktopAudio { get; init; }
    public string AudioInputDeviceId { get; init; } = "";
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
        // standard capture as fallback and for window shares - see
        // AutoMonitorCapture). --compat-capture forces duplication-only,
        // kept for diagnostics.
        bool captureDeviceSelected = !string.IsNullOrEmpty(_config.CaptureDeviceSymbolicLink);
        ICaptureSource capture;
        try
        {
            capture = captureDeviceSelected
                ? new MediaFoundationCapture(_config.CaptureDeviceSymbolicLink)
                : _config.WindowHandle != IntPtr.Zero
                    ? WindowReattachCapture.Create(
                        _config.WindowHandle,
                        _config.WindowProcessName,
                        _config.WindowProcessId)
                    : _config.CompatibilityCapture
                        ? new DuplicationCapture(_config.MonitorHandle)
                        : new AutoMonitorCapture(_config.MonitorHandle);
        }
        catch (CaptureTargetUnavailableException ex)
        {
            Console.Error.WriteLine($"[capture] {ex.Message}");
            return ex.Message;
        }
        using var captureLifetime = TrackCleanup(capture.Dispose, "capture");
        _captureAdapter = new(capture.GpuVendorId, capture.AdapterLuid, capture.DriverVersion);
        if (_config.NoCursor) capture.CursorEnabled = false;
        Console.WriteLine($"[capture] selected source @ {capture.Width}x{capture.Height}, GPU vendor 0x{capture.GpuVendorId:X4}{(_config.NoCursor ? ", cursor off" : "")}");
        int fps = capture.FrameRate ?? _config.Fps;

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

        var sourceAdapter = new EncoderAdapterIdentity(
            capture.GpuVendorId,
            capture.AdapterLuid,
            capture.DriverVersion);
        RawVideoEncoderSelection rawVideo = EncoderAdapterResolver.Resolve(
            capture,
            captureDeviceSelected,
            _config.Encoder);
        string encoder = rawVideo.Encoder;
        ActiveEncoder = encoder;
        OutputWidth = outW;
        OutputHeight = outH;

        // 0 = auto: scale bitrate with what actually goes out. A "Native" preset
        // used to hardcode 12 Mbps even when native meant 1440p or 4K.
        int bitrateKbps = _config.BitrateKbps > 0 ? _config.BitrateKbps : AutoBitrate(outW, outH, fps);
        if (_config.BitrateKbps <= 0)
            Console.WriteLine($"[encoder] auto bitrate for {outH}p{fps}: {bitrateKbps} kbps");

        IReadOnlyList<EncoderAdapterIdentity> hardwareAdapters =
            HardwareVideoLanePolicy.NeedsAdapterEnumeration(_config.Encoder, sourceAdapter)
                ? EncoderAdapterResolver.EnumerateHardwareAdapters()
                : [rawVideo.Adapter, sourceAdapter];
        var hardwareParameters = HardwareVideoEncoderParameters.FromSession(
            outW,
            outH,
            fps,
            bitrateKbps);
        using var hardwareEncoder = new UnavailableHardwareVideoEncoder();
        VideoPipelinePlan videoPlan = HardwareVideoLanePolicy.Select(
            _config.Encoder,
            rawVideo,
            sourceAdapter,
            capture is IGpuTextureCaptureSource,
            hardwareAdapters,
            hardwareParameters,
            hardwareEncoder.Probe);

        AudioPipeline? audioPipeline = AudioPipeline.Create(
            _config.AudioPid,
            _config.CaptureDesktopAudio,
            _config.AudioInputDeviceId,
            _config.Port);
        string? audioPipeName = audioPipeline?.PipeName;

        // Declared before the downstream lifetimes so audio stops after they do.
        // Its cancellation prevents delayed startup from surviving an early exit.
        using var audioTeardown = TrackCleanup(() => audioPipeline?.Dispose(), "audio");

        var ffmpeg = new FfmpegEncoder(capture.Width, capture.Height, fps,
            bitrateKbps, outW, outH, encoder, audioPipeName, _config.FragMs);
        using var ffmpegLifetime = TrackCleanup(ffmpeg.Dispose, "ffmpeg");

        audioPipeline?.Start(_videoEpoch.Task, ct);

        var broadcaster = new Broadcaster
        {
            Width = outW,
            Height = outH,
            Fps = fps,
            MaxViewers = _config.MaxViewers,
            HasAudio = audioPipeline is not null,
            StreamName = string.IsNullOrWhiteSpace(_config.StreamName)
                ? Environment.MachineName : _config.StreamName.Trim(),
        };
        broadcaster.WaitingForWindow = capture is WindowReattachCapture
        {
            WaitingForWindow: true,
        };
        Broadcaster = broadcaster;
        var splitterTask = Task.Run(() => Mp4Splitter.RunAsync(ffmpeg.Output, broadcaster, ct), ct);

        var server = new WebServer(_config.Port, broadcaster, _config.ViewKey);
        using var serverLifetime = TrackCleanup(server.Dispose, "web server");
        var serverTask = Task.Run(() => server.RunAsync(ct), ct);
        LocalOnly = server.BoundPrefix.Contains("localhost");
        if (LocalOnly)
            Console.Error.WriteLine($"[http] THIS PC ONLY: remote viewers will get HTTP 400. Run setup.bat {_config.Port} as administrator, then restart the stream.");

        // A source that cannot produce its first frame must fail before the server
        // reports live, so a disconnected device or broken desktop backend is visible.
        Description = $"{outW}x{outH}@{fps} ~{bitrateKbps}kbps";
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
                Console.Error.WriteLine($"[capture] backend started but never delivered a frame; adapter: {capture.AdapterName}.");
                Console.Error.WriteLine(captureDeviceSelected
                    ? "[capture] worth trying: reconnect the device, close other apps using it, or pick another device."
                    : "[capture] worth trying: a different source, the compatibility capture option, or a GPU driver update.");
                Console.Error.WriteLine("[capture] when reporting this, use Copy log in the app; the log file path is printed at startup.");
                return captureDeviceSelected
                    ? "no frames from capture device; see log"
                    : "no frames from screen capture; see log";
            }
            Thread.Sleep(100);
        }

        broadcaster.State = "live";
        WentLive = true;
        Console.WriteLine($"[ready] first frame captured; streaming {Description} via {encoder}");
        if (ConsoleMirror.ShowViewerLinksInConsole)
        {
            ConsoleMirror.WriteTransientLine($"[ready] watch at: {ShareLinkResolver.BuildViewerUrl("localhost", _config.Port, "", _config.ViewKey)}");
            var shareAddrs = ShareLinkResolver.GetShareAddresses();
            foreach (var ip in shareAddrs.Where(ShareLinkResolver.IsTailscaleAddress))
                ConsoleMirror.WriteTransientLine($"[ready]           {ShareLinkResolver.BuildViewerUrl(ip, _config.Port, "", _config.ViewKey)}");
            var lanAddrs = shareAddrs.Where(ip => !ShareLinkResolver.IsTailscaleAddress(ip)).ToList();
            if (lanAddrs.Count > 0)
                ConsoleMirror.WriteTransientLine("[ready] the LAN links below only work if you allowed LAN access (setup.bat or Open port with Allow LAN):");
            foreach (var ip in lanAddrs)
                ConsoleMirror.WriteTransientLine($"[ready]           {ShareLinkResolver.BuildViewerUrl(ip, _config.Port, "", _config.ViewKey)}");
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
            // seconds - the old fixed window (counted from the first captured
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
            IVideoPacingLane pacingLane = VideoPacingLaneFactory.Select(
                videoPlan,
                new RawVideoPacingLane(
                    capture,
                    ffmpeg,
                    writer,
                    broadcaster,
                    splitterTask,
                    serverTask,
                    fps,
                    _videoEpoch,
                    stage => _pacingStage = stage),
                hardwareLaneFactory: null);
            exitReason = pacingLane.Run(ct);
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

    /// <summary>Low/Medium/High bitrate options for an output size. Classified by
    /// pixel AREA, not height - a 1080x1871 portrait window has 1080p-class pixels
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

}
