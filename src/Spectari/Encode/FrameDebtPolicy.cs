namespace Spectari.Encode;

internal readonly record struct FrameDebtSnapshot(
    int DebtFrames,
    TimeSpan SyncError,
    bool StallSignal);

internal readonly record struct FrameSubmissionPlan(
    int SubmissionCount,
    FrameDebtSnapshot Debt);

internal readonly record struct HardwareFrameTickPlan(
    int CurrentFrameSubmissions,
    int DuplicateSubmissions,
    FrameDebtSnapshot Debt);

/// <summary>Pure timing contract for fixed-rate hardware submissions.</summary>
internal sealed class HardwareFrameTickPolicy
{
    private readonly FrameDebtPolicy _debt;
    private bool _epochAttached;

    internal HardwareFrameTickPolicy(int framesPerSecond, int? stallThresholdFrames = null)
    {
        _debt = new FrameDebtPolicy(framesPerSecond, stallThresholdFrames);
    }

    internal FrameDebtSnapshot CurrentDebt => _debt.Current;

    internal FrameDebtSnapshot RecordUnavailableTick() => _debt.RecordDroppedTick();

    internal HardwareFrameTickPlan PlanAvailableTick(bool duplicateAvailable)
    {
        FrameSubmissionPlan submission = _debt.PlanAvailableTick(duplicateAvailable);
        return new HardwareFrameTickPlan(
            CurrentFrameSubmissions: 1,
            DuplicateSubmissions: submission.SubmissionCount - 1,
            submission.Debt);
    }

    internal bool ConfirmEncoderSubmission(bool encoderSubmitted)
    {
        if (!encoderSubmitted || _epochAttached) return false;
        _epochAttached = true;
        return true;
    }
}

/// <summary>
/// Keeps a fixed-rate video timeline aligned with wall-clock audio after dropped ticks.
/// A later successful tick submits the current frame and at most one duplicate.
/// </summary>
internal sealed class FrameDebtPolicy
{
    private readonly long _frameDurationTicks;
    private readonly int _stallThresholdFrames;
    private int _debtFrames;

    internal FrameDebtPolicy(int framesPerSecond, int? stallThresholdFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        _frameDurationTicks = Math.Max(
            1,
            (long)Math.Round(TimeSpan.TicksPerSecond / (double)framesPerSecond));
        _stallThresholdFrames = stallThresholdFrames ?? framesPerSecond;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_stallThresholdFrames);
    }

    internal FrameDebtSnapshot Current => Snapshot();

    internal FrameDebtSnapshot RecordDroppedTick()
    {
        if (_debtFrames < int.MaxValue)
            _debtFrames++;
        return Snapshot();
    }

    internal FrameSubmissionPlan PlanAvailableTick(bool repayOne = true)
    {
        int submissions = 1;
        if (repayOne && _debtFrames > 0)
        {
            _debtFrames--;
            submissions++;
        }
        return new FrameSubmissionPlan(submissions, Snapshot());
    }

    private FrameDebtSnapshot Snapshot() => new(
        _debtFrames,
        TimeSpan.FromTicks(checked(_frameDurationTicks * (long)_debtFrames)),
        _debtFrames >= _stallThresholdFrames);
}
