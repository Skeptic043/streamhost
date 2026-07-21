using Spectari.Capture;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Spectari.Encode;

internal enum Nv12ConvertFailure
{
    None,
    PoolExhausted,
    TextureUnavailable,
    CpuFrameOnly,
    CrossAdapterUnsupported,
    CaptureCopyFailed,
    ConversionFailed,
}

/// <summary>
/// Creates the lane device at the selected adapter. Same-adapter capture retains
/// its device so a retired window-capture wrapper cannot invalidate the lane.
/// </summary>
internal sealed class GpuLaneDevice : IDisposable
{
    private readonly bool _ownsDevice;

    internal GpuLaneDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        string adapterLuid,
        bool ownsDevice)
    {
        Device = device;
        Context = context;
        AdapterLuid = adapterLuid;
        _ownsDevice = ownsDevice;
    }

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    internal string AdapterLuid { get; }

    public void Dispose()
    {
        if (!_ownsDevice) return;
        Context.Dispose();
        Device.Dispose();
    }
}

internal static class GpuLaneDeviceFactory
{
    internal static GpuLaneDevice Create(
        GpuTextureCaptureFrame capture,
        EncoderAdapterIdentity targetAdapter)
    {
        if (SameLuid(capture.AdapterLuid, targetAdapter.Luid))
        {
            Marshal.AddRef(capture.Device.NativePointer);
            try { Marshal.AddRef(capture.Context.NativePointer); }
            catch
            {
                Marshal.Release(capture.Device.NativePointer);
                throw;
            }
            return new GpuLaneDevice(
                new ID3D11Device(capture.Device.NativePointer),
                new ID3D11DeviceContext(capture.Context.NativePointer),
                targetAdapter.Luid,
                ownsDevice: true);
        }

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0;
             factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success;
             index++)
        {
            if (adapter is null) continue;
            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                string luid = $"{description.Luid.HighPart}:{description.Luid.LowPart}";
                if (!SameLuid(luid, targetAdapter.Luid)) continue;

                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    null!,
                    out ID3D11Device? device,
                    out _,
                    out ID3D11DeviceContext? context).CheckError();
                using (var multithread = context!.QueryInterfaceOrNull<ID3D11Multithread>())
                    multithread?.SetMultithreadProtected(true);
                return new GpuLaneDevice(device!, context, luid, ownsDevice: true);
            }
        }

        throw new InvalidOperationException(
            $"GPU lane adapter {targetAdapter.Luid} is no longer available.");
    }

    private static bool SameLuid(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// GPU scale and BGRA-to-NV12 conversion on the pacing thread. Pool rent is the
/// only admission point and fails immediately when every encoder lease is out.
/// </summary>
internal sealed class Nv12FrameConverter : IDisposable
{
    private readonly GpuTextureCaptureFrame _capture;
    private readonly GpuLaneDevice _laneDevice;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _bgraInput;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11Texture2D _waitingNv12;
    private readonly ID3D11VideoProcessorOutputView _waitingOutputView;
    private readonly Nv12TexturePool _pool;
    private readonly bool _sameAdapterDevice;
    private int _failureLogged;
    private bool _waitingFrameUploaded;
    private bool _disposed;

    internal Nv12FrameConverter(
        GpuTextureCaptureFrame capture,
        EncoderAdapterIdentity targetAdapter,
        int outputWidth,
        int outputHeight,
        int framesPerSecond,
        int poolCapacity = 3)
    {
        _capture = capture;
        _laneDevice = GpuLaneDeviceFactory.Create(capture, targetAdapter);
        _sameAdapterDevice = capture.Device.NativePointer == _laneDevice.Device.NativePointer;
        try
        {
            _videoDevice = _laneDevice.Device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _laneDevice.Context.QueryInterface<ID3D11VideoContext>();

            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)framesPerSecond, 1),
                InputWidth = (uint)capture.Width,
                InputHeight = (uint)capture.Height,
                OutputFrameRate = new Rational((uint)framesPerSecond, 1),
                OutputWidth = (uint)outputWidth,
                OutputHeight = (uint)outputHeight,
                Usage = VideoUsage.PlaybackNormal,
            };
            _videoDevice.CreateVideoProcessorEnumerator(
                ref content,
                out _enumerator).CheckError();
            _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

            _bgraInput = _laneDevice.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)capture.Width,
                Height = (uint)capture.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            var inputDescription = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            };
            _videoDevice.CreateVideoProcessorInputView(
                _bgraInput,
                _enumerator,
                inputDescription,
                out _inputView).CheckError();
            _pool = new Nv12TexturePool(
                _laneDevice.Device,
                outputWidth,
                outputHeight,
                targetAdapter.Luid,
                poolCapacity);
            _waitingNv12 = _laneDevice.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            _videoDevice.CreateVideoProcessorOutputView(
                _waitingNv12,
                _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                },
                out _waitingOutputView).CheckError();

            _videoContext.VideoProcessorSetStreamSourceRect(
                _processor,
                0,
                true,
                new RawRect(0, 0, capture.Width, capture.Height));
            _videoContext.VideoProcessorSetStreamDestRect(
                _processor,
                0,
                true,
                new RawRect(0, 0, outputWidth, outputHeight));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal FrameLeaseAccounting PoolAccounting => _pool.GetAccounting();
    internal nint DevicePointer => _laneDevice.Device.NativePointer;

    internal bool TryConvert(
        IGpuTextureCaptureSource captureSource,
        out VideoFrameLease? lease,
        out Nv12ConvertFailure failure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GpuTextureCaptureStatus status = captureSource.TryGetGpuTexture(out GpuTextureCaptureFrame? frame);
        if (status == GpuTextureCaptureStatus.CpuFrameOnly &&
            captureSource is IGpuWaitingFrameSource waitingSource)
        {
            return TryCopyWaitingFrame(waitingSource, out lease, out failure);
        }
        if (status != GpuTextureCaptureStatus.Available || frame is null)
        {
            lease = null;
            failure = status == GpuTextureCaptureStatus.CpuFrameOnly
                ? Nv12ConvertFailure.CpuFrameOnly
                : Nv12ConvertFailure.TextureUnavailable;
            return false;
        }
        if (!_sameAdapterDevice || frame.Device.NativePointer != _capture.Device.NativePointer)
        {
            lease = null;
            failure = Nv12ConvertFailure.CrossAdapterUnsupported;
            return false;
        }
        if (!_pool.TryRent(out lease))
        {
            failure = Nv12ConvertFailure.PoolExhausted;
            return false;
        }
        VideoFrameLease rented = lease!;

        try
        {
            if (!frame.CopyLatest(_bgraInput))
            {
                rented.Return(FrameLeaseReturnReason.InputRejected);
                lease = null;
                failure = Nv12ConvertFailure.CaptureCopyFailed;
                return false;
            }

            ID3D11Texture2D outputTexture = rented.NativeTexture
                ?? throw new InvalidOperationException("NV12 pool lease lost its texture.");
            var outputDescription = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            };
            _videoDevice.CreateVideoProcessorOutputView(
                outputTexture,
                _enumerator,
                outputDescription,
                out ID3D11VideoProcessorOutputView? outputView).CheckError();
            using (ID3D11VideoProcessorOutputView view = outputView!)
            {
                var stream = new VideoProcessorStream
                {
                    Enable = true,
                    InputSurface = _inputView,
                };
                _videoContext.VideoProcessorBlt(
                    _processor,
                    view,
                    0,
                    1,
                    [stream]).CheckError();
            }

            failure = Nv12ConvertFailure.None;
            return true;
        }
        catch (Exception ex)
        {
            rented.Return(FrameLeaseReturnReason.Failure);
            lease = null;
            failure = Nv12ConvertFailure.ConversionFailed;
            ReportFailureOnce("frame conversion", ex);
            return false;
        }
    }

    private bool TryCopyWaitingFrame(
        IGpuWaitingFrameSource waitingSource,
        out VideoFrameLease? lease,
        out Nv12ConvertFailure failure)
    {
        lease = null;
        try
        {
            if (!_waitingFrameUploaded)
            {
                if (!waitingSource.TryGetWaitingFrame(out ReadOnlyMemory<byte> bgra))
                {
                    lease = null;
                    failure = Nv12ConvertFailure.CpuFrameOnly;
                    return false;
                }
                _laneDevice.Context.UpdateSubresource(
                    bgra.Span,
                    _bgraInput,
                    0,
                    checked((uint)_capture.Width * 4),
                    0);
                var stream = new VideoProcessorStream
                {
                    Enable = true,
                    InputSurface = _inputView,
                };
                _videoContext.VideoProcessorBlt(
                    _processor,
                    _waitingOutputView,
                    0,
                    1,
                    [stream]).CheckError();
                _waitingFrameUploaded = true;
                Console.WriteLine("[gpu-convert] waiting screen uploaded once to the GPU NV12 lane.");
            }

            if (!_pool.TryRent(out lease))
            {
                failure = Nv12ConvertFailure.PoolExhausted;
                return false;
            }
            ID3D11Texture2D outputTexture = lease!.NativeTexture
                ?? throw new InvalidOperationException("NV12 pool lease lost its texture.");
            _laneDevice.Context.CopyResource(outputTexture, _waitingNv12);
            failure = Nv12ConvertFailure.None;
            return true;
        }
        catch (Exception ex)
        {
            lease?.Return(FrameLeaseReturnReason.Failure);
            lease = null;
            failure = Nv12ConvertFailure.ConversionFailed;
            ReportFailureOnce("waiting-frame upload", ex);
            return false;
        }
    }

    internal bool TryDuplicate(
        VideoFrameLease source,
        out VideoFrameLease? duplicate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pool.TryRent(out duplicate))
            return false;

        VideoFrameLease rented = duplicate!;
        try
        {
            ID3D11Texture2D sourceTexture = source.NativeTexture
                ?? throw new InvalidOperationException("Source NV12 lease lost its texture.");
            ID3D11Texture2D destinationTexture = rented.NativeTexture
                ?? throw new InvalidOperationException("Duplicate NV12 lease lost its texture.");
            _laneDevice.Context.CopyResource(destinationTexture, sourceTexture);
            return true;
        }
        catch (Exception ex)
        {
            rented.Return(FrameLeaseReturnReason.Failure);
            duplicate = null;
            ReportFailureOnce("debt-frame duplication", ex);
            return false;
        }
    }

    private void ReportFailureOnce(string action, Exception error)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) != 0) return;
        Console.Error.WriteLine(
            $"[gpu-convert] {action} failed: {error.Message.Replace('\r', ' ').Replace('\n', ' ')}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool?.Dispose();
        _waitingOutputView?.Dispose();
        _waitingNv12?.Dispose();
        _inputView?.Dispose();
        _bgraInput?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
        _laneDevice?.Dispose();
    }
}
