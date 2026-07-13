using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace StreamHost.Capture;

/// <summary>
/// Windows.Graphics.Capture of one monitor. Keeps the most recent frame in a GPU
/// texture; TryReadFrame does a staging readback of that frame on demand, so the
/// caller controls the output frame rate independent of the capture rate.
/// </summary>
public sealed class ScreenCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _latest;
    private readonly ID3D11RenderTargetView _latestRtv; // lets us black-fill _latest when the source shrinks
    private readonly ID3D11Texture2D _stagingA;
    private readonly ID3D11Texture2D _stagingB;
    private bool _flip;
    private bool _primed;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _frameSignal = new(false);
    private volatile bool _hasFrame;
    private long _framesArrived;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; } = "?";
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    /// <summary>First exception thrown inside the frame callback, if any — a set value
    /// means the capture pipeline is dead and the session should stop with a real error.</summary>
    private volatile Exception? _captureError;
    public Exception? CaptureError => _captureError;

    /// <summary>Monotonic count of frames delivered by the compositor — compare across calls to detect fresh content.</summary>
    public long FrameVersion => Interlocked.Read(ref _framesArrived);

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
        new(D3DInterop.CreateItemForMonitor(hMonitor));

    public static ScreenCapture ForWindow(IntPtr hWnd) =>
        new(D3DInterop.CreateItemForWindow(hWnd));

    private ScreenCapture(GraphicsCaptureItem item)
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null!, out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;

        // The free-threaded WGC frame pool uses this device from its own threads;
        // multithread protection makes the runtime serialize every context call
        // (our _gate only covers OUR calls, not WGC's internal ones).
        using (var mt = _context.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        // Capture adapter identity for the init log — on hybrid/multi-GPU boxes the
        // capture adapter need not be the render or encoder adapter.
        string adapterLuid = "?";
        string driverVersion = "?";
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        {
            GpuVendorId = (uint)adapter.Description.VendorId;
            AdapterName = adapter.Description.Description;
            // Best-effort LUID + UMD driver version — a diagnostics read must never
            // throw out of the constructor or fail capture.
            try
            {
                using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
                var luid = adapter1.Description1.Luid;
                adapterLuid = $"{luid.HighPart}:{luid.LowPart}";
                if (adapter1.CheckInterfaceSupport(typeof(IDXGIDevice), out long umd))
                    driverVersion = $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
            }
            catch { /* diagnostics only — never fail capture over adapter identity */ }
        }
        Console.WriteLine($"[capture] adapter: {AdapterName}, Windows {Environment.OSVersion.Version}, LUID {adapterLuid}, driver {driverVersion}");

        _winrtDevice = D3DInterop.CreateWinRtDevice(_device);
        _item = item;
        Width = _item.Size.Width & ~1;   // even-aligned for 4:2:0 chroma
        Height = _item.Size.Height & ~1;

        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RenderTarget so a shrunk source can be black-filled (ClearRenderTargetView)
            // before the smaller region is copied on top — otherwise the uncovered
            // margins keep the previous, larger frame's pixels.
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        _latest = _device.CreateTexture2D(desc);
        _latestRtv = _device.CreateRenderTargetView(_latest, null);

        // Staging textures are CPU-readback only and can't carry a bind flag.
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingA = _device.CreateTexture2D(desc);
        _stagingB = _device.CreateTexture2D(desc);

        // A closed window/monitor ends the capture permanently — surface it as a
        // clean session stop instead of a silent freeze-frame.
        _item.Closed += (_, _) =>
        {
            _captureError ??= new InvalidOperationException("the captured window was closed; start a new share once it's running again");
            try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
        };

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        try { _session.IsBorderRequired = false; } catch { /* needs consent API; yellow border is fine */ }
        _session.StartCapture();
    }

    private bool _warnedResize;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Never let an exception escape a WinRT callback (it would kill the process
        // or vanish silently). Record the first failure so the session can stop
        // with a real diagnostic instead of streaming nothing while looking live.
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // The source can change size mid-session (game res switch, borderless/
            // fullscreen toggle, launcher->game handoff, plain resize). Our pool and
            // encode textures are fixed at the original size, so CLAMP the copy region
            // to what is valid on both sides: a grown source is cropped to Width/Height,
            // a shrunk source copies only its (smaller) valid area. Compare on the
            // even-aligned content size so the harmless odd->even 1px rounding (an odd
            // window is Width+1 every frame) is not read as a resize. Never stop the
            // stream over this — the share stays live at its original dimensions.
            int cw = frame.ContentSize.Width & ~1;
            int ch = frame.ContentSize.Height & ~1;
            int copyW = Math.Min(cw, Width);
            int copyH = Math.Min(ch, Height);
            if (!_warnedResize && (cw != Width || ch != Height))
            {
                _warnedResize = true;
                Console.WriteLine($"[capture] source resized to {frame.ContentSize.Width}x{frame.ContentSize.Height}; output stays at {Width}x{Height}; restart the share for a clean full-size capture");
            }

            using var texture = D3DInterop.GetTexture(frame.Surface);
            lock (_gate)
            {
                // A shrunk source only fills the top-left of the fixed-size _latest;
                // the uncovered right/bottom margins would otherwise keep the previous,
                // larger frame's pixels (stale content leaking downstream). Clear to
                // black first whenever the copy won't cover the whole destination —
                // only then, not on every full-size frame. A zero-sized content frame
                // (minimized/occluded) has nothing to copy: emit a clean black frame
                // instead of issuing an invalid zero-extent copy box.
                bool partial = copyW < Width || copyH < Height || copyW <= 0 || copyH <= 0;
                if (partial)
                    _context.ClearRenderTargetView(_latestRtv, new Color4(0f, 0f, 0f, 1f));
                if (copyW > 0 && copyH > 0)
                    // Region copy: window sizes can be odd, our encode textures are even-aligned.
                    // copyW/copyH are clamped to the source so the region can never exceed it.
                    _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0,
                        new Box(0, 0, 0, copyW, copyH, 1));
            }
            _hasFrame = true;
            Interlocked.Increment(ref _framesArrived);
            try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
        }
        catch (Exception ex)
        {
            if (_captureError is null)
            {
                _captureError = ex;
                Console.Error.WriteLine($"[capture] frame callback failed (HRESULT 0x{ex.HResult:X8}): {ex}");
            }
        }
    }

    /// <summary>Copies the most recent frame into <paramref name="buffer"/> as tightly packed BGRA.
    /// Pipelined ping-pong: we queue this tick's GPU copy into one staging texture and map the
    /// OTHER one (whose copy finished a tick ago), so Map never stalls the context — a stalled
    /// Map here was blocking FrameArrived and capping WGC delivery at ~50 fps during play.
    /// Costs one frame of extra age, invisible at our buffer sizes.</summary>
    public unsafe bool TryReadFrame(byte[] buffer)
    {
        if (!_hasFrame) return false;
        int rowBytes = Width * 4;
        if (buffer.Length < rowBytes * Height)
            throw new ArgumentException("Frame buffer too small", nameof(buffer));

        try
        {
            ID3D11Texture2D readFrom;
            MappedSubresource mapped;
            lock (_gate)
            {
                var writeTo = _flip ? _stagingA : _stagingB;
                readFrom = _flip ? _stagingB : _stagingA;
                _flip = !_flip;
                _context.CopyResource(writeTo, _latest);
                if (!_primed) { _primed = true; readFrom = writeTo; } // first call: no previous copy yet
                mapped = _context.Map(readFrom, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            }

            // The 14 MB row copy happens WITHOUT the lock — reading a mapped pointer is
            // not a context call, and FrameArrived never touches the mapped resource.
            try
            {
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
                lock (_gate)
                {
                    _context.Unmap(readFrom, 0);
                }
            }
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
                Console.Error.WriteLine($"[capture] frame readback failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
            }
            return false;
        }
    }

    public void Dispose()
    {
        // Stop the frame source BEFORE disposing anything a late callback could
        // touch — the signal was previously disposed first, letting an in-flight
        // OnFrameArrived crash on a dead AutoResetEvent.
        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();
        _frameSignal.Dispose();
        _latestRtv.Dispose();
        _latest.Dispose();
        _stagingA.Dispose();
        _stagingB.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
