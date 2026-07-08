using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamHost.Capture;

/// <summary>
/// Monitor capture via DXGI desktop duplication. Unlike Windows.Graphics.Capture,
/// this path sees exclusive-fullscreen content (games that freeze under WGC when
/// focused). Trade-off: the mouse cursor is not composited into the frames.
/// Monitors only; rotated displays are not supported by this backend.
/// </summary>
public sealed class DuplicationCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _latest;
    private readonly ID3D11Texture2D _stagingA;
    private readonly ID3D11Texture2D _stagingB;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _frameSignal = new(false);
    private bool _flip;
    private bool _primed;
    private volatile bool _hasFrame;
    private long _framesArrived;
    private volatile Exception? _captureError;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; } = "?";
    public long FrameVersion => Interlocked.Read(ref _framesArrived);
    public long FramesArrived => Interlocked.Read(ref _framesArrived);
    public Exception? CaptureError => _captureError;
    public bool CursorEnabled { set { /* duplication does not composite the cursor */ } }

    public DuplicationCapture(IntPtr hMonitor)
    {
        // Duplication requires the D3D device to live on the adapter that owns the output.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? foundAdapter = null;
        IDXGIOutput? foundOutput = null;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                if (output!.Description.Monitor == hMonitor)
                {
                    foundAdapter = adapter;
                    foundOutput = output;
                    break;
                }
                output.Dispose();
            }
            if (foundOutput is not null) break;
            adapter!.Dispose();
        }
        if (foundAdapter is null || foundOutput is null)
            throw new InvalidOperationException("Monitor not found among DXGI outputs");

        try
        {
            AdapterName = foundAdapter.Description1.Description;
            GpuVendorId = (uint)foundAdapter.Description1.VendorId;

            D3D11.D3D11CreateDevice(
                foundAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                null!, out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
            _device = device!;
            _context = context!;

            _output = foundOutput.QueryInterface<IDXGIOutput1>();
            try
            {
                _duplication = _output.DuplicateOutput(_device);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "could not start desktop duplication — another app may be capturing this display (overlay/recorder), or the display is transitioning", ex);
            }

            var mode = _duplication.Description.ModeDescription;
            if (_duplication.Description.Rotation != ModeRotation.Identity)
                throw new NotSupportedException("Compatibility capture does not support rotated displays — use the standard capture for this monitor");
            Width = (int)mode.Width & ~1;
            Height = (int)mode.Height & ~1;
        }
        finally
        {
            foundOutput.Dispose();
            foundAdapter.Dispose();
        }

        Console.WriteLine($"[capture] compatibility capture (desktop duplication) on {AdapterName}, {Width}x{Height} — cursor is not captured in this mode");

        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        _latest = _device.CreateTexture2D(desc);
        desc.Usage = ResourceUsage.Staging;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingA = _device.CreateTexture2D(desc);
        _stagingB = _device.CreateTexture2D(desc);

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "dxgi-duplication" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        int consecutiveFailures = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = _duplication.AcquireNextFrame(500, out _, out IDXGIResource? resource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue; // no screen changes
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Mode change / fullscreen transition / secure desktop: re-duplicate.
                    Reduplicate();
                    continue;
                }
                result.CheckError();

                using (resource)
                using (var texture = resource!.QueryInterface<ID3D11Texture2D>())
                {
                    lock (_gate)
                    {
                        _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0,
                            new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
                    }
                }
                _duplication.ReleaseFrame();
                _hasFrame = true;
                Interlocked.Increment(ref _framesArrived);
                try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                // Fullscreen transitions cause temporary access loss — ride them out
                // (~15s of retries) before declaring the backend dead.
                if (++consecutiveFailures > 60)
                {
                    _captureError = ex;
                    Console.Error.WriteLine($"[capture] desktop duplication failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
                    break;
                }
                Thread.Sleep(250);
                try { Reduplicate(); } catch { /* next iteration retries */ }
            }
        }
    }

    private void Reduplicate()
    {
        try { _duplication.Dispose(); } catch { }
        _duplication = _output.DuplicateOutput(_device);
    }

    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        if (FrameVersion > sinceVersion) return true;
        _frameSignal.WaitOne(timeoutMs);
        return FrameVersion > sinceVersion;
    }

    public unsafe bool TryReadFrame(byte[] buffer)
    {
        if (!_hasFrame) return false;
        int rowBytes = Width * 4;
        if (buffer.Length < rowBytes * Height)
            throw new ArgumentException("Frame buffer too small", nameof(buffer));

        ID3D11Texture2D readFrom;
        MappedSubresource mapped;
        lock (_gate)
        {
            var writeTo = _flip ? _stagingA : _stagingB;
            readFrom = _flip ? _stagingB : _stagingA;
            _flip = !_flip;
            _context.CopyResource(writeTo, _latest);
            if (!_primed) { _primed = true; readFrom = writeTo; }
            mapped = _context.Map(readFrom, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        }

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

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
        try { _duplication.Dispose(); } catch { }
        _output.Dispose();
        _frameSignal.Dispose();
        _latest.Dispose();
        _stagingA.Dispose();
        _stagingB.Dispose();
        _context.Dispose();
        _device.Dispose();
        _cts.Dispose();
    }
}
