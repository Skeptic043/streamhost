using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class WindowLossGateTests
{
    [Fact]
    public void FirstDetectorWinsAndLaterDetectorIsRejected()
    {
        var gate = new WindowLossGate();

        Assert.True(gate.TryClaim(WindowLossReason.InvalidWindowHandle));
        Assert.False(gate.TryClaim(WindowLossReason.CaptureItemClosed));
        Assert.Equal(WindowLossReason.InvalidWindowHandle, gate.Winner);
    }

    [Fact]
    public void ConcurrentDetectorsProduceExactlyOneWinner()
    {
        var gate = new WindowLossGate();
        int claims = 0;

        Parallel.For(0, 100, index =>
        {
            WindowLossReason reason = index % 2 == 0
                ? WindowLossReason.CaptureItemClosed
                : WindowLossReason.InvalidWindowHandle;
            if (gate.TryClaim(reason)) Interlocked.Increment(ref claims);
        });

        Assert.Equal(1, claims);
        Assert.NotNull(gate.Winner);
    }
}
