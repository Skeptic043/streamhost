using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class WindowFrameDeliveryRateTrackerTests
{
    [Fact]
    public void RecordFrameProducesRateAfterMeasurementWindow()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 1000,
            samplePeriod: TimeSpan.FromSeconds(2));

        Assert.Null(tracker.RecordFrame(1000));
        Assert.Null(tracker.RecordFrame(1500));
        Assert.Null(tracker.RecordFrame(2000));
        Assert.Null(tracker.RecordFrame(2500));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(3000));

        Assert.Equal(4, sample.FrameCount);
        Assert.Equal(2, sample.ElapsedSeconds);
        Assert.Equal(2, sample.FramesPerSecond);
    }

    [Fact]
    public void RecordFrameStartsANewWindowAfterEachSample()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 10,
            samplePeriod: TimeSpan.FromSeconds(1));

        Assert.Null(tracker.RecordFrame(0));
        Assert.NotNull(tracker.RecordFrame(10));
        Assert.Null(tracker.RecordFrame(15));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(20));

        Assert.Equal(2, sample.FrameCount);
        Assert.Equal(2, sample.FramesPerSecond);
    }

    [Fact]
    public void RecordFrameResetsWhenTimestampMovesBackward()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 10,
            samplePeriod: TimeSpan.FromSeconds(1));

        Assert.Null(tracker.RecordFrame(20));
        Assert.Null(tracker.RecordFrame(10));
        Assert.Null(tracker.RecordFrame(15));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(20));

        Assert.Equal(2, sample.FrameCount);
        Assert.Equal(2, sample.FramesPerSecond);
    }
}
