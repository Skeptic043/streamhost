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
        Assert.Equal(3, snapshot.RecentNetGrowthFrames);
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

    [Fact]
    public void StandingDebtThatNeverGrowsDoesNotSignalAStall()
    {
        var policy = new FrameDebtPolicy(3);

        policy.RecordDroppedTick();
        policy.PlanAvailableTick(repayOne: false);
        policy.PlanAvailableTick(repayOne: false);
        policy.RecordDroppedTick();
        policy.PlanAvailableTick(repayOne: false);
        FrameDebtSnapshot standing = policy.PlanAvailableTick(repayOne: false).Debt;

        Assert.Equal(2, standing.DebtFrames);
        Assert.Equal(2, standing.RecentNetGrowthFrames);
        Assert.False(standing.StallSignal);

        for (int tick = 0; tick < 6; tick++)
            standing = policy.PlanAvailableTick(repayOne: false).Debt;

        Assert.Equal(2, standing.DebtFrames);
        Assert.Equal(0, standing.RecentNetGrowthFrames);
        Assert.False(standing.StallSignal);
    }

    [Fact]
    public void GrowthOutsideTheTwoSecondWindowDoesNotAccumulateTowardAStall()
    {
        var policy = new FrameDebtPolicy(3);

        policy.RecordDroppedTick();
        for (int tick = 0; tick < 6; tick++)
            policy.PlanAvailableTick(repayOne: false);
        FrameDebtSnapshot snapshot = policy.RecordDroppedTick();

        Assert.Equal(2, snapshot.DebtFrames);
        Assert.Equal(1, snapshot.RecentNetGrowthFrames);
        Assert.False(snapshot.StallSignal);
    }
}
