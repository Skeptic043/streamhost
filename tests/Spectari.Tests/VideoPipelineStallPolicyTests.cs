using Xunit;

namespace Spectari.Tests;

public sealed class VideoPipelineStallPolicyTests
{
    [Fact]
    public void UpstreamStallDoesNotPermitCpuRecovery()
    {
        VideoPipelineStallDecision decision = VideoPipelineStallPolicy.Classify(
            sustainedInput: false,
            inputWriteBlocked: false);

        Assert.False(decision.ConfirmedEncoderOrOutputFailure);
        Assert.False(decision.PermitCpuRecovery);
        Assert.False(StreamSession.IsPipelineStallReason(decision.StopReason));
        Assert.Contains("upstream capture, conversion, or pacing stall", decision.StopReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EncoderOrOutputFailureStillPermitsCpuRecovery(
        bool sustainedInput,
        bool inputWriteBlocked)
    {
        VideoPipelineStallDecision decision = VideoPipelineStallPolicy.Classify(
            sustainedInput,
            inputWriteBlocked);

        Assert.True(decision.ConfirmedEncoderOrOutputFailure);
        Assert.True(decision.PermitCpuRecovery);
        Assert.True(StreamSession.IsPipelineStallReason(decision.StopReason));
    }
}
