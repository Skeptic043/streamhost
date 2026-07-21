using Spectari.Capture;

namespace Spectari.Encode;

internal readonly record struct HardwareCaptureAdapterPreference(
    IReadOnlyList<EncoderAdapterIdentity> Adapters,
    string? Luid)
{
    internal static HardwareCaptureAdapterPreference Resolve(
        bool windowCapture,
        string? requestedEncoder)
    {
        if (!windowCapture ||
            HardwareVideoLanePolicy.RequestedVendor(requestedEncoder) is not uint vendor)
        {
            return new HardwareCaptureAdapterPreference([], null);
        }

        IReadOnlyList<EncoderAdapterIdentity> adapters =
            EncoderAdapterResolver.EnumerateHardwareAdapters();
        string? luid = adapters.FirstOrDefault(adapter => adapter.VendorId == vendor).Luid;
        return new HardwareCaptureAdapterPreference(adapters, luid);
    }
}

/// <summary>Owns hardware-lane selection, initialization, and ordered teardown.</summary>
internal sealed class HardwareVideoPipeline : IDisposable
{
    private readonly MediaFoundationH264Encoder _encoder;
    private bool _disposed;

    private HardwareVideoPipeline(
        VideoPipelinePlan plan,
        MediaFoundationH264Encoder encoder,
        Nv12FrameConverter? converter)
    {
        Plan = plan;
        _encoder = encoder;
        Converter = converter;
    }

    internal VideoPipelinePlan Plan { get; }
    internal IHardwareVideoEncoder Encoder => _encoder;
    internal Nv12FrameConverter? Converter { get; }
    internal FfmpegVideoInput FfmpegInput => Plan.Lane == VideoInputLane.GpuTexture
        ? FfmpegVideoInput.H264AnnexB
        : FfmpegVideoInput.RawBgra;
    internal string ActiveEncoderName(string rawVideoEncoder) =>
        Plan.Lane == VideoInputLane.GpuTexture ? _encoder.FriendlyName : rawVideoEncoder;

    internal static HardwareVideoPipeline Create(
        ICaptureSource capture,
        string? requestedEncoder,
        RawVideoEncoderSelection rawVideo,
        EncoderAdapterIdentity sourceAdapter,
        HardwareCaptureAdapterPreference captureAdapterPreference,
        int outputWidth,
        int outputHeight,
        int framesPerSecond,
        int bitrateKbps)
    {
        IReadOnlyList<EncoderAdapterIdentity> hardwareAdapters =
            HardwareVideoLanePolicy.NeedsAdapterEnumeration(requestedEncoder, sourceAdapter)
                ? captureAdapterPreference.Adapters.Count > 0
                    ? captureAdapterPreference.Adapters
                    : EncoderAdapterResolver.EnumerateHardwareAdapters()
                : [rawVideo.Adapter, sourceAdapter];
        HardwareVideoEncoderParameters parameters = HardwareVideoEncoderParameters.FromSession(
            outputWidth,
            outputHeight,
            framesPerSecond,
            bitrateKbps);
        var encoder = new MediaFoundationH264Encoder();
        VideoPipelinePlan plan = HardwareVideoLanePolicy.Select(
            requestedEncoder,
            rawVideo,
            sourceAdapter,
            capture is IGpuTextureCaptureSource,
            hardwareAdapters,
            parameters,
            encoder.Probe);

        Nv12FrameConverter? converter = null;
        if (plan.Lane == VideoInputLane.GpuTexture)
        {
            try
            {
                var gpuCapture = (IGpuTextureCaptureSource)capture;
                if (gpuCapture.TryGetGpuTexture(out GpuTextureCaptureFrame? firstTexture) !=
                    GpuTextureCaptureStatus.Available || firstTexture is null)
                {
                    throw new InvalidOperationException(
                        "capture did not expose its first GPU texture after first-frame readiness");
                }
                converter = new Nv12FrameConverter(
                    firstTexture,
                    plan.HardwareAdapter!.Value,
                    outputWidth,
                    outputHeight,
                    framesPerSecond);
                encoder.Initialize(
                    new HardwareEncoderInitialization(
                        plan.HardwareAdapter.Value,
                        converter.DevicePointer),
                    parameters);
            }
            catch (Exception ex)
            {
                converter?.Dispose();
                converter = null;
                HardwareFallbackDecision fallback = HardwareFallbackClassifier.Startup(
                    HardwareFallbackKind.Initialization,
                    $"Media Foundation initialization failed: {SingleLine(ex.Message)}");
                plan = plan with
                {
                    Lane = VideoInputLane.RawVideo,
                    RawVideoEncoder = "libx264",
                    Reason = fallback.Reason,
                    RequiresSessionCpuRecovery = fallback.StartCpuRecovery,
                };
            }
        }

        if (plan.Lane == VideoInputLane.RawVideo)
            encoder.Dispose();

        return new HardwareVideoPipeline(plan, encoder, converter);
    }

    internal void LogActivePath(string rawVideoEncoder)
    {
        if (Plan.Lane == VideoInputLane.GpuTexture)
        {
            Console.WriteLine(
                $"[encoder] active video path: GPU NV12 textures -> Media Foundation H.264 ({_encoder.FriendlyName}) -> ffmpeg stream copy.");
            Console.WriteLine(
                $"[encoder] GPU texture path: capture, conversion, and encoder share adapter {Plan.HardwareAdapter!.Value.Luid}.");
            return;
        }

        Console.WriteLine(
            $"[encoder] hardware texture lane unavailable: {Plan.Reason}; using raw BGRA -> ffmpeg {rawVideoEncoder}.");
        Console.WriteLine(
            $"[encoder] active video path: raw BGRA readback -> ffmpeg {rawVideoEncoder}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encoder.Dispose(); }
        finally { Converter?.Dispose(); }
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
