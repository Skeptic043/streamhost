using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectari.Util;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Spectari.Capture;

/// <summary>
/// Windows.Graphics.Capture of one monitor or window. Keeps the most recent frame in a GPU
/// texture; TryReadFrame does a staging readback of that frame on demand, so the
/// caller controls the output frame rate independent of the capture rate.
/// </summary>
public sealed class ScreenCapture : ICaptureSource, ICaptureDiagnostics, IGpuTextureCaptureSource
{
    private const DirectXPixelFormat CaptureFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int CaptureBufferCount = 2;

    private const string ScaleVertexShaderSource = """
        struct VertexOutput
        {
            float4 Position : SV_POSITION;
            float2 TexCoord : TEXCOORD0;
        };

        VertexOutput main(uint vertexId : SV_VertexID)
        {
            VertexOutput output;
            float2 texCoord = float2((vertexId << 1) & 2, vertexId & 2);
            output.Position = float4(texCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            output.TexCoord = texCoord;
            return output;
        }
        """;

    private const string ScalePixelShaderSource = """
        Texture2D SourceTexture : register(t0);
        SamplerState LinearSampler : register(s0);

        float4 main(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
        {
            return SourceTexture.Sample(LinearSampler, texCoord);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly bool _writeDiagnostics;
    private readonly WindowFrameDeliveryRateTracker? _windowDeliveryRate;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _latest;
    private readonly ID3D11RenderTargetView _latestRtv;
    private readonly GpuTextureCaptureFrame _gpuTextureFrame;
    private ID3D11Texture2D? _scaleSource;
    private ID3D11ShaderResourceView? _scaleSourceView;
    private ID3D11VertexShader? _scaleVertexShader;
    private ID3D11PixelShader? _scalePixelShader;
    private ID3D11SamplerState? _scaleSampler;
    private int _scaleSourceWidth;
    private int _scaleSourceHeight;
    private readonly ID3D11Texture2D _stagingA;
    private readonly ID3D11Texture2D _stagingB;
    private bool _flip;
    private bool _primed;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _frameSignal = new(false);
    private volatile bool _hasFrame;
    private volatile bool _disposing;
    private long _callbacksStarted;
    private long _framesArrived;
    private long _readbacksStarted;
    private long _readbacksCompleted;
    private long _lastCallbackTicks;
    private long _lastFrameReadyTicks;
    private long _lastReadbackStartedTicks;
    private long _lastReadbackCompletedTicks;
    private int _callbackStage;
    private int _readbackStage;
    private int _contentWidth;
    private int _contentHeight;
    private int _reportedContentWidth;
    private int _reportedContentHeight;
    private int _poolWidth;
    private int _poolHeight;
    private volatile bool _targetWasClosed;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; } = "?";
    public string AdapterLuid { get; } = "?";
    public string DriverVersion { get; } = "?";
    public long FramesArrived => Interlocked.Read(ref _framesArrived);
    internal bool TargetWasClosed => _targetWasClosed;
    internal event Action<ScreenCapture>? TargetClosed;

    /// <summary>First exception thrown inside the frame callback, if any - a set value
    /// means the capture pipeline is dead and the session should stop with a real error.</summary>
    private volatile Exception? _captureError;
    public Exception? CaptureError => _captureError;

    /// <summary>Monotonic count of frames delivered by the compositor - compare across calls to detect fresh content.</summary>
    public long FrameVersion => Interlocked.Read(ref _framesArrived);

    CaptureProgressSnapshot ICaptureDiagnostics.GetProgressSnapshot() => new(
        Interlocked.Read(ref _callbacksStarted),
        Interlocked.Read(ref _framesArrived),
        Interlocked.Read(ref _readbacksStarted),
        Interlocked.Read(ref _readbacksCompleted),
        Interlocked.Read(ref _lastCallbackTicks),
        Interlocked.Read(ref _lastFrameReadyTicks),
        Interlocked.Read(ref _lastReadbackStartedTicks),
        Interlocked.Read(ref _lastReadbackCompletedTicks),
        CallbackStageName(Volatile.Read(ref _callbackStage)),
        ReadbackStageName(Volatile.Read(ref _readbackStage)));

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a frame newer than <paramref name="sinceVersion"/>. Single-waiter only.</summary>
    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        if (FrameVersion > sinceVersion) return true;
        _frameSignal.WaitOne(timeoutMs);
        return FrameVersion > sinceVersion;
    }

    public bool CursorEnabled
    {
        set => _session.IsCursorCaptureEnabled = value;
    }

    public static ScreenCapture ForMonitor(IntPtr hMonitor) =>
        CreateForTarget("monitor", () => D3DInterop.CreateItemForMonitor(hMonitor));

    public static ScreenCapture ForWindow(IntPtr hWnd) =>
        CreateForTarget("window", () => D3DInterop.CreateItemForWindow(hWnd));

    internal static ScreenCapture ForWindow(IntPtr hWnd, GpuCaptureDevice device) =>
        CreateForTarget("window", () => D3DInterop.CreateItemForWindow(hWnd), sharedDevice: device);

    internal static ScreenCapture ForWindow(IntPtr hWnd, int outputWidth, int outputHeight) =>
        CreateForTarget(
            "window",
            () => D3DInterop.CreateItemForWindow(hWnd),
            outputWidth: outputWidth,
            outputHeight: outputHeight);

    internal static ScreenCapture ForWindow(
        IntPtr hWnd,
        int outputWidth,
        int outputHeight,
        GpuCaptureDevice device) =>
        CreateForTarget(
            "window",
            () => D3DInterop.CreateItemForWindow(hWnd),
            outputWidth: outputWidth,
            outputHeight: outputHeight,
            sharedDevice: device);

    internal static ScreenCapture ForPreviewMonitor(IntPtr hMonitor, CaptureCreationTrace trace) =>
        CreateForTarget("monitor", () => D3DInterop.CreateItemForMonitor(hMonitor), writeDiagnostics: false, trace);

    internal static ScreenCapture ForPreviewWindow(IntPtr hWnd, CaptureCreationTrace trace) =>
        CreateForTarget("window", () => D3DInterop.CreateItemForWindow(hWnd), writeDiagnostics: false, trace);

    private static ScreenCapture CreateForTarget(
        string targetKind,
        Func<GraphicsCaptureItem> createItem,
        bool writeDiagnostics = true,
        CaptureCreationTrace? trace = null,
        int? outputWidth = null,
        int? outputHeight = null,
        GpuCaptureDevice? sharedDevice = null)
    {
        string itemStep = targetKind == "window"
            ? "GraphicsCaptureItem.CreateForWindow"
            : "GraphicsCaptureItem.CreateForMonitor";
        trace?.Begin(itemStep);
        GraphicsCaptureItem item = createItem();
        trace?.Complete(itemStep);
        Windows.Graphics.SizeInt32 itemSize = item.Size;
        int itemWidth = itemSize.Width & ~1;
        int itemHeight = itemSize.Height & ~1;
        if (itemWidth <= 0 || itemHeight <= 0)
        {
            throw new CaptureTargetUnavailableException(
                $"{targetKind} has no capturable surface ({itemSize.Width}x{itemSize.Height}); pick another source.");
        }
        int width = (outputWidth ?? itemSize.Width) & ~1;
        int height = (outputHeight ?? itemSize.Height) & ~1;
        if (width <= 0 || height <= 0)
        {
            throw new CaptureTargetUnavailableException(
                $"{targetKind} has no capturable surface ({itemSize.Width}x{itemSize.Height}); pick another source.");
        }

        var capture = new ScreenCapture(
            item,
            itemSize,
            width,
            height,
            writeDiagnostics,
            targetKind == "window",
            trace,
            sharedDevice);
        trace?.MarkChainProven();
        return capture;
    }

    private ScreenCapture(
        GraphicsCaptureItem item,
        Windows.Graphics.SizeInt32 itemSize,
        int width,
        int height,
        bool writeDiagnostics,
        bool isWindowCapture,
        CaptureCreationTrace? trace,
        GpuCaptureDevice? sharedDevice)
    {
        _writeDiagnostics = writeDiagnostics;
        if (writeDiagnostics && isWindowCapture)
        {
            _windowDeliveryRate = new WindowFrameDeliveryRateTracker(
                Stopwatch.Frequency,
                TimeSpan.FromSeconds(10));
        }
        trace?.Begin("D3D11CreateDevice");
        if (sharedDevice is null)
        {
            D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                null!, out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
            _device = device!;
            _context = context!;
        }
        else
        {
            (_device, _context) = sharedDevice.Acquire();
        }
        trace?.Complete("D3D11CreateDevice");

        // The free-threaded WGC frame pool uses this device from its own threads;
        // multithread protection makes the runtime serialize every context call
        // (our _gate only covers OUR calls, not WGC's internal ones).
        trace?.Begin("ID3D11Multithread.SetMultithreadProtected");
        using (var mt = _context.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);
        trace?.Complete("ID3D11Multithread.SetMultithreadProtected");

        // Capture adapter identity for the init log - on hybrid/multi-GPU boxes the
        // capture adapter need not be the render or encoder adapter.
        trace?.Begin("ID3D11Device.QueryInterface<IDXGIDevice>");
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            trace?.Complete("ID3D11Device.QueryInterface<IDXGIDevice>");
            trace?.Begin("IDXGIDevice.GetAdapter");
            using (var adapter = dxgiDevice.GetAdapter())
            {
                trace?.Complete("IDXGIDevice.GetAdapter");
                trace?.Begin("IDXGIAdapter.Description");
                GpuVendorId = (uint)adapter.Description.VendorId;
                AdapterName = adapter.Description.Description;
                trace?.Complete("IDXGIAdapter.Description");
                // Best-effort LUID + UMD driver version - a diagnostics read must never
                // throw out of the constructor or fail capture.
                trace?.Begin("IDXGIAdapter1 identity queries");
                try
                {
                    using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
                    var luid = adapter1.Description1.Luid;
                    AdapterLuid = $"{luid.HighPart}:{luid.LowPart}";
                    if (adapter1.CheckInterfaceSupport(typeof(IDXGIDevice), out long umd))
                        DriverVersion = $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
                }
                catch { /* diagnostics only - never fail capture over adapter identity */ }
                trace?.Complete("IDXGIAdapter1 identity queries");
            }
        }
        if (_writeDiagnostics)
            Console.WriteLine($"[capture] adapter: {AdapterName}, Windows {Environment.OSVersion.Version}, LUID {AdapterLuid}, driver {DriverVersion}");

        trace?.Begin("CreateDirect3D11DeviceFromDXGIDevice");
        _winrtDevice = D3DInterop.CreateWinRtDevice(_device);
        trace?.Complete("CreateDirect3D11DeviceFromDXGIDevice");
        _item = item;
        Width = width;   // even-aligned for 4:2:0 chroma
        Height = height;
        _contentWidth = _poolWidth = itemSize.Width & ~1;
        _contentHeight = _poolHeight = itemSize.Height & ~1;
        _reportedContentWidth = itemSize.Width;
        _reportedContentHeight = itemSize.Height;

        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        trace?.Begin("ID3D11Device.CreateTexture2D (output)");
        _latest = _device.CreateTexture2D(desc);
        trace?.Complete("ID3D11Device.CreateTexture2D (output)");
        trace?.Begin("ID3D11Device.CreateRenderTargetView");
        _latestRtv = _device.CreateRenderTargetView(_latest, null);
        trace?.Complete("ID3D11Device.CreateRenderTargetView");
        _gpuTextureFrame = new GpuTextureCaptureFrame(
            _device,
            _context,
            Width,
            Height,
            AdapterLuid,
            TryCopyLatestTexture);

        // Staging textures are CPU-readback only and can't carry a bind flag.
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        trace?.Begin("ID3D11Device.CreateTexture2D (staging A)");
        _stagingA = _device.CreateTexture2D(desc);
        trace?.Complete("ID3D11Device.CreateTexture2D (staging A)");
        trace?.Begin("ID3D11Device.CreateTexture2D (staging B)");
        _stagingB = _device.CreateTexture2D(desc);
        trace?.Complete("ID3D11Device.CreateTexture2D (staging B)");

        _item.Closed += OnTargetClosed;

        trace?.Begin("Direct3D11CaptureFramePool.CreateFreeThreaded");
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, CaptureFormat, CaptureBufferCount, itemSize);
        trace?.Complete("Direct3D11CaptureFramePool.CreateFreeThreaded");
        _framePool.FrameArrived += OnFrameArrived;
        trace?.Begin("Direct3D11CaptureFramePool.CreateCaptureSession");
        _session = _framePool.CreateCaptureSession(_item);
        trace?.Complete("Direct3D11CaptureFramePool.CreateCaptureSession");
        if (_windowDeliveryRate is not null)
        {
            TryWriteWindowDiagnostic(DiagnosticLogEventText.WindowCaptureMinUpdateInterval(
                WindowCaptureMinUpdateInterval.Apply(_session)));
        }
        _session.IsCursorCaptureEnabled = true;
        trace?.Begin("GraphicsCaptureAccess.RequestAccessAsync");
        CaptureBorderSuppression.TryDisable(_session);
        trace?.Complete("GraphicsCaptureAccess.RequestAccessAsync");
        trace?.Begin("GraphicsCaptureSession.StartCapture");
        _session.StartCapture();
        trace?.Complete("GraphicsCaptureSession.StartCapture");
    }

    private void OnTargetClosed(GraphicsCaptureItem sender, object args)
    {
        _targetWasClosed = true;
        _captureError ??= new InvalidOperationException(
            "the captured window was closed; start a new share once it's running again");
        try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
        try
        {
            TargetClosed?.Invoke(this);
        }
        catch (Exception ex)
        {
            if (_writeDiagnostics)
                Console.Error.WriteLine(
                    $"[capture] target-close notification failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Never let an exception escape a WinRT callback (it would kill the process
        // or vanish silently). Record the first failure so the session can stop
        // with a real diagnostic instead of streaming nothing while looking live.
        Direct3D11CaptureFrame? frame = null;
        Interlocked.Increment(ref _callbacksStarted);
        Interlocked.Exchange(ref _lastCallbackTicks, Stopwatch.GetTimestamp());
        Volatile.Write(ref _callbackStage, (int)CallbackStage.WaitingForGpuGate);
        try
        {
            lock (_gate)
            {
                if (_disposing) return;

                Volatile.Write(ref _callbackStage, (int)CallbackStage.GettingFrame);
                frame = sender.TryGetNextFrame();
                if (frame is null) return;

                try
                {
                    Volatile.Write(ref _callbackStage, (int)CallbackStage.GpuCopyOrScale);
                    // Treat odd-to-even 1 px rounding as the same size so it cannot churn the pool.
                    var contentSize = frame.ContentSize;
                    int cw = contentSize.Width & ~1;
                    int ch = contentSize.Height & ~1;

                    if (cw != _contentWidth || ch != _contentHeight)
                    {
                        if (_writeDiagnostics)
                            Console.WriteLine(
                                $"[capture] source size changed: {_reportedContentWidth}x{_reportedContentHeight} -> {contentSize.Width}x{contentSize.Height}; output scaled to fit {Width}x{Height}; Switch source re-picks native size");
                        _contentWidth = cw;
                        _contentHeight = ch;
                        _reportedContentWidth = contentSize.Width;
                        _reportedContentHeight = contentSize.Height;
                    }

                    if (cw <= 0 || ch <= 0)
                    {
                        _context.ClearRenderTargetView(_latestRtv, new Color4(0f, 0f, 0f, 1f));
                    }
                    else if (cw != _poolWidth || ch != _poolHeight)
                    {
                        // WGC crops surfaces to the old pool size. Retire this frame before
                        // recreating the pool so the next callback receives the whole source.
                        frame.Dispose();
                        frame = null;
                        sender.Recreate(_winrtDevice, CaptureFormat, CaptureBufferCount, contentSize);
                        _poolWidth = cw;
                        _poolHeight = ch;
                        _context.ClearRenderTargetView(_latestRtv, new Color4(0f, 0f, 0f, 1f));
                    }
                    else
                    {
                        using var texture = D3DInterop.GetTexture(frame.Surface);
                        if (cw == Width && ch == Height)
                        {
                            // Region copy: window sizes can be odd, our encode textures are even-aligned.
                            // cw/ch are even-aligned so the region can never exceed the surface.
                            _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0,
                                new Box(0, 0, 0, cw, ch, 1));
                        }
                        else
                        {
                            ScaleToFit(texture, cw, ch);
                        }
                    }
                }
                finally
                {
                    frame?.Dispose();
                    frame = null;
                }
            }
            _hasFrame = true;
            Interlocked.Increment(ref _framesArrived);
            long frameReadyTicks = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastFrameReadyTicks, frameReadyTicks);
            if (_windowDeliveryRate?.RecordFrame(frameReadyTicks) is { } deliveryRate)
            {
                TryWriteWindowDiagnostic(
                    DiagnosticLogEventText.WindowFrameDeliveryRate(deliveryRate));
            }
            try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
        }
        catch (Exception ex)
        {
            if (!_disposing && _captureError is null)
            {
                _captureError = ex;
                if (_writeDiagnostics)
                    Console.Error.WriteLine($"[capture] frame callback failed (HRESULT 0x{ex.HResult:X8}): {ex}");
            }
        }
        finally
        {
            frame?.Dispose();
            Volatile.Write(ref _callbackStage, (int)CallbackStage.Idle);
        }
    }

    private unsafe void ScaleToFit(ID3D11Texture2D texture, int sourceWidth, int sourceHeight)
    {
        EnsureScaleResources(sourceWidth, sourceHeight);

        _context.CopySubresourceRegion(_scaleSource!, 0, 0, 0, 0, texture, 0,
            new Box(0, 0, 0, sourceWidth, sourceHeight, 1));
        _context.ClearRenderTargetView(_latestRtv, new Color4(0f, 0f, 0f, 1f));

        float scale = Math.Min((float)Width / sourceWidth, (float)Height / sourceHeight);
        float fittedWidth = sourceWidth * scale;
        float fittedHeight = sourceHeight * scale;
        var viewport = new Viewport(
            (Width - fittedWidth) * 0.5f,
            (Height - fittedHeight) * 0.5f,
            fittedWidth,
            fittedHeight);

        nint renderTarget = _latestRtv.NativePointer;
        nint sourceView = _scaleSourceView!.NativePointer;
        nint sampler = _scaleSampler!.NativePointer;
        nint context = _context.NativePointer;
        nint* contextMethods = *(nint**)context;
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        // Use the context ABI directly so Vortice cannot allocate interface arrays per frame.
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)contextMethods[11])(
            context, _scaleVertexShader!.NativePointer, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)contextMethods[9])(
            context, _scalePixelShader!.NativePointer, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)contextMethods[8])(
            context, 0, 1, &sourceView);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)contextMethods[10])(
            context, 0, 1, &sampler);
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)contextMethods[44])(
            context, 1, &viewport);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)contextMethods[33])(
            context, 1, &renderTarget, 0);
        try
        {
            _context.Draw(3, 0);
        }
        finally
        {
            nint none = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)contextMethods[8])(
                context, 0, 1, &none);
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)contextMethods[33])(
                context, 0, null, 0);
        }
    }

    private void EnsureScaleResources(int sourceWidth, int sourceHeight)
    {
        if (_scaleVertexShader is null)
            CreateScalePipeline();

        if (_scaleSourceWidth == sourceWidth && _scaleSourceHeight == sourceHeight)
            return;

        var desc = new Texture2DDescription
        {
            Width = (uint)sourceWidth,
            Height = (uint)sourceHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        ID3D11Texture2D? newSource = null;
        ID3D11ShaderResourceView? newView = null;
        try
        {
            newSource = _device.CreateTexture2D(desc);
            newView = _device.CreateShaderResourceView(newSource, null);
        }
        catch
        {
            newView?.Dispose();
            newSource?.Dispose();
            throw;
        }

        _scaleSourceView?.Dispose();
        _scaleSource?.Dispose();
        _scaleSource = newSource;
        _scaleSourceView = newView;
        _scaleSourceWidth = sourceWidth;
        _scaleSourceHeight = sourceHeight;
    }

    private unsafe void CreateScalePipeline()
    {
        ID3D11VertexShader? vertexShader = null;
        ID3D11PixelShader? pixelShader = null;
        ID3D11SamplerState? sampler = null;
        try
        {
            byte[] vertexBytecode = CompileShader(ScaleVertexShaderSource, "vs_4_0");
            byte[] pixelBytecode = CompileShader(ScalePixelShaderSource, "ps_4_0");
            fixed (byte* vertexPointer = vertexBytecode)
                vertexShader = _device.CreateVertexShader(vertexPointer, (nuint)vertexBytecode.Length, null);
            fixed (byte* pixelPointer = pixelBytecode)
                pixelShader = _device.CreatePixelShader(pixelPointer, (nuint)pixelBytecode.Length, null);
            sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxAnisotropy = 1,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0f,
                MaxLOD = float.MaxValue,
            });
        }
        catch
        {
            sampler?.Dispose();
            pixelShader?.Dispose();
            vertexShader?.Dispose();
            throw;
        }

        _scaleVertexShader = vertexShader;
        _scalePixelShader = pixelShader;
        _scaleSampler = sampler;
    }

    private static unsafe byte[] CompileShader(string source, string target)
    {
        nint code = 0;
        nint errors = 0;
        try
        {
            int result = D3DCompile(source, (nuint)source.Length, null, 0, 0, "main", target, 0, 0, out code, out errors);
            if (result < 0)
            {
                string detail = errors == 0
                    ? $"HRESULT 0x{result:X8}"
                    : Marshal.PtrToStringAnsi(GetBlobBuffer(errors), checked((int)GetBlobSize(errors)))?.TrimEnd('\0', '\r', '\n')
                        ?? $"HRESULT 0x{result:X8}";
                throw new InvalidOperationException($"D3D11 scale shader compilation failed: {detail}");
            }

            int length = checked((int)GetBlobSize(code));
            byte[] bytecode = new byte[length];
            Marshal.Copy(GetBlobBuffer(code), bytecode, 0, length);
            return bytecode;
        }
        finally
        {
            if (errors != 0) Marshal.Release(errors);
            if (code != 0) Marshal.Release(code);
        }
    }

    private static unsafe nint GetBlobBuffer(nint blob) =>
        ((delegate* unmanaged[Stdcall]<nint, nint>)(*(nint**)blob)[3])(blob);

    private static unsafe nuint GetBlobSize(nint blob) =>
        ((delegate* unmanaged[Stdcall]<nint, nuint>)(*(nint**)blob)[4])(blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string source,
        nuint sourceLength,
        [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        nint defines,
        nint include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errors);

    /// <summary>Copies the most recent frame into <paramref name="buffer"/> as tightly packed BGRA.
    /// Pipelined ping-pong: we queue this tick's GPU copy into one staging texture and map the
    /// OTHER one (whose copy finished a tick ago), so Map never stalls the context - a stalled
    /// Map here was blocking FrameArrived and capping WGC delivery at ~50 fps during play.
    /// Costs one frame of extra age, invisible at our buffer sizes.</summary>
    public unsafe bool TryReadFrame(byte[] buffer)
    {
        if (!_hasFrame) return false;
        int rowBytes = Width * 4;
        if (buffer.Length < rowBytes * Height)
            throw new ArgumentException("Frame buffer too small", nameof(buffer));

        Interlocked.Increment(ref _readbacksStarted);
        Interlocked.Exchange(ref _lastReadbackStartedTicks, Stopwatch.GetTimestamp());
        Volatile.Write(ref _readbackStage, (int)ReadbackStage.WaitingForGpuGate);
        try
        {
            ID3D11Texture2D readFrom;
            MappedSubresource mapped;
            lock (_gate)
            {
                Volatile.Write(ref _readbackStage, (int)ReadbackStage.QueueingGpuCopy);
                var writeTo = _flip ? _stagingA : _stagingB;
                readFrom = _flip ? _stagingB : _stagingA;
                _flip = !_flip;
                _context.CopyResource(writeTo, _latest);
                if (!_primed) { _primed = true; readFrom = writeTo; } // first call: no previous copy yet
                Volatile.Write(ref _readbackStage, (int)ReadbackStage.MappingGpuReadback);
                mapped = _context.Map(readFrom, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            }

            // The 14 MB row copy happens WITHOUT the lock - reading a mapped pointer is
            // not a context call, and FrameArrived never touches the mapped resource.
            try
            {
                Volatile.Write(ref _readbackStage, (int)ReadbackStage.CopyingToCpuBuffer);
                fixed (byte* dst = buffer)
                {
                    byte* src = (byte*)mapped.DataPointer;
                    if (mapped.RowPitch == rowBytes)
                    {
                        Buffer.MemoryCopy(src, dst, buffer.Length, (long)rowBytes * Height);
                    }
                    else
                    {
                        for (int y = 0; y < Height; y++)
                            Buffer.MemoryCopy(src + (long)y * mapped.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _readbackStage, (int)ReadbackStage.UnmappingGpuReadback);
                lock (_gate)
                {
                    _context.Unmap(readFrom, 0);
                }
            }
            Interlocked.Increment(ref _readbacksCompleted);
            Interlocked.Exchange(ref _lastReadbackCompletedTicks, Stopwatch.GetTimestamp());
            return true;
        }
        catch (Exception ex)
        {
            // A lost GPU device (driver reset/TDR) surfaces here as a failed Map.
            // Record it instead of throwing: monitor shares get a backend swap via
            // AutoMonitorCapture, window shares stop with a clean message.
            if (_captureError is null)
            {
                _captureError = ex;
                if (_writeDiagnostics)
                    Console.Error.WriteLine($"[capture] frame readback failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
            }
            return false;
        }
        finally
        {
            Volatile.Write(ref _readbackStage, (int)ReadbackStage.Idle);
        }
    }

    GpuTextureCaptureStatus IGpuTextureCaptureSource.TryGetGpuTexture(
        out GpuTextureCaptureFrame? frame)
    {
        if (!_hasFrame || _disposing)
        {
            frame = null;
            return GpuTextureCaptureStatus.Unavailable;
        }

        frame = _gpuTextureFrame;
        return GpuTextureCaptureStatus.Available;
    }

    private bool TryCopyLatestTexture(ID3D11Texture2D destination)
    {
        if (!_hasFrame || _disposing)
            return false;

        Texture2DDescription description = destination.Description;
        if (description.Width != (uint)Width || description.Height != (uint)Height ||
            description.Format != Format.B8G8R8A8_UNorm)
        {
            throw new ArgumentException(
                "GPU capture destination must match the BGRA capture texture.",
                nameof(destination));
        }

        lock (_gate)
        {
            if (_disposing || !_hasFrame)
                return false;
            _context.CopyResource(destination, _latest);
            return true;
        }
    }

    private enum CallbackStage
    {
        Idle,
        WaitingForGpuGate,
        GettingFrame,
        GpuCopyOrScale,
    }

    private enum ReadbackStage
    {
        Idle,
        WaitingForGpuGate,
        QueueingGpuCopy,
        MappingGpuReadback,
        CopyingToCpuBuffer,
        UnmappingGpuReadback,
    }

    private static string CallbackStageName(int stage) => (CallbackStage)stage switch
    {
        CallbackStage.WaitingForGpuGate => "waiting-for-gpu-gate",
        CallbackStage.GettingFrame => "getting-wgc-frame",
        CallbackStage.GpuCopyOrScale => "gpu-copy-or-scale",
        _ => "idle",
    };

    private static string ReadbackStageName(int stage) => (ReadbackStage)stage switch
    {
        ReadbackStage.WaitingForGpuGate => "waiting-for-gpu-gate",
        ReadbackStage.QueueingGpuCopy => "queueing-gpu-copy",
        ReadbackStage.MappingGpuReadback => "mapping-gpu-readback",
        ReadbackStage.CopyingToCpuBuffer => "copying-to-cpu-buffer",
        ReadbackStage.UnmappingGpuReadback => "unmapping-gpu-readback",
        _ => "idle",
    };

    private static void TryWriteWindowDiagnostic(string line)
    {
        try { ConsoleMirror.WriteDiagnosticLine(line); }
        catch { }
    }

    public void Dispose()
    {
        // Stop new callbacks before waiting for an in-flight callback's GPU gate.
        _disposing = true;
        try
        {
            _item.Closed -= OnTargetClosed;
        }
        catch (Exception ex)
        {
            if (_writeDiagnostics)
                Console.Error.WriteLine(
                    $"[capture] target-close unsubscribe failed during disposal: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();
        lock (_gate)
        {
            _scaleSourceView?.Dispose();
            _scaleSource?.Dispose();
            _scaleSampler?.Dispose();
            _scalePixelShader?.Dispose();
            _scaleVertexShader?.Dispose();
            _latestRtv.Dispose();
            _latest.Dispose();
            _stagingA.Dispose();
            _stagingB.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
        _frameSignal.Dispose();
    }
}

internal sealed class CaptureTargetUnavailableException : Exception
{
    internal CaptureTargetUnavailableException(string message) : base(message) { }
}
