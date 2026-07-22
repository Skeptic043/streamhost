namespace Spectari.Capture;

internal readonly record struct WindowFrameDeliveryRateSample(
    long FrameCount,
    long TargetTickCount,
    double ElapsedSeconds,
    double AverageCallbackMilliseconds,
    double MaximumCallbackMilliseconds,
    double AverageGateWaitMilliseconds,
    double MaximumGateWaitMilliseconds)
{
    internal double FramesPerSecond => FrameCount / ElapsedSeconds;
}

internal sealed class WindowFrameDeliveryRateTracker
{
    private readonly Lock _gate = new();
    private readonly long _timerFrequency;
    private readonly long _samplePeriodTicks;
    private readonly int _targetFramesPerSecond;
    private bool _started;
    private long _sampleStartedTicks;
    private long _frameCount;
    private long _totalCallbackTicks;
    private long _maximumCallbackTicks;
    private long _totalGateWaitTicks;
    private long _maximumGateWaitTicks;

    internal WindowFrameDeliveryRateTracker(
        long timerFrequency,
        TimeSpan samplePeriod,
        int targetFramesPerSecond)
    {
        if (timerFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timerFrequency));
        if (samplePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(samplePeriod));
        if (targetFramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));

        _timerFrequency = timerFrequency;
        _samplePeriodTicks = Math.Max(
            1,
            (long)Math.Round(samplePeriod.TotalSeconds * timerFrequency));
        _targetFramesPerSecond = targetFramesPerSecond;
    }

    internal WindowFrameDeliveryRateSample? RecordFrame(
        long timestampTicks,
        long callbackDurationTicks,
        long gateWaitTicks)
    {
        lock (_gate)
        {
            if (!_started || timestampTicks < _sampleStartedTicks)
            {
                _started = true;
                _sampleStartedTicks = timestampTicks;
                ResetCounters();
                return null;
            }

            _frameCount++;
            callbackDurationTicks = Math.Max(0, callbackDurationTicks);
            gateWaitTicks = Math.Max(0, gateWaitTicks);
            _totalCallbackTicks += callbackDurationTicks;
            _maximumCallbackTicks = Math.Max(_maximumCallbackTicks, callbackDurationTicks);
            _totalGateWaitTicks += gateWaitTicks;
            _maximumGateWaitTicks = Math.Max(_maximumGateWaitTicks, gateWaitTicks);
            long elapsedTicks = timestampTicks - _sampleStartedTicks;
            if (elapsedTicks < _samplePeriodTicks)
                return null;

            double millisecondsPerTick = 1000.0 / _timerFrequency;
            var sample = new WindowFrameDeliveryRateSample(
                _frameCount,
                (long)Math.Floor(
                    elapsedTicks * (_targetFramesPerSecond / (double)_timerFrequency)),
                elapsedTicks / (double)_timerFrequency,
                _totalCallbackTicks * millisecondsPerTick / _frameCount,
                _maximumCallbackTicks * millisecondsPerTick,
                _totalGateWaitTicks * millisecondsPerTick / _frameCount,
                _maximumGateWaitTicks * millisecondsPerTick);
            _sampleStartedTicks = timestampTicks;
            ResetCounters();
            return sample;
        }
    }

    private void ResetCounters()
    {
        _frameCount = 0;
        _totalCallbackTicks = 0;
        _maximumCallbackTicks = 0;
        _totalGateWaitTicks = 0;
        _maximumGateWaitTicks = 0;
    }
}
