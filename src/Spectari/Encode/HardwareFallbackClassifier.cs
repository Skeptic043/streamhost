namespace Spectari.Encode;

internal enum HardwareFallbackKind
{
    None,
    Probe,
    Initialization,
    AdapterMismatch,
    SustainedFrameDebt,
    RuntimeFailure,
}

internal readonly record struct HardwareFallbackDecision(
    HardwareFallbackKind Kind,
    bool StartCpuRecovery,
    string Reason);

internal static class HardwareFallbackClassifier
{
    internal static HardwareFallbackDecision Startup(
        HardwareFallbackKind kind,
        string reason) => kind switch
        {
            HardwareFallbackKind.Probe or
            HardwareFallbackKind.Initialization or
            HardwareFallbackKind.AdapterMismatch => new(
                kind,
                true,
                $"video pipeline stalled before hardware texture lane startup: {reason}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    internal static HardwareFallbackDecision Runtime(
        HardwareFallbackKind kind,
        string reason) => kind switch
        {
            HardwareFallbackKind.SustainedFrameDebt or
            HardwareFallbackKind.RuntimeFailure => new(kind, true,
                $"video pipeline stalled in the hardware texture lane: {reason}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
