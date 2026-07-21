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

        Assert.False(policy.ConfirmEncoderSubmission(encoderSubmitted: false));
        Assert.True(policy.ConfirmEncoderSubmission(encoderSubmitted: true));
        Assert.False(policy.ConfirmEncoderSubmission(encoderSubmitted: true));
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
}
