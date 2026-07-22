namespace Spectari.Encode;

internal enum VideoCodec
{
    H264,
}

internal enum VideoProfile
{
    High,
}

internal enum VideoRateControlMode
{
    ConstantBitrate,
}

internal enum VideoLatencyMode
{
    LowLatency,
}

internal sealed record HardwareVideoEncoderParameters(
    VideoCodec Codec,
    int Width,
    int Height,
    int FramesPerSecond,
    int BitrateKbps,
    int MaximumBitrateKbps,
    int BufferSizeKbps,
    int GopFrames,
    VideoProfile Profile,
    VideoRateControlMode RateControlMode,
    VideoLatencyMode LatencyMode)
{
    internal uint H264BufferSizeBytes =>
        checked((uint)(BufferSizeKbps * 1000L / 8L));

    internal static HardwareVideoEncoderParameters FromSession(
        int width,
        int height,
        int framesPerSecond,
        int bitrateKbps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrateKbps);

        return new HardwareVideoEncoderParameters(
            VideoCodec.H264,
            width,
            height,
            framesPerSecond,
            bitrateKbps,
            bitrateKbps * 5 / 4,
            bitrateKbps / 2,
            Math.Max(framesPerSecond / 2, 1),
            VideoProfile.High,
            VideoRateControlMode.ConstantBitrate,
            VideoLatencyMode.LowLatency);
    }
}

internal readonly record struct HardwareEncoderProbeContext(
    EncoderAdapterIdentity Adapter,
    string? ProbeCacheToken);

internal readonly record struct HardwareEncoderProbeResult(bool Available, string Reason)
{
    internal static HardwareEncoderProbeResult Unavailable(string reason) => new(false, reason);
    internal static HardwareEncoderProbeResult AvailableNow() => new(true, "available");
}

internal readonly record struct HardwareEncoderInitialization(
    EncoderAdapterIdentity Adapter,
    nint D3D11DevicePointer);

internal readonly record struct EncodedAccessUnit(ReadOnlyMemory<byte> Data, bool IsKeyFrame);

/// <summary>
/// Codec-aware encoder boundary. Native encoder API objects stay inside the implementation.
/// Input textures cross as neutral leases and output is complete H.264 access units.
/// </summary>
internal interface IHardwareVideoEncoder : IHardwarePullEncoder, IDisposable
{
    HardwareEncoderProbeResult Probe(
        HardwareEncoderProbeContext context,
        HardwareVideoEncoderParameters parameters);

    void Initialize(
        HardwareEncoderInitialization initialization,
        HardwareVideoEncoderParameters parameters);

    IReadOnlyList<EncodedAccessUnit> Shutdown();
    void Flush();
}

internal static class HardwareEncoderShutdownSequence
{
    internal static IReadOnlyList<EncodedAccessUnit> Execute(
        Action endOfStream,
        Func<IReadOnlyList<EncodedAccessUnit>> drain,
        Action endStreaming,
        Action shutdownObject,
        Action release)
    {
        IReadOnlyList<EncodedAccessUnit> output = [];
        try
        {
            endOfStream();
            output = drain();
        }
        finally
        {
            try { endStreaming(); }
            finally
            {
                try { shutdownObject(); }
                finally { release(); }
            }
        }
        return output;
    }
}

/// <summary>Keeps the hardware lane dormant when no encoder implementation exists.</summary>
internal sealed class UnavailableHardwareVideoEncoder : IHardwareVideoEncoder
{
    internal const string UnavailableReason = "no hardware encoder implementation is installed";
    public long SubmittedFrameCount => 0;

    public HardwareEncoderProbeResult Probe(
        HardwareEncoderProbeContext context,
        HardwareVideoEncoderParameters parameters) =>
        HardwareEncoderProbeResult.Unavailable(UnavailableReason);

    public void Initialize(
        HardwareEncoderInitialization initialization,
        HardwareVideoEncoderParameters parameters) =>
        throw new InvalidOperationException(UnavailableReason);

    public bool TrySubmit(
        IHardwareEncodeFrame frame,
        long presentationTime100ns,
        long duration100ns)
    {
        frame.Return(FrameLeaseReturnReason.InputRejected);
        throw new InvalidOperationException(UnavailableReason);
    }

    public IReadOnlyList<EncodedAccessUnit> Poll(long nowTicks) => [];
    public HardwarePullEncoderProgress GetProgressSnapshot() => new(0, 0, 0, 0);
    public IReadOnlyList<EncodedAccessUnit> Shutdown() => [];
    public void Flush() { }
    public void Dispose() { }
}
