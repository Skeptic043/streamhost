using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class Nv12TexturePoolTests
{
    [Fact]
    public void ExhaustionFailsImmediatelyUntilALeaseReturns()
    {
        using var pool = Nv12TexturePool.CreateForTesting(2);

        Assert.True(pool.TryRent(out VideoFrameLease? first));
        Assert.True(pool.TryRent(out VideoFrameLease? second));
        Assert.False(pool.TryRent(out VideoFrameLease? exhausted));
        Assert.Null(exhausted);

        Assert.True(first!.Return(FrameLeaseReturnReason.OutputProduced));
        Assert.True(pool.TryRent(out VideoFrameLease? replacement));
        Assert.NotNull(replacement);
        second!.Return(FrameLeaseReturnReason.Flush);
        replacement!.Return(FrameLeaseReturnReason.InputRejected);
    }

    [Fact]
    public void EveryExplicitReturnReasonIsAccounted()
    {
        using var pool = Nv12TexturePool.CreateForTesting(2);
        FrameLeaseReturnReason[] reasons =
        [
            FrameLeaseReturnReason.OutputProduced,
            FrameLeaseReturnReason.InputRejected,
            FrameLeaseReturnReason.Flush,
            FrameLeaseReturnReason.Failure,
            FrameLeaseReturnReason.Teardown,
        ];

        foreach (FrameLeaseReturnReason reason in reasons)
        {
            Assert.True(pool.TryRent(out VideoFrameLease? lease));
            Assert.True(lease!.Return(reason));
        }

        FrameLeaseAccounting accounting = pool.GetAccounting();
        Assert.Equal(2, accounting.Available);
        Assert.Equal(0, accounting.Outstanding);
        Assert.Equal(5, accounting.TotalRents);
        foreach (FrameLeaseReturnReason reason in reasons)
            Assert.Equal(1, accounting.Returns[reason]);
    }

    [Fact]
    public void TeardownReturnsEveryOutstandingLease()
    {
        var pool = Nv12TexturePool.CreateForTesting(3);
        pool.TryRent(out VideoFrameLease? first);
        pool.TryRent(out VideoFrameLease? second);

        pool.Dispose();

        FrameLeaseAccounting accounting = pool.GetAccounting();
        Assert.Equal(2, accounting.Returns[FrameLeaseReturnReason.Teardown]);
        Assert.False(first!.Return(FrameLeaseReturnReason.OutputProduced));
        Assert.False(second!.Return(FrameLeaseReturnReason.Failure));
    }

    [Fact]
    public void LeaseCanReturnOnlyOnce()
    {
        using var pool = Nv12TexturePool.CreateForTesting(2);
        pool.TryRent(out VideoFrameLease? lease);

        Assert.True(lease!.Return(FrameLeaseReturnReason.OutputProduced));
        Assert.False(lease.Return(FrameLeaseReturnReason.Failure));
        Assert.Equal(
            1,
            pool.GetAccounting().Returns[FrameLeaseReturnReason.OutputProduced]);
        Assert.Equal(0, pool.GetAccounting().Returns[FrameLeaseReturnReason.Failure]);
    }
}
