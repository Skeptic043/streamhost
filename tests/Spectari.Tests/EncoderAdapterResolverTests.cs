using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class EncoderAdapterResolverTests
{
    [Fact]
    public void CachedVerdictSelectsItsExactAdapter()
    {
        EncoderAdapterIdentity intel = Adapter(0x8086, "intel-luid", "intel-driver");
        EncoderAdapterIdentity nvidia = Adapter(0x10DE, "nvidia-luid", "nvidia-driver");

        EncoderAdapterIdentity? selected = EncoderAdapterResolver.ChooseCaptureDeviceAdapter(
            [intel, nvidia],
            "nvidia-token",
            adapter => adapter == nvidia ? "nvidia-token" : "intel-token");

        Assert.Equal(nvidia, selected);
    }

    [Fact]
    public void VerdictFromDifferentHardwareIsNotTrusted()
    {
        EncoderAdapterIdentity intel = Adapter(0x8086, "intel-luid", "intel-driver");
        EncoderAdapterIdentity nvidia = Adapter(0x10DE, "nvidia-luid", "nvidia-driver");

        EncoderAdapterIdentity? selected = EncoderAdapterResolver.ChooseCaptureDeviceAdapter(
            [intel, nvidia],
            "removed-adapter-token",
            adapter => adapter == intel ? "intel-token" : "nvidia-token");

        Assert.Equal(intel, selected);
    }

    [Fact]
    public void MissingVerdictSelectsFirstSupportedAdapterForProbe()
    {
        EncoderAdapterIdentity unsupported = Adapter(0x1234, "other-luid", "other-driver");
        EncoderAdapterIdentity nvidia = Adapter(0x10DE, "nvidia-luid", "");
        EncoderAdapterIdentity amd = Adapter(0x1002, "amd-luid", "amd-driver");

        EncoderAdapterIdentity? selected = EncoderAdapterResolver.ChooseCaptureDeviceAdapter(
            [unsupported, nvidia, amd],
            null,
            _ => throw new InvalidOperationException("No cache means no token comparison."));

        Assert.Equal(nvidia, selected);
    }

    [Fact]
    public void NoSupportedAdapterKeepsCpuSafePath()
    {
        EncoderAdapterIdentity? selected = EncoderAdapterResolver.ChooseCaptureDeviceAdapter(
            [Adapter(0x1234, "other-luid", "other-driver")],
            null,
            _ => null);

        Assert.Null(selected);
    }

    [Fact]
    public void ManualEncoderDoesNotResolveCaptureDeviceAdapter()
    {
        string selected = EncoderAdapterResolver.Select(
            Adapter(0, "", ""),
            captureDeviceSelected: true,
            requested: "h264_nvenc");

        Assert.Equal("h264_nvenc", selected);
    }

    private static EncoderAdapterIdentity Adapter(uint vendorId, string luid, string driver) =>
        new(vendorId, luid, driver);
}
