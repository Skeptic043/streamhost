using System.Security.Cryptography;
using System.Text;

namespace Spectari.Encode;

internal enum VideoInputLane
{
    RawVideo,
    GpuTexture,
}

internal readonly record struct VideoPipelinePlan(
    VideoInputLane Lane,
    string RawVideoEncoder,
    EncoderAdapterIdentity? HardwareAdapter,
    string? HardwareProbeCacheToken,
    HardwareVideoEncoderParameters Parameters,
    string Reason,
    bool RequiresSessionCpuRecovery = false);

internal static class HardwareEncoderProbeToken
{
    internal static string? Create(EncoderAdapterIdentity adapter)
    {
        if (!IdentityKnown(adapter.Luid) || !IdentityKnown(adapter.DriverVersion))
            return null;

        string identity = string.Join('\n',
            "probe=mf-h264-v1",
            $"vendor={adapter.VendorId:X}",
            $"adapter={adapter.Luid}",
            $"driver={adapter.DriverVersion}");
        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"mf-h264-v1:{adapter.VendorId:X}:{digest}";
    }

    private static bool IdentityKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value != "?" &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Single owner for GPU-lane adapter choice, probe, and rawvideo fallback.</summary>
internal static class HardwareVideoLanePolicy
{
    internal static VideoPipelinePlan Select(
        string? requested,
        RawVideoEncoderSelection rawVideo,
        EncoderAdapterIdentity sourceAdapter,
        bool textureCaptureAvailable,
        IReadOnlyList<EncoderAdapterIdentity> adapters,
        HardwareVideoEncoderParameters parameters,
        Func<HardwareEncoderProbeContext, HardwareVideoEncoderParameters, HardwareEncoderProbeResult> probe)
    {
        if (string.Equals(requested, "libx264", StringComparison.OrdinalIgnoreCase))
            return Raw(rawVideo, parameters, null, null, "CPU encoder selected");

        if (!textureCaptureAvailable)
            return Raw(rawVideo, parameters, null, null, "capture source has no GPU texture contract");

        EncoderAdapterIdentity? target = ResolveTargetAdapter(
            requested,
            rawVideo.Adapter,
            sourceAdapter,
            adapters);
        if (target is null)
        {
            HardwareFallbackDecision fallback = HardwareFallbackClassifier.Startup(
                HardwareFallbackKind.AdapterMismatch,
                "no matching hardware adapter was resolved");
            return Raw(
                rawVideo,
                parameters,
                null,
                null,
                fallback.Reason,
                "libx264",
                requiresSessionCpuRecovery: true);
        }

        if (!target.Value.Luid.Equals(sourceAdapter.Luid, StringComparison.OrdinalIgnoreCase))
        {
            HardwareFallbackDecision fallback = HardwareFallbackClassifier.Startup(
                HardwareFallbackKind.AdapterMismatch,
                "capture and encoder adapters differ; cross-adapter zero-copy is unavailable");
            return Raw(
                rawVideo,
                parameters,
                target,
                null,
                fallback.Reason,
                "libx264",
                requiresSessionCpuRecovery: true);
        }

        string? token = HardwareEncoderProbeToken.Create(target.Value);
        HardwareEncoderProbeResult result = probe(
            new HardwareEncoderProbeContext(target.Value, token),
            parameters);
        if (result.Available)
        {
            return new VideoPipelinePlan(
                VideoInputLane.GpuTexture,
                rawVideo.Encoder,
                target,
                token,
                parameters,
                "hardware texture encoder is available");
        }

        HardwareFallbackDecision probeFallback = HardwareFallbackClassifier.Startup(
            HardwareFallbackKind.Probe,
            result.Reason);
        return Raw(
            rawVideo,
            parameters,
            target,
            token,
            probeFallback.Reason,
            "libx264",
            requiresSessionCpuRecovery: true);
    }

    internal static EncoderAdapterIdentity? ResolveTargetAdapter(
        string? requested,
        EncoderAdapterIdentity autoAdapter,
        EncoderAdapterIdentity sourceAdapter,
        IReadOnlyList<EncoderAdapterIdentity> adapters)
    {
        if (string.IsNullOrEmpty(requested) || requested == "auto")
            return FfmpegEncoder.HasHardwareEncoder(autoAdapter.VendorId) &&
                IdentityKnown(autoAdapter.Luid)
                ? autoAdapter
                : null;

        uint? vendor = RequestedVendor(requested);
        if (vendor is null)
            return null;
        if (sourceAdapter.VendorId == vendor.Value && IdentityKnown(sourceAdapter.Luid))
            return sourceAdapter;

        return adapters.FirstOrDefault(adapter =>
            adapter.VendorId == vendor.Value && IdentityKnown(adapter.Luid)) is { VendorId: not 0 } match
            ? match
            : null;
    }

    internal static uint? RequestedVendor(string? requested) => requested switch
    {
        "h264_nvenc" => 0x10DE,
        "h264_amf" => 0x1002,
        "h264_qsv" => 0x8086,
        _ => null,
    };

    internal static bool NeedsAdapterEnumeration(
        string? requested,
        EncoderAdapterIdentity sourceAdapter) =>
        RequestedVendor(requested) is uint vendor &&
        (sourceAdapter.VendorId != vendor || !IdentityKnown(sourceAdapter.Luid));

    private static VideoPipelinePlan Raw(
        RawVideoEncoderSelection rawVideo,
        HardwareVideoEncoderParameters parameters,
        EncoderAdapterIdentity? target,
        string? token,
        string reason,
        string? rawVideoEncoder = null,
        bool requiresSessionCpuRecovery = false) => new(
            VideoInputLane.RawVideo,
            rawVideoEncoder ?? rawVideo.Encoder,
            target,
            token,
            parameters,
            reason,
            requiresSessionCpuRecovery);

    private static bool IdentityKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value != "?" &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase);
}
