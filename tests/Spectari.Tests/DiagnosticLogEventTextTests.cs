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
}
