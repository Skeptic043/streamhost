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
}
