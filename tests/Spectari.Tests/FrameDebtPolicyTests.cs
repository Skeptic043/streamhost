using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class FrameDebtPolicyTests
{
    [Fact]
    public void DroppedTicksAccumulateSyncErrorAtConfiguredRate()
    {
        var policy = new FrameDebtPolicy(60);

        policy.RecordDroppedTick();
        policy.RecordDroppedTick();
        FrameDebtSnapshot snapshot = policy.RecordDroppedTick();

        Assert.Equal(3, snapshot.DebtFrames);
        Assert.Equal(TimeSpan.FromTicks(500_001), snapshot.SyncError);
        Assert.False(snapshot.StallSignal);
    }

    [Fact]
    public void AvailableTickRepaysAtMostOneDebtFrame()
    {
        var policy = new FrameDebtPolicy(30);
        policy.RecordDroppedTick();
        policy.RecordDroppedTick();

        FrameSubmissionPlan first = policy.PlanAvailableTick();
        FrameSubmissionPlan second = policy.PlanAvailableTick();
        FrameSubmissionPlan third = policy.PlanAvailableTick();

        Assert.Equal(2, first.SubmissionCount);
        Assert.Equal(1, first.Debt.DebtFrames);
        Assert.Equal(2, second.SubmissionCount);
        Assert.Equal(0, second.Debt.DebtFrames);
        Assert.Equal(1, third.SubmissionCount);
        Assert.Equal(0, third.Debt.DebtFrames);
    }

    [Fact]
    public void TickWithoutDuplicateLeaseLeavesExistingDebtAlone()
    {
        var policy = new FrameDebtPolicy(60);
        policy.RecordDroppedTick();

        FrameSubmissionPlan plan = policy.PlanAvailableTick(repayOne: false);

        Assert.Equal(1, plan.SubmissionCount);
        Assert.Equal(1, plan.Debt.DebtFrames);
    }

    [Fact]
    public void OneSecondThresholdSignalsAStall()
    {
        var policy = new FrameDebtPolicy(3);

        Assert.False(policy.RecordDroppedTick().StallSignal);
        Assert.False(policy.RecordDroppedTick().StallSignal);
        Assert.True(policy.RecordDroppedTick().StallSignal);
    }
}
