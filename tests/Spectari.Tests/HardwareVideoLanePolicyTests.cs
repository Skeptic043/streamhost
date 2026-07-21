using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class HardwareVideoLanePolicyTests
{
    [Fact]
    public void CpuSelectionStaysOnRawvideoWithoutProbing()
    {
        EncoderAdapterIdentity nvidia = Adapter(0x10DE, "nvidia-luid", "nvidia-driver");

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            "libx264",
            new RawVideoEncoderSelection("libx264", nvidia),
            nvidia,
            textureCaptureAvailable: true,
            [nvidia],
            Parameters(),
            (_, _) => throw new InvalidOperationException("CPU must not probe hardware."));

        Assert.Equal(VideoInputLane.RawVideo, plan.Lane);
        Assert.Equal("libx264", plan.RawVideoEncoder);
        Assert.Null(plan.HardwareAdapter);
    }

    [Theory]
    [InlineData("h264_nvenc", 0x10DEu)]
    [InlineData("h264_amf", 0x1002u)]
    [InlineData("h264_qsv", 0x8086u)]
    public void ManualChoiceMapsToThatVendorsAdapter(string requested, uint vendorId)
    {
        EncoderAdapterIdentity source = Adapter(0, "", "");
        EncoderAdapterIdentity target = Adapter(vendorId, "target-luid", "target-driver");

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            requested,
            new RawVideoEncoderSelection(requested, source),
            source,
            textureCaptureAvailable: true,
            [target],
            Parameters(),
            (_, _) => HardwareEncoderProbeResult.Unavailable("phase 1"));

        Assert.Equal(target, plan.HardwareAdapter);
        Assert.Equal(VideoInputLane.RawVideo, plan.Lane);
        Assert.Equal(requested, plan.RawVideoEncoder);
    }

    [Fact]
    public void AutoUsesAdapterAlreadyResolvedByCaptureDevicePolicy()
    {
        EncoderAdapterIdentity unknownSource = Adapter(0, "", "");
        EncoderAdapterIdentity resolved = Adapter(0x10DE, "cached-luid", "cached-driver");

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            "auto",
            new RawVideoEncoderSelection("h264_nvenc", resolved),
            unknownSource,
            textureCaptureAvailable: true,
            [resolved],
            Parameters(),
            (_, _) => HardwareEncoderProbeResult.Unavailable("phase 1"));

        Assert.Equal(resolved, plan.HardwareAdapter);
    }

    [Fact]
    public void ManualChoiceEnumeratesWhenSourceLuidIsUnknown()
    {
        EncoderAdapterIdentity source = Adapter(0x10DE, "?", "driver");

        Assert.True(HardwareVideoLanePolicy.NeedsAdapterEnumeration("h264_nvenc", source));
        Assert.False(HardwareVideoLanePolicy.NeedsAdapterEnumeration(
            "h264_nvenc",
            Adapter(0x10DE, "known-luid", "driver")));
    }

    [Fact]
    public void AvailableProbeSelectsTextureLane()
    {
        EncoderAdapterIdentity nvidia = Adapter(0x10DE, "nvidia-luid", "nvidia-driver");

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            "auto",
            new RawVideoEncoderSelection("h264_nvenc", nvidia),
            nvidia,
            textureCaptureAvailable: true,
            [nvidia],
            Parameters(),
            (_, _) => HardwareEncoderProbeResult.AvailableNow());

        Assert.Equal(VideoInputLane.GpuTexture, plan.Lane);
        Assert.Equal(nvidia, plan.HardwareAdapter);
    }

    [Fact]
    public void PhaseOneUnavailableProbePreservesCurrentRawvideoFallback()
    {
        EncoderAdapterIdentity amd = Adapter(0x1002, "amd-luid", "amd-driver");
        using var encoder = new UnavailableHardwareVideoEncoder();

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            "h264_amf",
            new RawVideoEncoderSelection("h264_amf", amd),
            amd,
            textureCaptureAvailable: true,
            [amd],
            Parameters(),
            encoder.Probe);

        Assert.Equal(VideoInputLane.RawVideo, plan.Lane);
        Assert.Equal("h264_amf", plan.RawVideoEncoder);
        Assert.Equal(UnavailableHardwareVideoEncoder.UnavailableReason, plan.Reason);
    }

    [Fact]
    public void PacingFactoryBranchesOnTheResolvedLane()
    {
        var raw = new FakePacingLane();
        var hardware = new FakePacingLane();
        var rawPlan = new VideoPipelinePlan(
            VideoInputLane.RawVideo,
            "libx264",
            null,
            null,
            Parameters(),
            "raw");
        var hardwarePlan = rawPlan with { Lane = VideoInputLane.GpuTexture };

        Assert.Same(raw, VideoPacingLaneFactory.Select(rawPlan, raw, () => hardware));
        Assert.Same(hardware, VideoPacingLaneFactory.Select(hardwarePlan, raw, () => hardware));
    }

    [Fact]
    public void SourceWithoutTextureContractFallsBackBeforeProbe()
    {
        EncoderAdapterIdentity intel = Adapter(0x8086, "intel-luid", "intel-driver");

        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            "h264_qsv",
            new RawVideoEncoderSelection("h264_qsv", intel),
            intel,
            textureCaptureAvailable: false,
            [intel],
            Parameters(),
            (_, _) => throw new InvalidOperationException("No texture means no probe."));

        Assert.Equal(VideoInputLane.RawVideo, plan.Lane);
        Assert.Contains("no GPU texture", plan.Reason);
    }

    [Fact]
    public void ParameterMappingMatchesCurrentRateAndGopIntent()
    {
        HardwareVideoEncoderParameters parameters =
            HardwareVideoEncoderParameters.FromSession(1920, 1080, 60, 12_000);

        Assert.Equal(VideoCodec.H264, parameters.Codec);
        Assert.Equal(1920, parameters.Width);
        Assert.Equal(1080, parameters.Height);
        Assert.Equal(60, parameters.FramesPerSecond);
        Assert.Equal(12_000, parameters.BitrateKbps);
        Assert.Equal(15_000, parameters.MaximumBitrateKbps);
        Assert.Equal(6_000, parameters.BufferSizeKbps);
        Assert.Equal(30, parameters.GopFrames);
        Assert.Equal(VideoProfile.High, parameters.Profile);
        Assert.Equal(VideoRateControlMode.ConstantBitrate, parameters.RateControlMode);
        Assert.Equal(VideoLatencyMode.LowLatency, parameters.LatencyMode);
    }

    [Fact]
    public void ProbeTokenIsBoundToAdapterAndDriverIdentity()
    {
        string first = Assert.IsType<string>(HardwareEncoderProbeToken.Create(
            Adapter(0x10DE, "luid-a", "driver-a")));
        string changedAdapter = Assert.IsType<string>(HardwareEncoderProbeToken.Create(
            Adapter(0x10DE, "luid-b", "driver-a")));
        string changedDriver = Assert.IsType<string>(HardwareEncoderProbeToken.Create(
            Adapter(0x10DE, "luid-a", "driver-b")));

        Assert.StartsWith("mf-h264-v1:", first);
        Assert.NotEqual(first, changedAdapter);
        Assert.NotEqual(first, changedDriver);
        Assert.Null(HardwareEncoderProbeToken.Create(Adapter(0x10DE, "?", "driver-a")));
        Assert.Null(HardwareEncoderProbeToken.Create(Adapter(0x10DE, "luid-a", "?")));
    }

    private static HardwareVideoEncoderParameters Parameters() =>
        HardwareVideoEncoderParameters.FromSession(1280, 720, 60, 8_000);

    private static EncoderAdapterIdentity Adapter(uint vendorId, string luid, string driver) =>
        new(vendorId, luid, driver);

    private sealed class FakePacingLane : IVideoPacingLane
    {
        public string Run(CancellationToken cancellationToken) => "unused";
    }
}
