namespace Spectari.Capture;

internal readonly record struct WindowFrameDeliveryRateSample(
    long FrameCount,
    double ElapsedSeconds)
{
    internal double FramesPerSecond => FrameCount / ElapsedSeconds;
}

internal sealed class WindowFrameDeliveryRateTracker
{
    private readonly Lock _gate = new();
    private readonly long _timerFrequency;
    private readonly long _samplePeriodTicks;
    private bool _started;
    private long _sampleStartedTicks;
    private long _frameCount;

    internal WindowFrameDeliveryRateTracker(long timerFrequency, TimeSpan samplePeriod)
    {
        if (timerFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timerFrequency));
        if (samplePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(samplePeriod));

        _timerFrequency = timerFrequency;
        _samplePeriodTicks = Math.Max(
            1,
            (long)Math.Round(samplePeriod.TotalSeconds * timerFrequency));
    }

    internal WindowFrameDeliveryRateSample? RecordFrame(long timestampTicks)
    {
        lock (_gate)
        {
            if (!_started || timestampTicks < _sampleStartedTicks)
            {
                _started = true;
                _sampleStartedTicks = timestampTicks;
                _frameCount = 0;
                return null;
            }

            _frameCount++;
            long elapsedTicks = timestampTicks - _sampleStartedTicks;
            if (elapsedTicks < _samplePeriodTicks)
                return null;

            var sample = new WindowFrameDeliveryRateSample(
                _frameCount,
                elapsedTicks / (double)_timerFrequency);
            _sampleStartedTicks = timestampTicks;
            _frameCount = 0;
            return sample;
        }
    }
}
