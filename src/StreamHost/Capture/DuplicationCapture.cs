using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamHost.Capture;

/// <summary>
/// Monitor capture via DXGI desktop duplication. Unlike Windows.Graphics.Capture,
/// this path sees exclusive-fullscreen content (games that freeze under WGC when
/// focused). Duplication frames never include the pointer (the OS draws it as a
/// hardware overlay), so the shape/position surfaced by AcquireNextFrame is
/// composited into the CPU frame on readback.
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

    // Pointer state from AcquireNextFrame, drawn into the frame on readback.
    private readonly Lock _cursorGate = new();
    private byte[]? _pointerShape;
    private OutduplPointerShapeInfo _pointerShapeInfo;
    private int _pointerX, _pointerY;
    private bool _pointerVisible;
    private volatile bool _cursorEnabled = true;
    private bool _loggedCursorShape;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; } = "?";
    public string AdapterLuid { get; } = "?";
    public string DriverVersion { get; } = "?";
    public long FrameVersion => Interlocked.Read(ref _framesArrived);
    public long FramesArrived => Interlocked.Read(ref _framesArrived);
    public Exception? CaptureError => _captureError;
    public bool CursorEnabled { set { _cursorEnabled = value; } }

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

        // Transactional from here on: every step below acquires a D3D/DXGI resource
        // into a field, and several can throw (the DuplicateOutput reject, the
        // rotated-display NotSupportedException, texture creation). AutoMonitorCapture
        // retries this ctor on failure, so a partial build must dispose what it got
        // instead of leaking GPU/COM objects. On any throw, release the acquired
        // resources in reverse order and rethrow; ownership transfers to the finished
        // object only if construction reaches the end.
        try
        {
            // Capture adapter identity for the init log — on hybrid/multi-GPU boxes the
            // capture adapter need not be the render or encoder adapter.
            try
            {
                AdapterName = foundAdapter.Description1.Description;
                GpuVendorId = (uint)foundAdapter.Description1.VendorId;
                var luid = foundAdapter.Description1.Luid;
                AdapterLuid = $"{luid.HighPart}:{luid.LowPart}";
                // Best-effort UMD driver version, decoded from the packed long into the
                // conventional four 16-bit fields — a diagnostics read must never throw
                // out of the constructor or fail capture.
                try
                {
                    if (foundAdapter.CheckInterfaceSupport(typeof(IDXGIDevice), out long umd))
                        DriverVersion = $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
                }
                catch { }

                D3D11.D3D11CreateDevice(
                    foundAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    null!, out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
                _device = device!;
                _context = context!;

                // Duplication calls (AcquireNextFrame/GetFramePointerShape, capture
                // thread) use the immediate context internally WITHOUT locking, while
                // TryReadFrame maps staging textures on the pacing thread. Without
                // runtime-level protection that race corrupts the device (observed:
                // DEVICE_REMOVED at Map, AccessViolation reading a mapped frame).
                using (var mt = _context.QueryInterfaceOrNull<ID3D11Multithread>())
                    mt?.SetMultithreadProtected(true);

                _output = foundOutput.QueryInterface<IDXGIOutput1>();
                try
                {
                    _duplication = _output.DuplicateOutput(_device);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "could not start desktop duplication; another app may be capturing this display (overlay/recorder), or the display is transitioning", ex);
                }

                var mode = _duplication.Description.ModeDescription;
                if (_duplication.Description.Rotation != ModeRotation.Identity)
                    throw new NotSupportedException("Compatibility capture does not support rotated displays; use the standard capture for this monitor");
                Width = (int)mode.Width & ~1;
                Height = (int)mode.Height & ~1;
            }
            finally
            {
                foundOutput.Dispose();
                foundAdapter.Dispose();
            }

            Console.WriteLine($"[capture] desktop duplication on {AdapterName}, {Width}x{Height}, LUID {AdapterLuid}, driver {DriverVersion}");

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
        catch
        {
            // Partial build: dispose every resource acquired so far in reverse order,
            // each individually guarded so one failure can't mask another, then rethrow.
            // foundOutput/foundAdapter are already released by the inner finally above.
            // No thread to stop: its Start() is the last statement, so a throw here
            // always precedes it.
            try { _stagingB?.Dispose(); } catch { }
            try { _stagingA?.Dispose(); } catch { }
            try { _latest?.Dispose(); } catch { }
            try { _duplication?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            try { _frameSignal.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            throw;
        }
    }

    private void CaptureLoop()
    {
        int consecutiveFailures = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Timeout 0, not a blocking wait: with multithread protection on,
                // AcquireNextFrame holds the device lock for its whole duration —
                // a blocking wait here would stall the pacing thread's Map calls
                // and collapse the output rate. Poll instead; ~2 ms granularity
                // (timeBeginPeriod(1)) is far below a frame interval.
                var result = _duplication.AcquireNextFrame(0, out OutduplFrameInfo frameInfo, out IDXGIResource? resource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) { Thread.Sleep(2); continue; } // no screen changes yet
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Mode change / fullscreen transition / secure desktop: re-duplicate.
                    Reduplicate();
                    continue;
                }
                result.CheckError();

                // The acquire succeeded — we now own the frame and MUST release it
                // on every exit path (incl. an exception in the copy, the
                // QueryInterface, or GetFramePointerShape inside UpdatePointer)
                // BEFORE the outer catch runs Reduplicate on the duplication object.
                try
                {
                    using (resource)
                    using (var texture = resource!.QueryInterface<ID3D11Texture2D>())
                    {
                        lock (_gate)
                        {
                            _context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0,
                                new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
                        }
                    }
                    // Pointer metadata must be read between Acquire and Release.
                    // Mouse-only updates also arrive as frames, which is what makes
                    // the composited cursor move smoothly on an otherwise static screen.
                    if (frameInfo.LastMouseUpdateTime != 0) UpdatePointer(frameInfo);
                }
                finally
                {
                    // Guard the release so its own failure can't mask the original
                    // exception — the outer catch logs that one.
                    try { _duplication.ReleaseFrame(); } catch { }
                }
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

    private unsafe void UpdatePointer(in OutduplFrameInfo frameInfo)
    {
        lock (_cursorGate)
        {
            _pointerVisible = frameInfo.PointerPosition.Visible;
            if (frameInfo.PointerPosition.Visible)
            {
                _pointerX = frameInfo.PointerPosition.Position.X;
                _pointerY = frameInfo.PointerPosition.Position.Y;
            }
            if (frameInfo.PointerShapeBufferSize > 0)
            {
                if (_pointerShape is null || _pointerShape.Length < frameInfo.PointerShapeBufferSize)
                    _pointerShape = new byte[frameInfo.PointerShapeBufferSize];
                fixed (byte* p = _pointerShape)
                {
                    _duplication.GetFramePointerShape(frameInfo.PointerShapeBufferSize,
                        (IntPtr)p, out _, out _pointerShapeInfo);
                }
                if (!_loggedCursorShape)
                {
                    _loggedCursorShape = true;
                    Console.WriteLine($"[capture] cursor shape acquired ({_pointerShapeInfo.Width}x{_pointerShapeInfo.Height}, type {_pointerShapeInfo.Type}); compositing into frames");
                }
            }
        }
    }

    /// <summary>Draws the pointer into a tightly packed BGRA frame. The three DXGI
    /// shape formats: COLOR = BGRA with straight alpha; MASKED_COLOR = BGRA where
    /// alpha 0 means replace and 0xFF means XOR with the screen; MONOCHROME = a
    /// 1-bpp AND mask stacked above a 1-bpp XOR mask (Height covers both halves).</summary>
    private void ComposeCursor(byte[] frame)
    {
        lock (_cursorGate)
        {
            if (!_pointerVisible || _pointerShape is null) return;
            byte[] shape = _pointerShape;
            uint type = (uint)_pointerShapeInfo.Type;
            int w = (int)_pointerShapeInfo.Width;
            int h = type == 1 ? (int)_pointerShapeInfo.Height / 2 : (int)_pointerShapeInfo.Height;
            int pitch = (int)_pointerShapeInfo.Pitch;
            int px = _pointerX, py = _pointerY;

            for (int y = 0; y < h; y++)
            {
                int fy = py + y;
                if (fy < 0 || fy >= Height) continue;
                for (int x = 0; x < w; x++)
                {
                    int fx = px + x;
                    if (fx < 0 || fx >= Width) continue;
                    int fi = (fy * Width + fx) * 4;
                    switch (type)
                    {
                        case 2: // COLOR: straight alpha blend
                        {
                            int si = y * pitch + x * 4;
                            int a = shape[si + 3];
                            if (a == 0) break;
                            frame[fi] = (byte)((shape[si] * a + frame[fi] * (255 - a)) / 255);
                            frame[fi + 1] = (byte)((shape[si + 1] * a + frame[fi + 1] * (255 - a)) / 255);
                            frame[fi + 2] = (byte)((shape[si + 2] * a + frame[fi + 2] * (255 - a)) / 255);
                            break;
                        }
                        case 4: // MASKED_COLOR: alpha 0 = replace, 0xFF = XOR
                        {
                            int si = y * pitch + x * 4;
                            if (shape[si + 3] == 0)
                            {
                                frame[fi] = shape[si];
                                frame[fi + 1] = shape[si + 1];
                                frame[fi + 2] = shape[si + 2];
                            }
                            else
                            {
                                frame[fi] ^= shape[si];
                                frame[fi + 1] ^= shape[si + 1];
                                frame[fi + 2] ^= shape[si + 2];
                            }
                            break;
                        }
                        default: // 1, MONOCHROME: screen = (screen AND and-mask) XOR xor-mask
                        {
                            int si = y * pitch + (x >> 3);
                            int bit = 0x80 >> (x & 7);
                            byte and = (byte)((shape[si] & bit) != 0 ? 0xFF : 0x00);
                            byte xor = (byte)((shape[si + h * pitch] & bit) != 0 ? 0xFF : 0x00);
                            frame[fi] = (byte)((frame[fi] & and) ^ xor);
                            frame[fi + 1] = (byte)((frame[fi + 1] & and) ^ xor);
                            frame[fi + 2] = (byte)((frame[fi + 2] & and) ^ xor);
                            break;
                        }
                    }
                }
            }
        }
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
        }
        catch (Exception ex)
        {
            // Map surfaces DEVICE_REMOVED here when the GPU device is lost (driver
            // reset/TDR). Record it instead of throwing so AutoMonitorCapture can
            // swap backends rather than the session dying on a stack trace.
            if (_captureError is null)
            {
                _captureError = ex;
                Console.Error.WriteLine($"[capture] frame readback failed (HRESULT 0x{ex.HResult:X8}, device status 0x{(uint)_device.DeviceRemovedReason.Code:X8}): {ex.Message}");
            }
            return false;
        }
        if (_cursorEnabled) ComposeCursor(buffer);
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
