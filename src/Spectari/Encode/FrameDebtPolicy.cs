namespace Spectari.Encode;

internal readonly record struct FrameDebtSnapshot(
    int DebtFrames,
    TimeSpan SyncError,
    int RecentNetGrowthFrames,
    bool StallSignal);

internal readonly record struct FrameSubmissionPlan(
    int SubmissionCount,
    FrameDebtSnapshot Debt);

internal readonly record struct HardwareFrameTickPlan(
    int CurrentFrameSubmissions,
    int DuplicateSubmissions,
    FrameDebtSnapshot Debt);

internal readonly record struct HardwareFrameTickAdmission(
    bool Accepted,
    bool CanRepayDebt,
    FrameDebtSnapshot Debt);

/// <summary>Pure timing contract for fixed-rate hardware submissions.</summary>
internal sealed class HardwareFrameTickPolicy
{
    internal const int MaximumPendingQueueDepth = 6;

    private readonly FrameDebtPolicy _debt;
    private bool _epochAttached;

    internal HardwareFrameTickPolicy(int framesPerSecond, int? growthThresholdFrames = null)
    {
        _debt = new FrameDebtPolicy(framesPerSecond, growthThresholdFrames);
    }

    internal FrameDebtSnapshot CurrentDebt => _debt.Current;

    internal FrameDebtSnapshot RecordUnavailableTick() => _debt.RecordDroppedTick();

    internal HardwareFrameTickAdmission AdmitEncoderTick(int pendingQueueDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingQueueDepth);
        bool accepted = pendingQueueDepth + 1 <= MaximumPendingQueueDepth;
        return accepted
            ? new(
                Accepted: true,
                CanRepayDebt: pendingQueueDepth + 2 <= MaximumPendingQueueDepth,
                _debt.Current)
            : new(
                Accepted: false,
                CanRepayDebt: false,
                _debt.RecordDroppedTick());
    }

    internal HardwareFrameTickPlan PlanAvailableTick(bool duplicateAvailable)
    {
        FrameSubmissionPlan submission = _debt.PlanAvailableTick(duplicateAvailable);
        return new HardwareFrameTickPlan(
            CurrentFrameSubmissions: 1,
            DuplicateSubmissions: submission.SubmissionCount - 1,
            submission.Debt);
    }

    internal bool ConfirmNormalTickSubmission(long submittedBefore, long submittedAfter)
    {
        if (submittedAfter <= submittedBefore || _epochAttached) return false;
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
    internal const int GrowthWindowSeconds = 2;

    private readonly long _frameDurationTicks;
    private readonly int _growthThresholdFrames;
    private readonly int[] _recentDebtChanges;
    private int _debtFrames;
    private int _recentNetGrowthFrames;
    private int _recentChangeCount;
    private int _recentChangeIndex;

    internal FrameDebtPolicy(int framesPerSecond, int? growthThresholdFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        _frameDurationTicks = Math.Max(
            1,
            (long)Math.Round(TimeSpan.TicksPerSecond / (double)framesPerSecond));
        _growthThresholdFrames = growthThresholdFrames ?? framesPerSecond;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_growthThresholdFrames);
        _recentDebtChanges = new int[checked(framesPerSecond * GrowthWindowSeconds)];
    }

    internal FrameDebtSnapshot Current => Snapshot();

    internal FrameDebtSnapshot RecordDroppedTick()
    {
        if (_debtFrames < int.MaxValue)
        {
            _debtFrames++;
            RecordDebtChange(1);
        }
        else
        {
            RecordDebtChange(0);
        }
        return Snapshot();
    }

    internal FrameSubmissionPlan PlanAvailableTick(bool repayOne = true)
    {
        int submissions = 1;
        int debtChange = 0;
        if (repayOne && _debtFrames > 0)
        {
            _debtFrames--;
            debtChange = -1;
            submissions++;
        }
        RecordDebtChange(debtChange);
        return new FrameSubmissionPlan(submissions, Snapshot());
    }

    private void RecordDebtChange(int change)
    {
        if (_recentChangeCount == _recentDebtChanges.Length)
            _recentNetGrowthFrames -= _recentDebtChanges[_recentChangeIndex];
        else
            _recentChangeCount++;

        _recentDebtChanges[_recentChangeIndex] = change;
        _recentNetGrowthFrames += change;
        _recentChangeIndex = (_recentChangeIndex + 1) % _recentDebtChanges.Length;
    }

    private FrameDebtSnapshot Snapshot() => new(
        _debtFrames,
        TimeSpan.FromTicks(checked(_frameDurationTicks * (long)_debtFrames)),
        _recentNetGrowthFrames,
        _recentNetGrowthFrames >= _growthThresholdFrames);
}
