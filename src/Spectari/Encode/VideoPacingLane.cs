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
    private readonly IGpuTextureCaptureSource _capture;
    private readonly Nv12FrameConverter _converter;
    private readonly IHardwareVideoEncoder _encoder;
    private readonly IEncodedAccessUnitSink _output;
    private readonly FrameDebtPolicy _debt;
    private readonly int _framesPerSecond;
    private readonly TaskCompletionSource<long> _videoEpoch;
    private readonly Action<string> _setStage;

    internal HardwareVideoPacingLane(
        IGpuTextureCaptureSource capture,
        Nv12FrameConverter converter,
        IHardwareVideoEncoder encoder,
        IEncodedAccessUnitSink output,
        int framesPerSecond,
        TaskCompletionSource<long> videoEpoch,
        Action<string> setStage)
    {
        _capture = capture;
        _converter = converter;
        _encoder = encoder;
        _output = output;
        _debt = new FrameDebtPolicy(framesPerSecond);
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
        bool epochProduced = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            _setStage("waiting-for-sample-time");
            timer.WaitUntil(next);
            next += pacingTicks;

            _setStage("gpu-scale-convert");
            if (!_converter.TryConvert(_capture, out VideoFrameLease? current, out _))
            {
                _debt.RecordDroppedTick();
                continue;
            }

            VideoFrameLease? duplicate = null;
            bool repay = _debt.Current.DebtFrames > 0 &&
                _converter.TryDuplicate(current!, out duplicate);
            FrameSubmissionPlan plan = _debt.PlanAvailableTick(repay);
            VideoFrameLease[] submissions = plan.SubmissionCount == 2
                ? [current!, duplicate!]
                : [current!];

            foreach (VideoFrameLease frame in submissions)
            {
                if (!epochProduced)
                {
                    _videoEpoch.TrySetResult(Stopwatch.GetTimestamp());
                    epochProduced = true;
                }

                try
                {
                    _setStage("hardware-encode");
                    IReadOnlyList<EncodedAccessUnit> output = _encoder.Encode(
                        frame,
                        submittedFrames * frameDuration100ns,
                        frameDuration100ns);
                    submittedFrames++;
                    if (output.Count > 0)
                    {
                        _setStage("access-unit-output");
                        _output.Write(output);
                    }
                }
                catch
                {
                    frame.Return(FrameLeaseReturnReason.Failure);
                    throw;
                }
            }
            _setStage("sample-complete");

            long now = Stopwatch.GetTimestamp();
            if (now > next + 5 * pacingTicks)
                next = now + pacingTicks;
        }

        _setStage("hardware-drain");
        IReadOnlyList<EncodedAccessUnit> drained = _encoder.Drain();
        if (drained.Count > 0)
            _output.Write(drained);
        return "stopped";
    }
}
