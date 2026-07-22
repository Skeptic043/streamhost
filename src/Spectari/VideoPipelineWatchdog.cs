using System.Diagnostics;
using Spectari.Capture;
using Spectari.Encode;
using Spectari.Server;

namespace Spectari;

/// <summary>Owns pipeline progress accounting and stalled-session recovery.</summary>
internal sealed class VideoPipelineWatchdog
{
    private readonly ICaptureSource _capture;
    private readonly IVideoInputWriter _writer;
    private readonly Broadcaster _broadcaster;
    private readonly FfmpegEncoder _ffmpeg;
    private readonly PipelineStallExitCoordinator _stallExit;
    private readonly IHardwareVideoEncoder _hardwareEncoder;
    private readonly Nv12FrameConverter? _hardwareConverter;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<string> _pacingStage;
    private readonly Func<string> _teardownStage;

    internal VideoPipelineWatchdog(
        ICaptureSource capture,
        IVideoInputWriter writer,
        Broadcaster broadcaster,
        FfmpegEncoder ffmpeg,
        PipelineStallExitCoordinator stallExit,
        IHardwareVideoEncoder hardwareEncoder,
        Nv12FrameConverter? hardwareConverter,
        CancellationToken cancellationToken,
        Func<string> pacingStage,
        Func<string> teardownStage)
    {
        _capture = capture;
        _writer = writer;
        _broadcaster = broadcaster;
        _ffmpeg = ffmpeg;
        _stallExit = stallExit;
        _hardwareEncoder = hardwareEncoder;
        _hardwareConverter = hardwareConverter;
        _cancellationToken = cancellationToken;
        _pacingStage = pacingStage;
        _teardownStage = teardownStage;
    }

    internal void Start()
    {
        new Thread(Run)
        { IsBackground = true, Name = "encoder-watchdog" }.Start();
    }

    private void Run()
    {
        CancellationToken cancellationToken = _cancellationToken;
        PipelineBaseline baseline = TakeBaseline();
        if (!_broadcaster.WaitForInit(TimeSpan.FromSeconds(10), cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            ReportStall(
                baseline,
                $"[pipeline] ffmpeg produced no init segment in 10s while using {_ffmpeg.EncoderName}; the live video pipeline is stalled.");
            return;
        }

        baseline = TakeBaseline();
        bool firstWindow = true;
        while (!cancellationToken.WaitHandle.WaitOne(5000))
        {
            long total = Interlocked.Read(ref _broadcaster.FragmentsSent);
            long produced = total - baseline.Fragments;
            if (firstWindow ? produced < 5 : produced == 0)
            {
                ReportStall(
                    baseline,
                    firstWindow
                        ? $"[pipeline] ffmpeg wrote the init segment but produced fewer than five fragments in 5s while using {_ffmpeg.EncoderName}; the live video pipeline is stalled."
                        : $"[pipeline] ffmpeg output stopped for 5s while using {_ffmpeg.EncoderName}; the live video pipeline is stalled.");
                return;
            }
            firstWindow = false;
            baseline = TakeBaseline();
        }
    }

    private void ReportStall(PipelineBaseline baseline, string diagnostic)
    {
        Console.Error.WriteLine(diagnostic);

        VideoInputWriterProgress currentWriter = _writer.GetProgressSnapshot();
        long now = Stopwatch.GetTimestamp();
        long completedInputs = currentWriter.WritesCompleted - baseline.Writer.WritesCompleted;
        long completedHardwareInputs =
            _hardwareEncoder.SubmittedFrameCount - baseline.EncoderSubmissions;
        bool sustainedInput = completedInputs >= 5 || completedHardwareInputs >= 5;
        bool inputWriteBlocked = currentWriter.WriteInProgress &&
            now - currentWriter.LastWriteStartedTicks >= Stopwatch.Frequency;
        VideoPipelineStallDecision decision = VideoPipelineStallPolicy.Classify(
            sustainedInput,
            inputWriteBlocked);

        LogProgress(baseline);
        Console.Error.WriteLine(decision.ConfirmedEncoderOrOutputFailure
            ? "[pipeline] sustained MF or ffmpeg input activity, or a blocked stdin write, places the stall in the encoder/output path; the cached hardware verdict is no longer trusted."
            : "[pipeline] video input did not advance continuously during the watchdog window; this is an upstream capture, conversion, or pacing stall, not a confirmed encoder failure.");

        if (!decision.PermitCpuRecovery)
        {
            Console.Error.WriteLine(
                "[pipeline] CPU recovery skipped because the stall is upstream of the encoder/output path.");
        }
        else if (_ffmpeg.CopiesH264Video || _ffmpeg.EncoderName != "libx264")
        {
            Console.Error.WriteLine(
                "[pipeline] Starting a one-time CPU recovery session (libx264); capture and encoder state will be recreated.");
            if (!_ffmpeg.CopiesH264Video)
                FfmpegEncoder.InvalidateProbeCache();
        }
        else
        {
            Console.Error.WriteLine(
                "[pipeline] CPU recovery is already active; stopping with the stage reason above.");
        }

        _stallExit.Begin(decision.StopReason, FormatActiveStages);
    }

    private PipelineBaseline TakeBaseline() => new(
        Stopwatch.GetTimestamp(),
        _capture is ICaptureDiagnostics diagnostics ? diagnostics.GetProgressSnapshot() : null,
        _writer.GetProgressSnapshot(),
        _hardwareEncoder.SubmittedFrameCount,
        _hardwareConverter?.PoolAccounting,
        Interlocked.Read(ref _broadcaster.FragmentsSent));

    private void LogProgress(PipelineBaseline baseline)
    {
        long now = Stopwatch.GetTimestamp();
        VideoInputWriterProgress currentWriter = _writer.GetProgressSnapshot();
        CaptureProgressSnapshot? currentCapture = _capture is ICaptureDiagnostics diagnostics
            ? diagnostics.GetProgressSnapshot()
            : null;
        string captureDelta = currentCapture is { } current && baseline.Capture is { } prior
            ? $"capture-arrival +{current.CallbacksStarted - prior.CallbacksStarted}, gpu-frame-ready +{current.FramesReady - prior.FramesReady}, gpu-readback-start +{current.ReadbacksStarted - prior.ReadbacksStarted}, gpu-readback-complete +{current.ReadbacksCompleted - prior.ReadbacksCompleted}, "
            : "capture-stage counters unavailable for this backend, ";
        long fragments = Interlocked.Read(ref _broadcaster.FragmentsSent);
        FrameLeaseAccounting? pool = _hardwareConverter?.PoolAccounting;
        string hardwareDelta = pool is { } currentPool && baseline.Pool is { } priorPool
            ? $"mf-input +{_hardwareEncoder.SubmittedFrameCount - baseline.EncoderSubmissions}, nv12-rent +{currentPool.TotalRents - priorPool.TotalRents}, nv12-outstanding {currentPool.Outstanding}, "
            : "";
        double elapsed = Math.Max(0, now - baseline.StartedTicks) /
            (double)Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[pipeline] progress over {elapsed:F1}s: {captureDelta}{hardwareDelta}frame-enqueue +{currentWriter.FramesEnqueued - baseline.Writer.FramesEnqueued}, ffmpeg-stdin-start +{currentWriter.WritesStarted - baseline.Writer.WritesStarted}, ffmpeg-stdin-complete +{currentWriter.WritesCompleted - baseline.Writer.WritesCompleted}, ffmpeg-fragment +{fragments - baseline.Fragments}.");

        string captureAges = currentCapture is { } captureProgress
            ? $"capture-arrival {ProgressAge(captureProgress.LastCallbackTicks, now)}, gpu-frame-ready {ProgressAge(captureProgress.LastFrameReadyTicks, now)}, gpu-readback {ProgressAge(captureProgress.LastReadbackCompletedTicks, now)}, "
            : "";
        Console.Error.WriteLine(
            $"[pipeline] last completed: {captureAges}frame-enqueue {ProgressAge(currentWriter.LastEnqueueTicks, now)}, ffmpeg-stdin-write {ProgressAge(currentWriter.LastWriteCompletedTicks, now)}, ffmpeg-init {ProgressAge(_broadcaster.InitReadyTicks, now)}, ffmpeg-fragment {ProgressAge(_broadcaster.LastFragmentTicks, now)}.");
        Console.Error.WriteLine($"[pipeline] active stages: {FormatActiveStages()}");
    }

    private string FormatActiveStages()
    {
        long now = Stopwatch.GetTimestamp();
        CaptureProgressSnapshot? captureProgress = _capture is ICaptureDiagnostics diagnostics
            ? diagnostics.GetProgressSnapshot()
            : null;
        string captureStages = captureProgress is { } progress
            ? $"capture-callback={ActiveStage(progress.CallbackStage, progress.LastCallbackTicks, now)}, gpu-readback={ActiveStage(progress.ReadbackStage, progress.LastReadbackStartedTicks, now)}"
            : "capture-callback=unavailable, gpu-readback=unavailable";
        VideoInputWriterProgress writerProgress = _writer.GetProgressSnapshot();
        string inputStage = writerProgress.WriteInProgress ? "writing"
            : writerProgress.Failed ? "failed"
            : "idle";
        string hardwareStage = _hardwareConverter is null
            ? ""
            : $", mf-input={_hardwareEncoder.SubmittedFrameCount}, nv12-pool={_hardwareConverter.PoolAccounting.Outstanding}/{_hardwareConverter.PoolAccounting.Capacity}";
        return $"{captureStages}, pacing={_pacingStage()}{hardwareStage}, ffmpeg-stdin={inputStage}, teardown={_teardownStage()}";
    }

    private static string ActiveStage(string stage, long enteredTicks, long now) =>
        stage == "idle" ? "idle" : $"{stage} (entered {ProgressAge(enteredTicks, now)})";

    private static string ProgressAge(long then, long now)
    {
        if (then == 0) return "never";
        long ms = (long)(Math.Max(0, now - then) * 1000.0 / Stopwatch.Frequency);
        return ms < 1000 ? $"{ms}ms ago" : $"{ms / 1000.0:F1}s ago";
    }

    private readonly record struct PipelineBaseline(
        long StartedTicks,
        CaptureProgressSnapshot? Capture,
        VideoInputWriterProgress Writer,
        long EncoderSubmissions,
        FrameLeaseAccounting? Pool,
        long Fragments);
}
