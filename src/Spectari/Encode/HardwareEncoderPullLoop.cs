using System.Diagnostics;
using Spectari.Capture;

namespace Spectari.Encode;

internal interface IHardwareEncodeCapture
{
    long FrameVersion { get; }
    long FramesArrived { get; }
    bool WaitForFreshFrame(long sinceVersion, int timeoutMs);
}

internal interface IHardwareEncodeFrame
{
    long CaptureVersion { get; }
    void Return(FrameLeaseReturnReason reason);
}

internal interface IHardwareEncodeConverter
{
    FrameLeaseAccounting PoolAccounting { get; }

    bool TryConvert(
        long captureVersion,
        out IHardwareEncodeFrame? frame,
        out string failureReason);
}

internal readonly record struct HardwarePullEncoderProgress(
    int InFlightDepth,
    int InputCredits,
    long LastNeedInputEventTicks,
    long LastHaveOutputEventTicks);

internal interface IHardwarePullEncoder
{
    long SubmittedFrameCount { get; }
    HardwarePullEncoderProgress GetProgressSnapshot();
    IReadOnlyList<EncodedAccessUnit> Poll(long nowTicks);

    bool TrySubmit(
        IHardwareEncodeFrame frame,
        long presentationTime100ns,
        long duration100ns);
}

internal sealed class CaptureHardwareEncodeAdapter(ICaptureSource capture)
    : IHardwareEncodeCapture
{
    public long FrameVersion => capture.FrameVersion;
    public long FramesArrived => capture.FramesArrived;
    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs) =>
        capture.WaitForFreshFrame(sinceVersion, timeoutMs);
}

internal sealed class GpuHardwareEncodeFrame : IHardwareEncodeFrame
{
    internal GpuHardwareEncodeFrame(
        VideoFrameLease lease,
        long captureVersion)
    {
        Lease = lease;
        CaptureVersion = captureVersion;
    }

    internal VideoFrameLease Lease { get; }
    public long CaptureVersion { get; }
    public void Return(FrameLeaseReturnReason reason) => Lease.Return(reason);
}

internal sealed class Nv12HardwareEncodeConverter(
    Nv12FrameConverter converter,
    ICaptureSource capture) : IHardwareEncodeConverter
{
    public FrameLeaseAccounting PoolAccounting => converter.PoolAccounting;

    public bool TryConvert(
        long captureVersion,
        out IHardwareEncodeFrame? frame,
        out string failureReason)
    {
        bool converted = converter.TryConvert(
            (IGpuTextureCaptureSource)capture,
            out VideoFrameLease? lease,
            out long copiedVersion,
            out Nv12ConvertFailure failure);
        frame = converted
            ? new GpuHardwareEncodeFrame(
                lease!,
                Math.Max(captureVersion, copiedVersion))
            : null;
        failureReason = failure.ToString();
        return converted;
    }
}

internal readonly record struct HardwarePullStepResult(
    bool DidWork,
    bool SubmittedFrame,
    int AccessUnitsWritten,
    string? StallReason);

internal readonly record struct HardwarePullRunResult(
    string Reason,
    long SubmittedFrames,
    long AccessUnitsWritten);

internal sealed class EncoderCreditFaminePolicy
{
    internal const int DefaultFrameIntervals = 6;

    private readonly long _thresholdTicks;
    private long _readySinceTicks;
    private long _observedNeedInputTicks;

    internal EncoderCreditFaminePolicy(
        int framesPerSecond,
        long timestampFrequency,
        int frameIntervals = DefaultFrameIntervals)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameIntervals);
        _thresholdTicks = Math.Max(
            1,
            checked(timestampFrequency * frameIntervals / framesPerSecond));
    }

    internal bool Observe(
        long nowTicks,
        bool newerFrameReady,
        HardwarePullEncoderProgress progress)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowTicks);
        if (!newerFrameReady || progress.InputCredits > 0)
        {
            Reset();
            return false;
        }

        if (_readySinceTicks == 0)
        {
            _readySinceTicks = nowTicks;
            _observedNeedInputTicks = progress.LastNeedInputEventTicks;
            return false;
        }

        if (progress.LastNeedInputEventTicks > _observedNeedInputTicks)
        {
            _readySinceTicks = nowTicks;
            _observedNeedInputTicks = progress.LastNeedInputEventTicks;
            return false;
        }

        return nowTicks - _readySinceTicks >= _thresholdTicks;
    }

    private void Reset()
    {
        _readySinceTicks = 0;
        _observedNeedInputTicks = 0;
    }
}

/// <summary>
/// Owns the pull contract between capture, conversion, encoding, and output.
/// Every method is called serially by one worker thread.
/// </summary>
internal sealed class HardwareEncoderPullLoop
{
    internal const int KeepaliveIntervalMilliseconds = 250;

    private readonly IHardwareEncodeCapture _capture;
    private readonly IHardwareEncodeConverter _converter;
    private readonly IHardwarePullEncoder _encoder;
    private readonly IEncodedAccessUnitSink _output;
    private readonly EncoderCreditFaminePolicy _creditFamine;
    private readonly long _timestampFrequency;
    private readonly long _frameIntervalTicks;
    private readonly long _keepaliveIntervalTicks;
    private readonly long _nominalDuration100ns;
    private readonly Action<long> _firstSubmission;
    private long _lastSubmittedVersion;
    private long _nextSubmissionDeadlineTicks;
    private long _nextKeepaliveDeadlineTicks;
    private long _firstSubmissionTicks;
    private long _lastPresentationTime100ns;
    private long _submittedFrames;
    private long _duplicateFrames;
    private long _accessUnitsWritten;

    internal HardwareEncoderPullLoop(
        IHardwareEncodeCapture capture,
        IHardwareEncodeConverter converter,
        IHardwarePullEncoder encoder,
        IEncodedAccessUnitSink output,
        int framesPerSecond,
        long timestampFrequency,
        Action<long> firstSubmission)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _capture = capture;
        _converter = converter;
        _encoder = encoder;
        _output = output;
        _creditFamine = new EncoderCreditFaminePolicy(
            framesPerSecond,
            timestampFrequency);
        _timestampFrequency = timestampFrequency;
        _frameIntervalTicks = Math.Max(1, timestampFrequency / framesPerSecond);
        _keepaliveIntervalTicks = Math.Max(
            1,
            checked(timestampFrequency * KeepaliveIntervalMilliseconds / 1000));
        _nominalDuration100ns = Math.Max(
            1,
            TimeSpan.TicksPerSecond / framesPerSecond);
        _firstSubmission = firstSubmission;
        _lastSubmittedVersion = capture.FrameVersion == long.MinValue
            ? long.MinValue
            : capture.FrameVersion - 1;
    }

    internal long LastSubmittedVersion => _lastSubmittedVersion;
    internal long SubmittedFrames => _submittedFrames;
    internal long DuplicateFrames => _duplicateFrames;
    internal long AccessUnitsWritten => _accessUnitsWritten;

    internal HardwarePullStepResult Step(long nowTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowTicks);
        int accessUnits = PollAndWrite(nowTicks);
        bool didWork = accessUnits > 0;
        bool submitted = false;

        long currentVersion = _capture.FrameVersion;
        bool newerFrameReady = currentVersion > _lastSubmittedVersion;
        bool keepaliveDue = _submittedFrames > 0 &&
            nowTicks >= _nextKeepaliveDeadlineTicks;
        bool frameReadyForSubmission = newerFrameReady || keepaliveDue;
        HardwarePullEncoderProgress progress = _encoder.GetProgressSnapshot();
        bool submissionDue = _submittedFrames == 0 ||
            nowTicks >= _nextSubmissionDeadlineTicks;
        if (frameReadyForSubmission && submissionDue && progress.InputCredits > 0)
        {
            if (_converter.TryConvert(
                currentVersion,
                out IHardwareEncodeFrame? frame,
                out _))
            {
                long presentationTime100ns = PresentationTime(nowTicks);
                long duration100ns = _submittedFrames == 0
                    ? _nominalDuration100ns
                    : Math.Max(1, presentationTime100ns - _lastPresentationTime100ns);
                if (!_encoder.TrySubmit(frame!, presentationTime100ns, duration100ns))
                {
                    frame!.Return(FrameLeaseReturnReason.InputRejected);
                    throw new InvalidOperationException(
                        "Encoder rejected a frame after advertising an input credit.");
                }

                if (_submittedFrames == 0)
                {
                    _firstSubmissionTicks = nowTicks;
                    _firstSubmission(nowTicks);
                }
                bool duplicate = frame!.CaptureVersion <= _lastSubmittedVersion;
                _lastPresentationTime100ns = presentationTime100ns;
                _lastSubmittedVersion = Math.Max(
                    currentVersion,
                    frame!.CaptureVersion);
                _submittedFrames++;
                if (duplicate) _duplicateFrames++;
                AdvanceSubmissionDeadline(nowTicks);
                _nextKeepaliveDeadlineTicks = checked(
                    nowTicks + _keepaliveIntervalTicks);
                submitted = true;
                didWork = true;
                accessUnits += PollAndWrite(nowTicks);
                currentVersion = _capture.FrameVersion;
                newerFrameReady = currentVersion > _lastSubmittedVersion;
                keepaliveDue = nowTicks >= _nextKeepaliveDeadlineTicks;
                frameReadyForSubmission = newerFrameReady || keepaliveDue;
                progress = _encoder.GetProgressSnapshot();
                submissionDue = nowTicks >= _nextSubmissionDeadlineTicks;
            }
        }

        string? stallReason = _creditFamine.Observe(
            nowTicks,
            frameReadyForSubmission && submissionDue,
            progress)
                ? $"encoder received no NeedInput event for {EncoderCreditFaminePolicy.DefaultFrameIntervals} frame intervals while a capture frame was ready for submission"
                : null;
        return new HardwarePullStepResult(didWork, submitted, accessUnits, stallReason);
    }

    internal HardwarePullRunResult Run(
        CancellationToken cancellationToken,
        Func<long> getTimestamp)
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        while (!cancellationToken.IsCancellationRequested)
        {
            HardwarePullStepResult step = Step(getTimestamp());
            if (step.StallReason is not null)
            {
                return new HardwarePullRunResult(
                    step.StallReason,
                    _submittedFrames,
                    _accessUnitsWritten);
            }
            if (!step.DidWork)
            {
                bool frameReady = _capture.WaitForFreshFrame(
                    _lastSubmittedVersion,
                    2);
                if (frameReady)
                    cancellationToken.WaitHandle.WaitOne(1);
            }
        }

        return new HardwarePullRunResult(
            "stopped",
            _submittedFrames,
            _accessUnitsWritten);
    }

    private long PresentationTime(long nowTicks)
    {
        if (_submittedFrames == 0) return 0;
        return Math.Max(
            _lastPresentationTime100ns + 1,
            checked((nowTicks - _firstSubmissionTicks) * TimeSpan.TicksPerSecond /
                _timestampFrequency));
    }

    private void AdvanceSubmissionDeadline(long nowTicks)
    {
        if (_submittedFrames == 1)
        {
            _nextSubmissionDeadlineTicks = checked(nowTicks + _frameIntervalTicks);
            return;
        }

        long advanced = checked(_nextSubmissionDeadlineTicks + _frameIntervalTicks);
        _nextSubmissionDeadlineTicks = advanced <= nowTicks
            ? checked(nowTicks + _frameIntervalTicks)
            : advanced;
    }

    private int PollAndWrite(long nowTicks)
    {
        IReadOnlyList<EncodedAccessUnit> accessUnits = _encoder.Poll(nowTicks);
        if (accessUnits.Count == 0) return 0;
        _output.Write(accessUnits);
        _accessUnitsWritten += accessUnits.Count;
        return accessUnits.Count;
    }
}
