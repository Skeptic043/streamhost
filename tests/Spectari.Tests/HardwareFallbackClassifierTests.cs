using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class HardwareFallbackClassifierTests
{
    [Theory]
    [InlineData((int)HardwareFallbackKind.Probe)]
    [InlineData((int)HardwareFallbackKind.Initialization)]
    [InlineData((int)HardwareFallbackKind.AdapterMismatch)]
    public void StartupFailuresStayInSessionWithoutAStallReason(int kindValue)
    {
        var kind = (HardwareFallbackKind)kindValue;
        HardwareFallbackDecision decision = HardwareFallbackClassifier.Startup(kind, "unavailable");

        Assert.False(decision.StartCpuRecovery);
        Assert.False(StreamSession.IsPipelineStallReason(decision.Reason));
        Assert.StartsWith("hardware texture lane unavailable:", decision.Reason);
    }

    [Theory]
    [InlineData((int)HardwareFallbackKind.EncoderCreditFamine)]
    [InlineData((int)HardwareFallbackKind.RuntimeFailure)]
    public void RuntimeFailuresProduceSessionStallReason(int kindValue)
    {
        var kind = (HardwareFallbackKind)kindValue;
        HardwareFallbackDecision decision = HardwareFallbackClassifier.Runtime(kind, "stopped");

        Assert.True(decision.StartCpuRecovery);
        Assert.True(StreamSession.IsPipelineStallReason(decision.Reason));
    }
}
