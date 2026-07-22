using Vortice.Direct3D11;

namespace Spectari.Capture;

internal enum GpuTextureCaptureStatus
{
    Available,
    CpuFrameOnly,
    Unavailable,
}

internal interface IGpuTextureCaptureSource
{
    GpuTextureCaptureStatus TryGetGpuTexture(out GpuTextureCaptureFrame? frame);

    bool TryCopyLatestGpuTexture(
        GpuTextureCaptureFrame expectedFrame,
        ID3D11Texture2D destination,
        out long captureVersion);
}

internal interface IGpuWaitingFrameSource
{
    bool TryGetWaitingFrame(out ReadOnlyMemory<byte> bgraFrame);
}

/// <summary>
/// A current BGRA texture source. CopyLatest only queues a GPU copy into a texture
/// on this device, so the pacing thread never maps capture memory to the CPU.
/// </summary>
internal sealed class GpuTextureCaptureFrame
{
    internal GpuTextureCaptureFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        string adapterLuid)
    {
        Device = device;
        Context = context;
        Width = width;
        Height = height;
        AdapterLuid = adapterLuid;
    }

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal string AdapterLuid { get; }
}
