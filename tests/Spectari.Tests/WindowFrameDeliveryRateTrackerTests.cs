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
            samplePeriod: TimeSpan.FromSeconds(2),
            targetFramesPerSecond: 2);

        Assert.Null(tracker.RecordFrame(1000, 0, 0));
        Assert.Null(tracker.RecordFrame(1500, 0, 0));
        Assert.Null(tracker.RecordFrame(2000, 0, 0));
        Assert.Null(tracker.RecordFrame(2500, 0, 0));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(3000, 0, 0));

        Assert.Equal(4, sample.FrameCount);
        Assert.Equal(4, sample.TargetTickCount);
        Assert.Equal(2, sample.ElapsedSeconds);
        Assert.Equal(2, sample.FramesPerSecond);
    }

    [Fact]
    public void RecordFrameStartsANewWindowAfterEachSample()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 10,
            samplePeriod: TimeSpan.FromSeconds(1),
            targetFramesPerSecond: 2);

        Assert.Null(tracker.RecordFrame(0, 0, 0));
        Assert.NotNull(tracker.RecordFrame(10, 0, 0));
        Assert.Null(tracker.RecordFrame(15, 0, 0));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(20, 0, 0));

        Assert.Equal(2, sample.FrameCount);
        Assert.Equal(2, sample.FramesPerSecond);
    }

    [Fact]
    public void RecordFrameResetsWhenTimestampMovesBackward()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 10,
            samplePeriod: TimeSpan.FromSeconds(1),
            targetFramesPerSecond: 2);

        Assert.Null(tracker.RecordFrame(20, 0, 0));
        Assert.Null(tracker.RecordFrame(10, 0, 0));
        Assert.Null(tracker.RecordFrame(15, 0, 0));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(20, 0, 0));

        Assert.Equal(2, sample.FrameCount);
        Assert.Equal(2, sample.FramesPerSecond);
    }

    [Fact]
    public void RecordFrameAttributesCallbackAndGateWaitTiming()
    {
        var tracker = new WindowFrameDeliveryRateTracker(
            timerFrequency: 1000,
            samplePeriod: TimeSpan.FromSeconds(1),
            targetFramesPerSecond: 60);

        Assert.Null(tracker.RecordFrame(1000, 5, 1));
        Assert.Null(tracker.RecordFrame(1250, 10, 2));
        Assert.Null(tracker.RecordFrame(1500, 20, 4));
        Assert.Null(tracker.RecordFrame(1750, 30, 6));
        WindowFrameDeliveryRateSample sample = Assert.IsType<WindowFrameDeliveryRateSample>(
            tracker.RecordFrame(2000, 40, 8));

        Assert.Equal(4, sample.FrameCount);
        Assert.Equal(60, sample.TargetTickCount);
        Assert.Equal(25, sample.AverageCallbackMilliseconds);
        Assert.Equal(40, sample.MaximumCallbackMilliseconds);
        Assert.Equal(5, sample.AverageGateWaitMilliseconds);
        Assert.Equal(8, sample.MaximumGateWaitMilliseconds);
    }
}
