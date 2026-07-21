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
}

/// <summary>
/// A current BGRA texture source. CopyLatest only queues a GPU copy into a texture
/// on this device, so the pacing thread never maps capture memory to the CPU.
/// </summary>
internal sealed class GpuTextureCaptureFrame
{
    private readonly Func<ID3D11Texture2D, bool> _copyLatest;

    internal GpuTextureCaptureFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        string adapterLuid,
        Func<ID3D11Texture2D, bool> copyLatest)
    {
        Device = device;
        Context = context;
        Width = width;
        Height = height;
        AdapterLuid = adapterLuid;
        _copyLatest = copyLatest;
    }

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal string AdapterLuid { get; }

    internal bool CopyLatest(ID3D11Texture2D destination) => _copyLatest(destination);
}
