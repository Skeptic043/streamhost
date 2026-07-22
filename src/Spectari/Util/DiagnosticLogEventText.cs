using System.Globalization;
using Spectari.Capture;

namespace Spectari.Util;

internal static class DiagnosticLogEventText
{
    internal static string AppStarted(string version) =>
        $"[app] Spectari version: {version}";

    internal static string CaptureDeviceNativeMode(CaptureDeviceFormat format)
    {
        string pixelFormat = format.PixelFormat switch
        {
            CaptureDevicePixelFormat.Nv12 => "NV12",
            CaptureDevicePixelFormat.Yuy2 => "YUY2",
            CaptureDevicePixelFormat.Mjpeg => "MJPEG",
            _ => format.PixelFormat.ToString().ToUpperInvariant(),
        };
        string framesPerSecond = format.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
        return $"[capture-device] native mode: {format.Width}x{format.Height}, {pixelFormat}, " +
               $"{framesPerSecond} fps ({format.FrameRateNumerator}/{format.FrameRateDenominator})";
    }

    internal static string WindowCaptureMinUpdateInterval(
        WindowCaptureMinUpdateIntervalResult result)
    {
        string milliseconds = result.Interval.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
        return result.Status switch
        {
            WindowCaptureMinUpdateIntervalStatus.Applied =>
                $"[window-capture] MinUpdateInterval applied: {milliseconds} ms " +
                $"({result.TargetFramesPerSecond} fps target)",
            WindowCaptureMinUpdateIntervalStatus.Unavailable =>
                "[window-capture] MinUpdateInterval unavailable; system default remains active",
            WindowCaptureMinUpdateIntervalStatus.InterfaceQueryFailed =>
                $"[window-capture] MinUpdateInterval interface query failed " +
                $"(HRESULT 0x{result.HResult:X8}); system default remains active",
            _ =>
                $"[window-capture] MinUpdateInterval apply failed " +
                $"(HRESULT 0x{result.HResult:X8}); system default remains active",
        };
    }

    internal static string WindowFrameDeliveryRate(WindowFrameDeliveryRateSample sample)
    {
        string framesPerSecond = sample.FramesPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
        string elapsedSeconds = sample.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string averageCallbackMilliseconds = sample.AverageCallbackMilliseconds.ToString(
            "0.00",
            CultureInfo.InvariantCulture);
        string maximumCallbackMilliseconds = sample.MaximumCallbackMilliseconds.ToString(
            "0.00",
            CultureInfo.InvariantCulture);
        string averageGateWaitMilliseconds = sample.AverageGateWaitMilliseconds.ToString(
            "0.00",
            CultureInfo.InvariantCulture);
        string maximumGateWaitMilliseconds = sample.MaximumGateWaitMilliseconds.ToString(
            "0.00",
            CultureInfo.InvariantCulture);
        return $"[window-capture] frame delivery: {framesPerSecond} fps " +
               $"(WGC delivered {sample.FrameCount} frames / {sample.TargetTickCount} target ticks in {elapsedSeconds}s); " +
               $"callback avg/max {averageCallbackMilliseconds}/{maximumCallbackMilliseconds} ms; " +
               $"gate/context wait avg/max {averageGateWaitMilliseconds}/{maximumGateWaitMilliseconds} ms";
    }
}
