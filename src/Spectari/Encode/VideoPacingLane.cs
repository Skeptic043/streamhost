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
        long freshCount = 0, dupCount = 0, pacingSlips = 0;
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
                    total / windowSec);
                _broadcaster.DupPercent = (int)Math.Round(dupPct);
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

/// <summary>Runs the GPU lane on one encoder worker and owns ordered shutdown.</summary>
internal sealed class HardwareVideoPacingLane : IVideoPacingLane
{
    private readonly ICaptureSource _capture;
    private readonly Nv12FrameConverter _converter;
    private readonly IHardwareVideoEncoder _encoder;
    private readonly IEncodedAccessUnitSink _output;
    private readonly IVideoInputWriter _writer;
    private readonly FfmpegEncoder _ffmpeg;
    private readonly Broadcaster _broadcaster;
    private readonly Task _splitterTask;
    private readonly Task _serverTask;
    private readonly int _framesPerSecond;
    private readonly TaskCompletionSource<long> _videoEpoch;
    private readonly Action<string> _setStage;
    private readonly Action<string> _runtimeFailure;

    internal HardwareVideoPacingLane(
        ICaptureSource capture,
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
        Action<string> setStage,
        Action<string> runtimeFailure)
    {
        _capture = capture;
        _converter = converter;
        _encoder = encoder;
        _output = output;
        _writer = writer;
        _ffmpeg = ffmpeg;
        _broadcaster = broadcaster;
        _splitterTask = splitterTask;
        _serverTask = serverTask;
        _framesPerSecond = framesPerSecond;
        _videoEpoch = videoEpoch;
        _setStage = setStage;
        _runtimeFailure = runtimeFailure;
    }

    public string Run(CancellationToken cancellationToken)
    {
        var capture = new CaptureHardwareEncodeAdapter(_capture);
        var converter = new Nv12HardwareEncodeConverter(_converter, _capture);
        var pullLoop = new HardwareEncoderPullLoop(
            capture,
            converter,
            _encoder,
            _output,
            _framesPerSecond,
            Stopwatch.Frequency,
            ticks => _videoEpoch.TrySetResult(ticks));
        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var workerWake = new AutoResetEvent(false);
        using var workerCompleted = new ManualResetEventSlim(false);
        HardwarePullRunResult workerResult = default;
        Exception? workerError = null;

        var worker = new Thread(() =>
        {
            try
            {
                workerResult = RunWorker(
                    pullLoop,
                    capture,
                    workerStop.Token,
                    workerWake);
            }
            catch (Exception ex)
            {
                workerError = ex;
            }
            finally
            {
                workerCompleted.Set();
            }
        })
        {
            IsBackground = true,
            Name = "mf-encoder-pull",
            Priority = ThreadPriority.AboveNormal,
        };

        worker.Start();
        try
        {
            while (!workerCompleted.Wait(25))
            {
                if (!cancellationToken.IsCancellationRequested) continue;
                workerStop.Cancel();
                workerWake.Set();
            }
        }
        finally
        {
            workerStop.Cancel();
            workerWake.Set();
        }

        if (!worker.Join(TimeSpan.FromSeconds(2)))
        {
            return RuntimeFailure(
                "encoder worker did not stop within 2 seconds");
        }

        if (workerError is not null)
        {
            try { ShutdownHardware(); }
            catch { }
            throw workerError;
        }

        string result = workerResult.Reason;
        if (result != "stopped")
        {
            long now = Stopwatch.GetTimestamp();
            Console.Error.WriteLine(HardwareStallDiagnostic.Format(
                converter.PoolAccounting,
                _encoder.GetProgressSnapshot(),
                _writer.GetProgressSnapshot(),
                now));
            HardwareFallbackKind kind = result.Contains(
                "no NeedInput event",
                StringComparison.Ordinal)
                    ? HardwareFallbackKind.EncoderCreditFamine
                    : HardwareFallbackKind.RuntimeFailure;
            result = RuntimeFailure(result, kind);
        }

        ShutdownHardware();
        return result;
    }

    private HardwarePullRunResult RunWorker(
        HardwareEncoderPullLoop pullLoop,
        IHardwareEncodeCapture capture,
        CancellationToken cancellationToken,
        AutoResetEvent wake)
    {
        long lastReport = Stopwatch.GetTimestamp();
        long reportInterval = Stopwatch.Frequency * 2;
        long lastSubmittedFrames = pullLoop.SubmittedFrames;
        long lastDuplicateFrames = pullLoop.DuplicateFrames;
        long lastAccessUnits = pullLoop.AccessUnitsWritten;

        while (!cancellationToken.IsCancellationRequested)
        {
            _broadcaster.WaitingForWindow = _capture is WindowReattachCapture
            {
                WaitingForWindow: true,
            };
            _setStage("supervising-pipeline");
            string? failure = SupervisePipeline();
            if (failure is not null)
            {
                return new HardwarePullRunResult(
                    failure,
                    pullLoop.SubmittedFrames,
                    pullLoop.AccessUnitsWritten);
            }

            _setStage("hardware-event-poll");
            long now = Stopwatch.GetTimestamp();
            HardwarePullStepResult step = pullLoop.Step(now);
            _setStage("sample-complete");
            if (step.StallReason is not null)
            {
                return new HardwarePullRunResult(
                    step.StallReason,
                    pullLoop.SubmittedFrames,
                    pullLoop.AccessUnitsWritten);
            }

            if (now - lastReport > reportInterval)
            {
                double seconds = (now - lastReport) / (double)Stopwatch.Frequency;
                long submitted = pullLoop.SubmittedFrames - lastSubmittedFrames;
                long duplicates = pullLoop.DuplicateFrames - lastDuplicateFrames;
                long accessUnits = pullLoop.AccessUnitsWritten - lastAccessUnits;
                double duplicatePercent = submitted > 0
                    ? duplicates * 100.0 / submitted
                    : 0;
                _broadcaster.SourceFps = (int)Math.Round(
                    submitted / seconds);
                _broadcaster.DupPercent = (int)Math.Round(duplicatePercent);
                Console.WriteLine(HardwareStallDiagnostic.FormatDelivery(
                    submitted / seconds,
                    duplicatePercent,
                    accessUnits,
                    _encoder.GetProgressSnapshot()));
                lastSubmittedFrames = pullLoop.SubmittedFrames;
                lastDuplicateFrames = pullLoop.DuplicateFrames;
                lastAccessUnits = pullLoop.AccessUnitsWritten;
                lastReport = now;
                reportInterval = Stopwatch.Frequency * 10;
            }

            if (step.DidWork) continue;
            _setStage("waiting-for-capture");
            bool frameReady = capture.WaitForFreshFrame(
                pullLoop.LastSubmittedVersion,
                1);
            if (frameReady)
                WaitHandle.WaitAny([cancellationToken.WaitHandle, wake], 1);
        }

        return new HardwarePullRunResult(
            "stopped",
            pullLoop.SubmittedFrames,
            pullLoop.AccessUnitsWritten);
    }

    private void ShutdownHardware()
    {
        try
        {
            _setStage("hardware-drain");
            IReadOnlyList<EncodedAccessUnit> drained = _encoder.Shutdown();
            if (drained.Count > 0)
                _output.Write(drained);
        }
        finally
        {
            try
            {
                _setStage("hardware-release");
                _encoder.Dispose();
            }
            finally
            {
                _setStage("hardware-pool-dispose");
                _converter.Dispose();
            }
        }
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

    private string RuntimeFailure(
        string reason,
        HardwareFallbackKind kind = HardwareFallbackKind.RuntimeFailure)
    {
        HardwareFallbackDecision decision = HardwareFallbackClassifier.Runtime(kind, reason);
        Console.Error.WriteLine($"[gpu-encode] {decision.Reason}");
        _runtimeFailure(decision.Reason);
        return decision.Reason;
    }
}
