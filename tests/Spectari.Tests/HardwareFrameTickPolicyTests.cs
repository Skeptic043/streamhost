using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class HardwareFrameTickPolicyTests
{
    [Fact]
    public void EveryAvailableTickSubmitsExactlyOneCurrentFrame()
    {
        var policy = new HardwareFrameTickPolicy(60);

        HardwareFrameTickPlan first = policy.PlanAvailableTick(duplicateAvailable: false);
        HardwareFrameTickPlan second = policy.PlanAvailableTick(duplicateAvailable: false);

        Assert.Equal(1, first.CurrentFrameSubmissions);
        Assert.Equal(1, second.CurrentFrameSubmissions);
        Assert.Equal(0, first.DuplicateSubmissions);
        Assert.Equal(0, second.DuplicateSubmissions);
    }

    [Fact]
    public void EpochAttachesOnlyToFirstSuccessfulSubmission()
    {
        var policy = new HardwareFrameTickPolicy(60);
        policy.RecordUnavailableTick();

        policy.PlanAvailableTick(duplicateAvailable: false);

        Assert.False(policy.ConfirmNormalTickSubmission(0, 0));
        Assert.True(policy.ConfirmNormalTickSubmission(0, 1));
        Assert.False(policy.ConfirmNormalTickSubmission(1, 2));
    }

    [Fact]
    public void CollectionOnlyUnavailableTickRecordsDebtWithoutAttachingEpoch()
    {
        var policy = new HardwareFrameTickPolicy(60);

        FrameDebtSnapshot debt = policy.RecordUnavailableTick();
        bool epochAttached = policy.ConfirmNormalTickSubmission(0, 0);
        HardwareFrameTickPlan recovery = policy.PlanAvailableTick(duplicateAvailable: true);

        Assert.Equal(1, debt.DebtFrames);
        Assert.False(epochAttached);
        Assert.Equal(1, recovery.CurrentFrameSubmissions);
        Assert.Equal(1, recovery.DuplicateSubmissions);
        Assert.Equal(0, recovery.Debt.DebtFrames);
    }

    [Fact]
    public void CollectTickSubmissionCannotAttachEpochOnNextNormalTickWithoutProgress()
    {
        var policy = new HardwareFrameTickPolicy(60);
        policy.RecordUnavailableTick();

        Assert.False(policy.ConfirmNormalTickSubmission(1, 1));
        Assert.True(policy.ConfirmNormalTickSubmission(1, 2));
    }

    [Fact]
    public void AvailableTickRepaysAtMostOneDebtFrame()
    {
        var policy = new HardwareFrameTickPolicy(60);
        policy.RecordUnavailableTick();
        policy.RecordUnavailableTick();

        HardwareFrameTickPlan plan = policy.PlanAvailableTick(duplicateAvailable: true);

        Assert.Equal(1, plan.CurrentFrameSubmissions);
        Assert.Equal(1, plan.DuplicateSubmissions);
        Assert.Equal(1, plan.Debt.DebtFrames);
    }

    [Fact]
    public void PendingQueueAtLimitRecordsDebtWithoutAdmittingAFrame()
    {
        var policy = new HardwareFrameTickPolicy(60);

        HardwareFrameTickAdmission blocked = policy.AdmitEncoderTick(
            HardwareFrameTickPolicy.MaximumPendingQueueDepth);
        HardwareFrameTickAdmission recovery = policy.AdmitEncoderTick(
            HardwareFrameTickPolicy.MaximumPendingQueueDepth - 2);
        HardwareFrameTickPlan recovered = policy.PlanAvailableTick(recovery.CanRepayDebt);

        Assert.False(blocked.Accepted);
        Assert.Equal(1, blocked.Debt.DebtFrames);
        Assert.True(recovery.Accepted);
        Assert.True(recovery.CanRepayDebt);
        Assert.Equal(1, recovered.DuplicateSubmissions);
        Assert.Equal(0, recovered.Debt.DebtFrames);
    }

    [Fact]
    public void StandingDepthTwoAdmitsCurrentFrameAndRepayment()
    {
        var policy = new HardwareFrameTickPolicy(60);
        policy.RecordUnavailableTick();

        HardwareFrameTickAdmission admission = policy.AdmitEncoderTick(2);
        HardwareFrameTickPlan plan = policy.PlanAvailableTick(admission.CanRepayDebt);

        Assert.True(admission.Accepted);
        Assert.True(admission.CanRepayDebt);
        Assert.Equal(1, plan.CurrentFrameSubmissions);
        Assert.Equal(1, plan.DuplicateSubmissions);
        Assert.Equal(0, plan.Debt.DebtFrames);
    }

    [Fact]
    public void LastPendingSlotAdmitsCurrentFrameWithoutRepayment()
    {
        var policy = new HardwareFrameTickPolicy(60);
        policy.RecordUnavailableTick();

        HardwareFrameTickAdmission admission = policy.AdmitEncoderTick(
            HardwareFrameTickPolicy.MaximumPendingQueueDepth - 1);
        HardwareFrameTickPlan plan = policy.PlanAvailableTick(admission.CanRepayDebt);

        Assert.True(admission.Accepted);
        Assert.False(admission.CanRepayDebt);
        Assert.Equal(0, plan.DuplicateSubmissions);
        Assert.Equal(1, plan.Debt.DebtFrames);
        Assert.Equal(6, HardwareFrameTickPolicy.MaximumPendingQueueDepth);
    }
}
