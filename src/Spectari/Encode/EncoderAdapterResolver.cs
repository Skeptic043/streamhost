using Spectari.Capture;
using Vortice.DXGI;

namespace Spectari.Encode;

internal readonly record struct EncoderAdapterIdentity(
    uint VendorId,
    string Luid,
    string DriverVersion);

internal readonly record struct RawVideoEncoderSelection(
    string Encoder,
    EncoderAdapterIdentity Adapter);

internal static class EncoderAdapterResolver
{
    internal static string Select(
        ICaptureSource capture,
        bool captureDeviceSelected,
        string? requested) =>
        Resolve(
            new EncoderAdapterIdentity(
                capture.GpuVendorId,
                capture.AdapterLuid,
                capture.DriverVersion),
            captureDeviceSelected,
            requested).Encoder;

    internal static string Select(
        EncoderAdapterIdentity sourceAdapter,
        bool captureDeviceSelected,
        string? requested) =>
        Resolve(sourceAdapter, captureDeviceSelected, requested).Encoder;

    internal static RawVideoEncoderSelection Resolve(
        ICaptureSource capture,
        bool captureDeviceSelected,
        string? requested) =>
        Resolve(
            new EncoderAdapterIdentity(
                capture.GpuVendorId,
                capture.AdapterLuid,
                capture.DriverVersion),
            captureDeviceSelected,
            requested);

    internal static RawVideoEncoderSelection Resolve(
        EncoderAdapterIdentity sourceAdapter,
        bool captureDeviceSelected,
        string? requested)
    {
        EncoderAdapterIdentity adapter = sourceAdapter;
        if (captureDeviceSelected && (string.IsNullOrEmpty(requested) || requested == "auto"))
            adapter = ResolveCaptureDeviceAdapter() ?? sourceAdapter;

        return new RawVideoEncoderSelection(
            FfmpegEncoder.PickEncoder(
                adapter.VendorId,
                adapter.Luid,
                adapter.DriverVersion,
                requested),
            adapter);
    }

    internal static EncoderAdapterIdentity? ChooseCaptureDeviceAdapter(
        IReadOnlyList<EncoderAdapterIdentity> adapters,
        string? cachedProbeToken,
        Func<EncoderAdapterIdentity, string?> expectedProbeToken)
    {
        EncoderAdapterIdentity[] candidates = adapters
            .Where(adapter => FfmpegEncoder.HasHardwareEncoder(adapter.VendorId))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(cachedProbeToken))
        {
            foreach (EncoderAdapterIdentity candidate in candidates)
            {
                string? expected = expectedProbeToken(candidate);
                if (expected is not null &&
                    string.Equals(expected, cachedProbeToken, StringComparison.Ordinal))
                    return candidate;
            }
        }

        return candidates[0];
    }

    private static EncoderAdapterIdentity? ResolveCaptureDeviceAdapter()
    {
        IReadOnlyList<EncoderAdapterIdentity> adapters = EnumerateHardwareAdapters();
        string? cachedProbeToken = FfmpegEncoder.ReadCachedProbeToken();
        var ffmpeg = new Lazy<(string version, string buildconf, string sha256)>(
            FfmpegEncoder.FfmpegBuildInfo);

        return ChooseCaptureDeviceAdapter(
            adapters,
            cachedProbeToken,
            adapter => FfmpegEncoder.ExpectedProbeToken(
                adapter.VendorId,
                adapter.Luid,
                adapter.DriverVersion,
                ffmpeg.Value));
    }

    internal static IReadOnlyList<EncoderAdapterIdentity> EnumerateHardwareAdapters()
    {
        var adapters = new List<EncoderAdapterIdentity>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0;
                 factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success;
                 index++)
            {
                if (adapter is null)
                    continue;

                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0 ||
                        description.VendorId == 0x1414)
                        continue;

                    adapters.Add(new EncoderAdapterIdentity(
                        (uint)description.VendorId,
                        $"{description.Luid.HighPart}:{description.Luid.LowPart}",
                        ReadDriverVersion(adapter)));
                }
            }
        }
        catch
        {
            Console.Error.WriteLine(
                "[encoder] hardware adapter enumeration failed; capture-device Auto will use CPU.");
        }

        return adapters;
    }

    private static string ReadDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice), out long umd))
            {
                return $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}." +
                    $"{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
            }
        }
        catch
        {
        }

        return "?";
    }
}
