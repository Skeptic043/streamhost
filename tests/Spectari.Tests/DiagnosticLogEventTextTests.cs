using Spectari.Capture;
using Spectari.Util;
using Xunit;

namespace Spectari.Tests;

public sealed class DiagnosticLogEventTextTests
{
    [Fact]
    public void AppStartedIncludesVersion()
    {
        Assert.Equal(
            "[app] Spectari version: 0.21-rc.2",
            DiagnosticLogEventText.AppStarted("0.21-rc.2"));
    }

    [Theory]
    [InlineData((int)CaptureDevicePixelFormat.Nv12, "NV12")]
    [InlineData((int)CaptureDevicePixelFormat.Yuy2, "YUY2")]
    [InlineData((int)CaptureDevicePixelFormat.Mjpeg, "MJPEG")]
    public void CaptureDeviceNativeModeIncludesNegotiatedDetails(
        int pixelFormatValue,
        string expectedPixelFormat)
    {
        var pixelFormat = (CaptureDevicePixelFormat)pixelFormatValue;
        var format = new CaptureDeviceFormat(pixelFormat, 1920, 1080, 60000, 1001);

        Assert.Equal(
            $"[capture-device] native mode: 1920x1080, {expectedPixelFormat}, 59.94 fps (60000/1001)",
            DiagnosticLogEventText.CaptureDeviceNativeMode(format));
    }

    [Fact]
    public void WindowCaptureMinUpdateIntervalIncludesAppliedTarget()
    {
        var result = new WindowCaptureMinUpdateIntervalResult(
            WindowCaptureMinUpdateIntervalStatus.Applied,
            WindowCaptureMinUpdateInterval.TargetInterval,
            60);

        Assert.Equal(
            "[window-capture] MinUpdateInterval applied: 16.667 ms (60 fps target)",
            DiagnosticLogEventText.WindowCaptureMinUpdateInterval(result));
    }

    [Theory]
    [InlineData(
        (int)WindowCaptureMinUpdateIntervalStatus.Unavailable,
        0,
        "[window-capture] MinUpdateInterval unavailable; system default remains active")]
    [InlineData(
        (int)WindowCaptureMinUpdateIntervalStatus.InterfaceQueryFailed,
        unchecked((int)0x80004005),
        "[window-capture] MinUpdateInterval interface query failed (HRESULT 0x80004005); system default remains active")]
    [InlineData(
        (int)WindowCaptureMinUpdateIntervalStatus.ApplyFailed,
        unchecked((int)0x80070057),
        "[window-capture] MinUpdateInterval apply failed (HRESULT 0x80070057); system default remains active")]
    public void WindowCaptureMinUpdateIntervalDescribesFallback(
        int statusValue,
        int hResult,
        string expected)
    {
        var result = new WindowCaptureMinUpdateIntervalResult(
            (WindowCaptureMinUpdateIntervalStatus)statusValue,
            TimeSpan.FromTicks(166667),
            60,
            hResult);

        Assert.Equal(expected, DiagnosticLogEventText.WindowCaptureMinUpdateInterval(result));
    }

    [Fact]
    public void WindowFrameDeliveryRateIncludesMeasuredWindow()
    {
        var sample = new WindowFrameDeliveryRateSample(599, 10.01);

        Assert.Equal(
            "[window-capture] frame delivery: 59.8 fps (599 frames in 10.0s)",
            DiagnosticLogEventText.WindowFrameDeliveryRate(sample));
    }
}
