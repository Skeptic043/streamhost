namespace Spectari;

internal readonly record struct VideoPipelineStallDecision(
    bool ConfirmedEncoderOrOutputFailure,
    bool PermitCpuRecovery,
    string StopReason);

internal static class VideoPipelineStallPolicy
{
    internal static VideoPipelineStallDecision Classify(
        bool sustainedInput,
        bool inputWriteBlocked)
    {
        if (sustainedInput)
        {
            return new VideoPipelineStallDecision(
                true,
                true,
                "video pipeline stalled after sustained video-input delivery; see log");
        }

        if (inputWriteBlocked)
        {
            return new VideoPipelineStallDecision(
                true,
                true,
                "video pipeline stalled at ffmpeg stdin; see log");
        }

        return new VideoPipelineStallDecision(
            false,
            false,
            "video pipeline stopped after an upstream capture, conversion, or pacing stall; see log");
    }
}
