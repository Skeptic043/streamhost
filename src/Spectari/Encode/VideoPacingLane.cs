using System.Diagnostics;
using Spectari.Capture;
using Spectari.Server;
using Spectari.Util;

namespace Spectari.Encode;

internal interface IVideoPacingLane
{
    string Run(CancellationToken cancellationToken);
}

internal static class VideoPacingLaneFactory
{
    internal static IVideoPacingLane Select(
        VideoPipelinePlan plan,
        IVideoPacingLane rawVideoLane,
        Func<IVideoPacingLane>? hardwareLaneFactory) => plan.Lane switch
        {
            VideoInputLane.RawVideo => rawVideoLane,
            VideoInputLane.GpuTexture when hardwareLaneFactory is not null => hardwareLaneFactory(),
            VideoInputLane.GpuTexture => throw new InvalidOperationException(
                "GPU texture lane selected without an initialized hardware encoder."),
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };
}

/// <summary>The existing BGRA readback and ffmpeg stdin lane.</summary>
internal sealed class RawVideoPacingLane : IVideoPacingLane
{
    private readonly ICaptureSource _capture;
    private readonly FfmpegEncoder _ffmpeg;
    private readonly FrameWriter _writer;
    private readonly Broadcaster _broadcaster;
    private readonly Task _splitterTask;
    private readonly Task _serverTask;
    private readonly int _framesPerSecond;
    private readonly TaskCompletionSource<long> _videoEpoch;
    private readonly Action<string> _setStage;

    internal RawVideoPacingLane(
        ICaptureSource capture,
        FfmpegEncoder ffmpeg,
        FrameWriter writer,
        Broadcaster broadcaster,
        Task splitterTask,
        Task serverTask,
        int framesPerSecond,
        TaskCompletionSource<long> videoEpoch,
        Action<string> setStage)
    {
        _capture = capture;
        _ffmpeg = ffmpeg;
        _writer = writer;
        _broadcaster = broadcaster;
        _splitterTask = splitterTask;
        _serverTask = serverTask;
        _framesPerSecond = framesPerSecond;
        _videoEpoch = videoEpoch;
        _setStage = setStage;
    }

    public string Run(CancellationToken cancellationToken)
    {
        using var timer = new HighResTimer();
        if (!timer.IsHighResolution)
            Console.WriteLine("[pacing] WARNING: high-resolution timer unavailable, falling back to Sleep()");
        long ticksPerFrame = Stopwatch.Frequency / _framesPerSecond;
        int graceMs = Math.Max(1000 / _framesPerSecond * 2 / 5, 2);
        long next = Stopwatch.GetTimestamp() + ticksPerFrame;
        long lastVersion = 0;
        bool videoEpochProduced = false;
        long freshCount = 0, dupCount = 0, pacingSlips = 0, lastCompositorFrames = 0;
        long lastReport = Stopwatch.GetTimestamp();
        long reportInterval = Stopwatch.Frequency * 2;

        while (!cancellationToken.IsCancellationRequested)
        {
            _broadcaster.WaitingForWindow = _capture is WindowReattachCapture
            {
                WaitingForWindow: true,
            };
            _setStage("waiting-for-sample-time");
            timer.WaitUntil(next);

            _setStage("supervising-pipeline");
            if (_ffmpeg.HasExited || _writer.Failed)
            {
                Console.Error.WriteLine(
                    $"[encoder] ffmpeg exited unexpectedly (code {_ffmpeg.ExitCode}); stopping.");
                return $"encoder exited unexpectedly (code {_ffmpeg.ExitCode})";
            }
            if (_capture.CaptureError is not null)
                return $"capture failed mid-stream: {_capture.CaptureError.Message}";
            if (_splitterTask.IsFaulted)
            {
                Console.Error.WriteLine(
                    $"[mp4] splitter failed: {_splitterTask.Exception?.GetBaseException().Message}");
                return "mp4 splitter failed; see log";
            }
            if (_serverTask.IsFaulted)
            {
                Console.Error.WriteLine(
                    $"[http] server failed: {_serverTask.Exception?.GetBaseException().Message}");
                return "web server failed; see log";
            }

            _setStage("waiting-for-capture");
            bool fresh = _capture.WaitForFreshFrame(lastVersion, graceMs);
            lastVersion = _capture.FrameVersion;

            byte[] buffer;
            _setStage("waiting-for-frame-buffer");
            try { buffer = _writer.RentBuffer(cancellationToken); }
            catch (OperationCanceledException) { break; }

            _setStage("gpu-readback");
            bool frameReady = _capture.TryReadFrame(buffer);
            if (cancellationToken.IsCancellationRequested) break;
            if (frameReady)
            {
                long enqueueTicks = videoEpochProduced ? 0 : Stopwatch.GetTimestamp();
                _setStage("frame-enqueue");
                _writer.Enqueue(buffer);
                if (!videoEpochProduced)
                {
                    _videoEpoch.TrySetResult(enqueueTicks);
                    videoEpochProduced = true;
                }
                if (fresh) freshCount++; else dupCount++;
            }
            else
            {
                _writer.ReturnUnused(buffer);
            }
            _setStage("sample-complete");

            next += ticksPerFrame;
            long now = Stopwatch.GetTimestamp();
            if (now > next + 5 * ticksPerFrame)
            {
                next = now + ticksPerFrame;
                pacingSlips++;
            }

            if (now - lastReport > reportInterval)
            {
                reportInterval = Stopwatch.Frequency * 10;
                long total = freshCount + dupCount;
                double dupPct = total > 0 ? dupCount * 100.0 / total : 0;
                double windowSec = (double)(now - lastReport) / Stopwatch.Frequency;
                _broadcaster.SourceFps = (int)Math.Round(
                    (_capture.FramesArrived - lastCompositorFrames) / windowSec);
                _broadcaster.DupPercent = (int)Math.Round(dupPct);
                lastCompositorFrames = _capture.FramesArrived;
                freshCount = dupCount = 0;
                lastReport = now;
            }
        }
        return "stopped";
    }
}

internal interface IEncodedAccessUnitSink
{
    void Write(IReadOnlyList<EncodedAccessUnit> accessUnits);
}

/// <summary>
/// Owns texture-lane fixed-rate submission, debt repayment, and the first-submission
/// epoch while encoder and output implementations stay swappable.
/// </summary>
internal sealed class HardwareVideoPacingLane : IVideoPacingLane
{
    private readonly ICaptureSource _capture;
    private readonly IGpuTextureCaptureSource _gpuCapture;
    private readonly Nv12FrameConverter _converter;
    private readonly IHardwareVideoEncoder _encoder;
    private readonly IEncodedAccessUnitSink _output;
    private readonly IVideoInputWriter _writer;
    private readonly FfmpegEncoder _ffmpeg;
    private readonly Broadcaster _broadcaster;
    private readonly Task _splitterTask;
    private readonly Task _serverTask;
    private readonly HardwareFrameTickPolicy _ticks;
    private readonly int _framesPerSecond;
    private readonly TaskCompletionSource<long> _videoEpoch;
    private readonly Action<string> _setStage;

    internal HardwareVideoPacingLane(
        ICaptureSource capture,
        IGpuTextureCaptureSource gpuCapture,
        Nv12FrameConverter converter,
        IHardwareVideoEncoder encoder,
        IEncodedAccessUnitSink output,
        IVideoInputWriter writer,
        FfmpegEncoder ffmpeg,
        Broadcaster broadcaster,
        Task splitterTask,
        Task serverTask,
        int framesPerSecond,
        TaskCompletionSource<long> videoEpoch,
        Action<string> setStage)
    {
        _capture = capture;
        _gpuCapture = gpuCapture;
        _converter = converter;
        _encoder = encoder;
        _output = output;
        _writer = writer;
        _ffmpeg = ffmpeg;
        _broadcaster = broadcaster;
        _splitterTask = splitterTask;
        _serverTask = serverTask;
        _ticks = new HardwareFrameTickPolicy(framesPerSecond);
        _framesPerSecond = framesPerSecond;
        _videoEpoch = videoEpoch;
        _setStage = setStage;
    }

    public string Run(CancellationToken cancellationToken)
    {
        using var timer = new HighResTimer();
        long pacingTicks = Math.Max(1, Stopwatch.Frequency / _framesPerSecond);
        long frameDuration100ns = Math.Max(1, TimeSpan.TicksPerSecond / _framesPerSecond);
        long next = Stopwatch.GetTimestamp() + pacingTicks;
        long submittedFrames = 0;
        long encodedUnits = 0;
        long freshFrames = 0;
        long duplicateFrames = 0;
        long lastVersion = _capture.FrameVersion;
        long lastCaptureFrames = _capture.FramesArrived;
        long lastReport = Stopwatch.GetTimestamp();
        long reportInterval = Stopwatch.Frequency * 2;
        long lastDebtLog = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            _broadcaster.WaitingForWindow = _capture is WindowReattachCapture
            {
                WaitingForWindow: true,
            };
            _setStage("waiting-for-sample-time");
            timer.WaitUntil(next);
            next += pacingTicks;

            _setStage("supervising-pipeline");
            string? failure = SupervisePipeline();
            if (failure is not null) return RuntimeFailure(failure);

            long currentVersion = _capture.FrameVersion;
            bool fresh = currentVersion != lastVersion;
            lastVersion = currentVersion;

            _setStage("gpu-scale-convert");
            if (!_converter.TryConvert(_gpuCapture, out VideoFrameLease? current, out Nv12ConvertFailure convertFailure))
            {
                FrameDebtSnapshot debt = _ticks.RecordUnavailableTick();
                long debtNow = Stopwatch.GetTimestamp();
                if (debtNow - lastDebtLog >= Stopwatch.Frequency || debt.StallSignal)
                {
                    Console.Error.WriteLine(
                        $"[gpu-encode] frame debt: {debt.DebtFrames} frames, sync error {debt.SyncError.TotalMilliseconds:F1} ms ({convertFailure}).");
                    lastDebtLog = debtNow;
                }
                if (debt.StallSignal)
                    return RuntimeFailure(
                        $"sustained frame debt reached {debt.DebtFrames} frames after {convertFailure}",
                        HardwareFallbackKind.SustainedFrameDebt);
                continue;
            }

            VideoFrameLease? duplicate = null;
            bool repay = _ticks.CurrentDebt.DebtFrames > 0 &&
                _converter.TryDuplicate(current!, out duplicate);
            HardwareFrameTickPlan plan = _ticks.PlanAvailableTick(repay);
            VideoFrameLease[] submissions = plan.DuplicateSubmissions == 1
                ? [current!, duplicate!]
                : [current!];
            if (fresh) freshFrames++;
            else duplicateFrames++;
            duplicateFrames += plan.DuplicateSubmissions;

            for (int submissionIndex = 0; submissionIndex < submissions.Length; submissionIndex++)
            {
                VideoFrameLease frame = submissions[submissionIndex];
                try
                {
                    _setStage("hardware-encode");
                    IReadOnlyList<EncodedAccessUnit> output = _encoder.Encode(
                        frame,
                        submittedFrames * frameDuration100ns,
                        frameDuration100ns);
                    if (_ticks.ConfirmEncoderSubmission(_encoder.SubmittedFrameCount > 0))
                        _videoEpoch.TrySetResult(Stopwatch.GetTimestamp());
                    submittedFrames++;
                    encodedUnits += output.Count;
                    if (output.Count > 0)
                    {
                        _setStage("access-unit-output");
                        _output.Write(output);
                    }
                }
                catch
                {
                    for (int pendingIndex = submissionIndex + 1;
                        pendingIndex < submissions.Length;
                        pendingIndex++)
                    {
                        submissions[pendingIndex].Return(FrameLeaseReturnReason.Failure);
                    }
                    throw;
                }
            }
            _setStage("sample-complete");

            long now = Stopwatch.GetTimestamp();
            if (now > next + 5 * pacingTicks)
                next = now + pacingTicks;

            if (now - lastReport > reportInterval)
            {
                double seconds = (now - lastReport) / (double)Stopwatch.Frequency;
                double encodeFps = (freshFrames + duplicateFrames) / seconds;
                long totalFrames = freshFrames + duplicateFrames;
                double duplicatePercent = totalFrames == 0
                    ? 0
                    : duplicateFrames * 100.0 / totalFrames;
                _broadcaster.SourceFps = (int)Math.Round(
                    (_capture.FramesArrived - lastCaptureFrames) / seconds);
                _broadcaster.DupPercent = (int)Math.Round(duplicatePercent);
                Console.WriteLine(
                    $"[gpu-encode] encode delivery: {encodeFps:F1} fps, {encodedUnits} access units, debt {plan.Debt.DebtFrames} frames.");
                lastCaptureFrames = _capture.FramesArrived;
                freshFrames = duplicateFrames = encodedUnits = 0;
                lastReport = now;
                reportInterval = Stopwatch.Frequency * 10;
            }
        }

        _setStage("hardware-drain");
        IReadOnlyList<EncodedAccessUnit> drained = _encoder.Drain();
        if (drained.Count > 0)
            _output.Write(drained);
        return "stopped";
    }

    private string? SupervisePipeline()
    {
        if (_ffmpeg.HasExited || _writer.Failed)
            return $"ffmpeg exited unexpectedly (code {_ffmpeg.ExitCode})";
        if (_capture.CaptureError is not null)
            return $"capture failed mid-stream: {_capture.CaptureError.Message}";
        if (_splitterTask.IsFaulted)
            return $"mp4 splitter failed: {_splitterTask.Exception?.GetBaseException().Message}";
        if (_serverTask.IsFaulted)
            return $"web server failed: {_serverTask.Exception?.GetBaseException().Message}";
        return null;
    }

    private static string RuntimeFailure(
        string reason,
        HardwareFallbackKind kind = HardwareFallbackKind.RuntimeFailure)
    {
        HardwareFallbackDecision decision = HardwareFallbackClassifier.Runtime(kind, reason);
        Console.Error.WriteLine($"[gpu-encode] {decision.Reason}");
        return decision.Reason;
    }
}
