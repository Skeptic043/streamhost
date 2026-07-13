using System.Diagnostics;

namespace StreamHost.Capture;

/// <summary>
/// Self-managing monitor capture. DEFAULT backend is DXGI desktop duplication
/// (full-rate delivery — 141 fps measured where WGC capped at ~50 — sees
/// exclusive fullscreen, and composites the cursor itself since v0.12).
/// Windows.Graphics.Capture is the fallback when duplication can't start
/// (rotated display, another capture app holding the output) or dies. If WGC
/// is active and starves while a fullscreen app is focused, we hot-swap back
/// to duplication. The stream never notices a swap: dimensions are identical
/// and the frame counter stays monotonic.
/// </summary>
public sealed class AutoMonitorCapture : ICaptureSource
{
    private const int StarvationMs = 2500;   // no fresh frames for this long...
    private const int SwapCooldownMs = 15000; // ...and don't thrash between backends

    private readonly IntPtr _hMonitor;
    private readonly Lock _swapLock = new();
    private readonly Thread _watchdog;
    private readonly CancellationTokenSource _cts = new();

    private ICaptureSource _active;
    private bool _usingDuplication;
    private long _versionOffset;
    private long _lastSeenVersion;
    private long _lastFreshTicks = Stopwatch.GetTimestamp();
    private long _lastSwapAttemptTicks;
    private volatile Exception? _bothFailed;
    private bool _cursorEnabled = true;

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId { get; }
    public string AdapterName { get; }

    public AutoMonitorCapture(IntPtr hMonitor)
    {
        _hMonitor = hMonitor;
        try
        {
            _active = new DuplicationCapture(hMonitor);
            _usingDuplication = true;
        }
        catch (Exception ex)
        {
            // Duplication can't start here (rotated display, another capture app,
            // transition in progress) — fall back to standard capture.
            Console.WriteLine($"[capture] desktop duplication unavailable ({ex.Message}); using standard capture");
            _active = ScreenCapture.ForMonitor(hMonitor);
        }
        Width = _active.Width;
        Height = _active.Height;
        GpuVendorId = _active.GpuVendorId;
        AdapterName = _active.AdapterName;

        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "capture-watchdog" };
        _watchdog.Start();
    }

    public long FrameVersion { get { lock (_swapLock) return _versionOffset + _active.FrameVersion; } }
    public long FramesArrived => FrameVersion;
    public Exception? CaptureError => _bothFailed;
    public bool CursorEnabled { set { _cursorEnabled = value; lock (_swapLock) _active.CursorEnabled = value; } }

    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        ICaptureSource active;
        long offset;
        lock (_swapLock) { active = _active; offset = _versionOffset; }
        bool fresh = active.WaitForFreshFrame(sinceVersion - offset, timeoutMs);
        if (fresh) _lastFreshTicks = Stopwatch.GetTimestamp();
        return fresh;
    }

    public bool TryReadFrame(byte[] buffer)
    {
        lock (_swapLock) return _active.TryReadFrame(buffer);
    }

    private void WatchdogLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(500);
            try { Evaluate(); } catch { /* never kill the watchdog */ }
        }
    }

    private void Evaluate()
    {
        long now = Stopwatch.GetTimestamp();
        double sinceSwapMs = (now - _lastSwapAttemptTicks) * 1000.0 / Stopwatch.Frequency;
        if (sinceSwapMs < SwapCooldownMs && _lastSwapAttemptTicks != 0) return;

        ICaptureSource active;
        lock (_swapLock) active = _active;

        // Track freshness independently of the pacing loop (covers the startup gate too).
        long version = FrameVersion;
        if (version != _lastSeenVersion)
        {
            _lastSeenVersion = version;
            _lastFreshTicks = now;
        }
        double starvedMs = (now - _lastFreshTicks) * 1000.0 / Stopwatch.Frequency;

        if (active.CaptureError is not null)
        {
            // Active backend died outright: swap to the other one.
            Swap(toDuplication: !_usingDuplication, $"active capture failed ({active.CaptureError.Message})");
            return;
        }

        if (!_usingDuplication && starvedMs > StarvationMs && ForegroundProbe.FullscreenAppOnMonitor(_hMonitor))
        {
            // The exclusive-fullscreen signature: WGC starves while a fullscreen
            // app is focused here. A static desktop never triggers this.
            Swap(toDuplication: true, "fullscreen app detected while standard capture starved");
        }
    }

    private void Swap(bool toDuplication, string reason)
    {
        _lastSwapAttemptTicks = Stopwatch.GetTimestamp();
        Console.WriteLine($"[capture] switching to {(toDuplication ? "desktop duplication" : "standard capture")}: {reason}");
        try
        {
            ICaptureSource fresh = toDuplication
                ? new DuplicationCapture(_hMonitor)
                : ScreenCapture.ForMonitor(_hMonitor);
            if (fresh.Width != Width || fresh.Height != Height)
            {
                fresh.Dispose();
                throw new InvalidOperationException($"backend resolution mismatch ({fresh.Width}x{fresh.Height} vs {Width}x{Height})");
            }
            fresh.CursorEnabled = _cursorEnabled;
            lock (_swapLock)
            {
                _versionOffset += _active.FrameVersion;
                var old = _active;
                _active = fresh;
                _usingDuplication = toDuplication;
                try { old.Dispose(); } catch { }
            }
            _lastFreshTicks = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] switch failed: {ex.Message}");
            ICaptureSource active;
            lock (_swapLock) active = _active;
            if (active.CaptureError is not null)
                _bothFailed = new InvalidOperationException(
                    $"both capture backends failed (active: {active.CaptureError.Message}; alternative: {ex.Message})");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watchdog.Join(1500);
        lock (_swapLock) _active.Dispose();
        _cts.Dispose();
    }
}
