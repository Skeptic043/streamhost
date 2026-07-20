using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class CaptureDeviceFormatPolicyTests
{
    [Fact]
    public void OversizedMjpegDoesNotBeatRealTimeFullHdMode()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 3840, 2160, 30),
            Format(CaptureDevicePixelFormat.Nv12, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 30),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
        Assert.Equal(1920, selected.Width);
        Assert.Equal(1080, selected.Height);
        Assert.Equal(60, selected.RoundedFramesPerSecond);
    }

    [Fact]
    public void FullHdWebcamMjpegBeatsLowerResolutionUncompressedMode()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 30),
            Format(CaptureDevicePixelFormat.Nv12, 1280, 720, 30),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Mjpeg, selected.PixelFormat);
        Assert.Equal(1920, selected.Width);
        Assert.Equal(1080, selected.Height);
        Assert.Equal(30, selected.RoundedFramesPerSecond);
    }

    [Fact]
    public void SmallestResolutionWinsWhenEveryModeExceedsThePixelAreaCeiling()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 3840, 2160, 60),
            Format(CaptureDevicePixelFormat.Yuy2, 2560, 1440, 60),
            Format(CaptureDevicePixelFormat.Nv12, 2560, 1440, 30),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Yuy2, selected.PixelFormat);
        Assert.Equal(2560, selected.Width);
        Assert.Equal(1440, selected.Height);
        Assert.Equal(60, selected.RoundedFramesPerSecond);
    }

    [Fact]
    public void PreferredFormatStaysAtOrBelowTheFrameRateCeiling()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Yuy2, 1920, 1080, 120),
            Format(CaptureDevicePixelFormat.Nv12, 1280, 720, 60),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
        Assert.Equal(1280, selected.Width);
        Assert.Equal(720, selected.Height);
        Assert.Equal(60, selected.RoundedFramesPerSecond);
    }

    [Fact]
    public void UncompressedSubtypeOrderBreaksEquivalentModeTies()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Yuy2, 1920, 1080, 60),
            Format(CaptureDevicePixelFormat.Nv12, 1920, 1080, 60),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
    }

    [Fact]
    public void CheaperSubtypeWinsWhenEquivalentCompressedModeHasHigherPixelRate()
    {
        CaptureDeviceFormat[] formats =
        [
            Format(CaptureDevicePixelFormat.Mjpeg, 1920, 1080, 60),
            new(CaptureDevicePixelFormat.Nv12, 1920, 1080, 60_000, 1_001),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
        Assert.Equal(60_000u, selected.FrameRateNumerator);
        Assert.Equal(1_001u, selected.FrameRateDenominator);
    }

    [Fact]
    public void InvalidModesAreIgnored()
    {
        CaptureDeviceFormat[] formats =
        [
            new(CaptureDevicePixelFormat.Mjpeg, 3840, 2160, 60, 0),
            Format(CaptureDevicePixelFormat.Nv12, 1920, 1080, 30),
        ];

        CaptureDeviceFormat? selected = CaptureDeviceFormatPolicy.ChoosePreferredFormat(formats);

        Assert.NotNull(selected);
        Assert.Equal(CaptureDevicePixelFormat.Nv12, selected.PixelFormat);
    }

    private static CaptureDeviceFormat Format(
        CaptureDevicePixelFormat pixelFormat,
        int width,
        int height,
        uint fps) => new(pixelFormat, width, height, fps, 1);
}
