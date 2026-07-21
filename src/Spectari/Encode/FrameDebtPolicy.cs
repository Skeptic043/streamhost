namespace Spectari.Encode;

internal readonly record struct FrameDebtSnapshot(
    int DebtFrames,
    TimeSpan SyncError,
    bool StallSignal);

internal readonly record struct FrameSubmissionPlan(
    int SubmissionCount,
    FrameDebtSnapshot Debt);

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
