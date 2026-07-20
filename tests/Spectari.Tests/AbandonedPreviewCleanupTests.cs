using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class AbandonedPreviewCleanupTests
{
    [Fact]
    public async Task StartsRetirementWithoutWaitingForBlockedFinalCleanup()
    {
        using var retirementStarted = new ManualResetEventSlim();
        using var allowCompletion = new ManualResetEventSlim();
        var resources = new BlockingResources(retirementStarted, allowCompletion);

        Task cleanup = AbandonedPreviewCleanup.Start(resources, "stream-start teardown", _ => { });
        try
        {
            Assert.True(retirementStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(cleanup.IsCompleted);
        }
        finally
        {
            allowCompletion.Set();
        }

        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, resources.DisposeCalls);
    }

    [Fact]
    public async Task UnexpectedRetirementFailureLogsOnceWithTheAction()
    {
        var logs = new List<string>();

        await AbandonedPreviewCleanup.Start(
            new ThrowingResources(),
            "source-change teardown",
            logs.Add);

        string log = Assert.Single(logs);
        Assert.Contains("[preview] source-change teardown background retirement failed", log);
        Assert.DoesNotContain('\n', log);
    }

    private sealed class BlockingResources(
        ManualResetEventSlim retirementStarted,
        ManualResetEventSlim allowCompletion) : IDisposable
    {
        internal int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            retirementStarted.Set();
            allowCompletion.Wait();
        }
    }

    private sealed class ThrowingResources : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("blocked\r\ncleanup");
    }
}
