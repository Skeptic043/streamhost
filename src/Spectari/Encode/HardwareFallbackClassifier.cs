namespace Spectari.Encode;

internal enum HardwareFallbackKind
{
    None,
    Probe,
    Initialization,
    AdapterMismatch,
    EncoderCreditFamine,
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
                false,
                $"hardware texture lane unavailable: {reason}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    internal static HardwareFallbackDecision Runtime(
        HardwareFallbackKind kind,
        string reason) => kind switch
        {
            HardwareFallbackKind.EncoderCreditFamine or
            HardwareFallbackKind.RuntimeFailure => new(kind, true,
                $"video pipeline stalled in the hardware texture lane: {reason}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
