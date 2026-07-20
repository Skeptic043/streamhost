namespace Spectari.Capture;

internal enum CaptureDevicePixelFormat
{
    Nv12,
    Yuy2,
    Mjpeg,
}

internal sealed record CaptureDeviceFormat(
    CaptureDevicePixelFormat PixelFormat,
    int Width,
    int Height,
    uint FrameRateNumerator,
    uint FrameRateDenominator)
{
    internal double FramesPerSecond => FrameRateDenominator == 0
        ? 0
        : (double)FrameRateNumerator / FrameRateDenominator;

    internal int RoundedFramesPerSecond => Math.Max(1, (int)Math.Round(FramesPerSecond));
}

internal static class CaptureDeviceFormatPolicy
{
    private const double PreferredFrameRateCeiling = 60.5;
    private const long PreferredPixelAreaCeiling = 1920L * 1080;

    internal static CaptureDeviceFormat? ChoosePreferredFormat(
        IEnumerable<CaptureDeviceFormat> formats)
    {
        List<CaptureDeviceFormat> supported = formats
            .Where(format => format.Width > 0 && format.Height > 0 && format.FramesPerSecond > 0)
            .ToList();
        if (supported.Count == 0) return null;

        List<CaptureDeviceFormat> withinFrameRateCeiling = supported
            .Where(format => format.FramesPerSecond <= PreferredFrameRateCeiling)
            .ToList();
        List<CaptureDeviceFormat> rateCandidates = withinFrameRateCeiling.Count > 0
            ? withinFrameRateCeiling
            : supported;

        List<CaptureDeviceFormat> withinPixelAreaCeiling = rateCandidates
            .Where(format => PixelArea(format) <= PreferredPixelAreaCeiling)
            .ToList();
        IEnumerable<CaptureDeviceFormat> candidates = withinPixelAreaCeiling.Count > 0
            ? withinPixelAreaCeiling
            : SmallestPixelAreaModes(rateCandidates);

        return PreferCheapestEquivalentModes(candidates)
            .OrderByDescending(PixelRate)
            .ThenByDescending(format => format.FramesPerSecond)
            .ThenByDescending(PixelArea)
            .ThenBy(format => PixelFormatOrder(format.PixelFormat))
            .ThenByDescending(format => format.Width)
            .ThenByDescending(format => format.Height)
            .First();
    }

    private static IEnumerable<CaptureDeviceFormat> PreferCheapestEquivalentModes(
        IEnumerable<CaptureDeviceFormat> formats) =>
        formats
            .GroupBy(format => (format.Width, format.Height, format.RoundedFramesPerSecond))
            .Select(group => group
                .OrderBy(format => PixelFormatOrder(format.PixelFormat))
                .ThenByDescending(format => format.FramesPerSecond)
                .First());

    private static IEnumerable<CaptureDeviceFormat> SmallestPixelAreaModes(
        IReadOnlyCollection<CaptureDeviceFormat> formats)
    {
        long smallestPixelArea = formats.Min(PixelArea);
        return formats.Where(format => PixelArea(format) == smallestPixelArea);
    }

    private static long PixelArea(CaptureDeviceFormat format) =>
        (long)format.Width * format.Height;

    private static double PixelRate(CaptureDeviceFormat format) =>
        PixelArea(format) * format.FramesPerSecond;

    private static int PixelFormatOrder(CaptureDevicePixelFormat format) => format switch
    {
        CaptureDevicePixelFormat.Nv12 => 0,
        CaptureDevicePixelFormat.Yuy2 => 1,
        CaptureDevicePixelFormat.Mjpeg => 2,
        _ => int.MaxValue,
    };
}
