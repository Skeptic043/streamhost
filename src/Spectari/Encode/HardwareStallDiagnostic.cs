using System.Diagnostics;

namespace Spectari.Encode;

internal static class HardwareStallDiagnostic
{
    internal static string FormatDelivery(
        double framesPerSecond,
        double duplicatePercent,
        long accessUnits,
        HardwarePullEncoderProgress encoder) =>
        $"[gpu-encode] encode delivery: {framesPerSecond:F1} stream fps ({duplicatePercent:F0}% duplicate), {accessUnits} access units, in-flight={encoder.InFlightDepth}, input-credits={encoder.InputCredits}.";

    internal static string Format(
        FrameLeaseAccounting pool,
        HardwarePullEncoderProgress encoder,
        VideoInputWriterProgress writer,
        long nowTicks) =>
        Format(pool, encoder, writer, nowTicks, Stopwatch.Frequency);

    internal static string Format(
        FrameLeaseAccounting pool,
        HardwarePullEncoderProgress encoder,
        VideoInputWriterProgress writer,
        long nowTicks,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);

        long lastWriteTicks = writer.WriteInProgress
            ? writer.LastWriteStartedTicks
            : writer.LastWriteCompletedTicks;
        return string.Join(Environment.NewLine,
            "[gpu-encode] encoder-stall resources:",
            $"  nv12-pool outstanding={pool.Outstanding}/{pool.Capacity}",
            $"  mf-encoder in-flight={encoder.InFlightDepth} input-credits={encoder.InputCredits} last-need-input={Age(encoder.LastNeedInputEventTicks, nowTicks, timestampFrequency)} last-have-output={Age(encoder.LastHaveOutputEventTicks, nowTicks, timestampFrequency)}",
            $"  video-input queue-depth={writer.QueueDepth} write-in-progress={writer.WriteInProgress.ToString().ToLowerInvariant()} last-write={Age(lastWriteTicks, nowTicks, timestampFrequency)}");
    }

    private static string Age(long thenTicks, long nowTicks, long timestampFrequency)
    {
        if (thenTicks == 0) return "never";
        long elapsedMilliseconds = (long)(
            Math.Max(0, nowTicks - thenTicks) * 1000.0 / timestampFrequency);
        return elapsedMilliseconds < 1000
            ? $"{elapsedMilliseconds}ms ago"
            : $"{elapsedMilliseconds / 1000.0:F1}s ago";
    }
}
