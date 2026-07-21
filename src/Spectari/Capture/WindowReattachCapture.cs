using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Spectari.Capture;

/// <summary>
/// Keeps one window-share canvas alive while its application is absent, then
/// replaces the WGC source with a newly appeared window from that application.
/// </summary>
internal sealed class WindowReattachCapture : ICaptureSource, ICaptureDiagnostics,
    IGpuTextureCaptureSource, IGpuWaitingFrameSource
{
    private const int CandidatePollMs = 500;
    private const int CandidateFirstFrameMs = 5000;

    private readonly object _gate = new();
    private readonly Func<List<WindowDescription>> _enumerateWindows;
    private readonly Func<IntPtr, int, int, ScreenCapture> _createCapture;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _stateChanged = new(false);
    private readonly Thread _worker;
    private readonly byte[] _waitingFrame;
    private readonly GpuCaptureDevice? _sharedDevice;
    private readonly string _applicationName;
    private readonly uint _gpuVendorId;
    private readonly string _adapterName;
    private readonly string _adapterLuid;
    private readonly string _driverVersion;
    private readonly HashSet<WindowIdentity> _failedCandidates = [];

    private ScreenCapture? _active;
    private WindowIdentity _activeIdentity;
    private WindowLossGate _lossGate = new();
    private WindowReattachPolicy? _policy;
    private CaptureProgressSnapshot _completedProgress;
    private Exception? _fatalError;
    private long _activeVersionBase;
    private long _waitingVersion;
    private bool _waiting;
    private bool _cursorEnabled = true;
    private bool _candidateEnumerationFailureLogged;
    private bool _disposed;
    private int _workerResourcesDisposed;

    private WindowReattachCapture(
        ScreenCapture initialCapture,
        string applicationName,
        WindowIdentity initialIdentity,
        Func<List<WindowDescription>> enumerateWindows,
        Func<IntPtr, int, int, ScreenCapture> createCapture,
        GpuCaptureDevice? sharedDevice = null)
    {
        _active = initialCapture;
        _applicationName = applicationName;
        _activeIdentity = initialIdentity;
        _enumerateWindows = enumerateWindows;
        _createCapture = createCapture;
        _sharedDevice = sharedDevice;
        Width = initialCapture.Width;
        Height = initialCapture.Height;
        _gpuVendorId = initialCapture.GpuVendorId;
        _adapterName = initialCapture.AdapterName;
        _adapterLuid = initialCapture.AdapterLuid;
        _driverVersion = initialCapture.DriverVersion;
        _waitingFrame = CreateWaitingFrame(Width, Height);

        initialCapture.TargetClosed += OnTargetClosed;
        _worker = new Thread(ReattachLoop)
        {
            IsBackground = true,
            Name = "window-reattach",
        };
        _worker.Start();

        if (initialCapture.TargetWasClosed)
            OnTargetClosed(initialCapture);
    }

    internal static WindowReattachCapture Create(
        IntPtr windowHandle,
        string applicationName,
        uint processId,
        string? preferredAdapterLuid = null)
    {
        GpuCaptureDevice sharedDevice = GpuCaptureDevice.Create(preferredAdapterLuid);
        ScreenCapture initial;
        try { initial = ScreenCapture.ForWindow(windowHandle, sharedDevice); }
        catch
        {
            sharedDevice.Dispose();
            throw;
        }
        try
        {
            return new WindowReattachCapture(
                initial,
                applicationName,
                new WindowIdentity(windowHandle, processId),
                WindowEnumerator.GetWindows,
                (handle, width, height) =>
                    ScreenCapture.ForWindow(handle, width, height, sharedDevice),
                sharedDevice);
        }
        catch
        {
            initial.Dispose();
            sharedDevice.Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public uint GpuVendorId => _gpuVendorId;
    public string AdapterName => _adapterName;
    public string AdapterLuid => _adapterLuid;
    public string DriverVersion => _driverVersion;

    internal bool WaitingForWindow
    {
        get { lock (_gate) return _waiting; }
    }

    public long FrameVersion
    {
        get
        {
            lock (_gate)
                return _waiting || _active is null
                    ? _waitingVersion
                    : _activeVersionBase + _active.FrameVersion;
        }
    }

    public long FramesArrived => FrameVersion;

    public Exception? CaptureError
    {
        get
        {
            lock (_gate)
            {
                if (_fatalError is not null) return _fatalError;
                return _active is { TargetWasClosed: false } active
                    ? active.CaptureError
                    : null;
            }
        }
    }

    public bool CursorEnabled
    {
        set
        {
            ScreenCapture? active;
            lock (_gate)
            {
                _cursorEnabled = value;
                active = _active;
            }
            if (active is not null) active.CursorEnabled = value;
        }
    }

    public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
    {
        ScreenCapture? active;
        long activeVersion;
        lock (_gate)
        {
            if (CurrentVersionLocked() > sinceVersion) return true;
            active = _active;
            activeVersion = active?.FrameVersion ?? 0;
        }

        if (active is null)
            _stateChanged.WaitOne(timeoutMs);
        else
            active.WaitForFreshFrame(activeVersion, timeoutMs);

        return FrameVersion > sinceVersion;
    }

    public bool TryReadFrame(byte[] buffer)
    {
        ScreenCapture? active;
        lock (_gate)
        {
            if (_waiting || _active is null)
            {
                if (buffer.Length < _waitingFrame.Length)
                    throw new ArgumentException("Frame buffer too small", nameof(buffer));
                _waitingFrame.CopyTo(buffer, 0);
                return true;
            }
            active = _active;
        }

        return active.TryReadFrame(buffer);
    }

    GpuTextureCaptureStatus IGpuTextureCaptureSource.TryGetGpuTexture(
        out GpuTextureCaptureFrame? frame)
    {
        ScreenCapture? active;
        lock (_gate)
        {
            if (_waiting)
            {
                frame = null;
                return GpuTextureCaptureStatus.CpuFrameOnly;
            }
            active = _active;
        }

        if (active is null)
        {
            frame = null;
            return GpuTextureCaptureStatus.Unavailable;
        }

        return ((IGpuTextureCaptureSource)active).TryGetGpuTexture(out frame);
    }

    bool IGpuWaitingFrameSource.TryGetWaitingFrame(out ReadOnlyMemory<byte> bgraFrame)
    {
        lock (_gate)
        {
            if (!_waiting)
            {
                bgraFrame = default;
                return false;
            }
            bgraFrame = _waitingFrame;
            return true;
        }
    }

    private void OnTargetClosed(ScreenCapture closedCapture)
    {
        WindowLossGate lossGate;
        lock (_gate)
        {
            if (_disposed || _waiting || !ReferenceEquals(_active, closedCapture)) return;
            lossGate = _lossGate;
        }

        OnTargetLost(closedCapture, lossGate, WindowLossReason.CaptureItemClosed);
    }

    private void OnTargetLost(
        ScreenCapture lostCapture,
        WindowLossGate lossGate,
        WindowLossReason reason)
    {
        lock (_gate)
        {
            if (_disposed
                || _waiting
                || !ReferenceEquals(_active, lostCapture)
                || !ReferenceEquals(_lossGate, lossGate)
                || !lossGate.TryClaim(reason))
            {
                return;
            }
        }

        string reasonText = reason switch
        {
            WindowLossReason.CaptureItemClosed => "capture-item closed event",
            WindowLossReason.InvalidWindowHandle => "window handle validity check",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        Console.WriteLine($"[window-reattach] window loss detected by {reasonText}.");

        List<WindowDescription> windowsAtLoss;
        try
        {
            windowsAtLoss = _enumerateWindows();
        }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException(
                "window reattach could not snapshot the windows present at capture loss", ex);
            lock (_gate)
            {
                if (_disposed
                    || _fatalError is not null
                    || !ReferenceEquals(_active, lostCapture)
                    || !ReferenceEquals(_lossGate, lossGate))
                {
                    return;
                }
                _fatalError = failure;
            }
            Console.Error.WriteLine(
                $"[window-reattach] failed to snapshot windows at capture loss: {SingleLine(ex.Message)}");
            _stateChanged.Set();
            return;
        }

        lock (_gate)
        {
            if (_disposed
                || _waiting
                || !ReferenceEquals(_active, lostCapture)
                || !ReferenceEquals(_lossGate, lossGate))
            {
                return;
            }

            _waitingVersion = CurrentVersionLocked() + 1;
            AccumulateProgress(lostCapture);
            _active = null;
            _waiting = true;
            _policy = new WindowReattachPolicy(
                _applicationName,
                _activeIdentity,
                windowsAtLoss);
            _failedCandidates.Clear();
            _candidateEnumerationFailureLogged = false;
        }

        lostCapture.TargetClosed -= OnTargetClosed;
        RetireInBackground(lostCapture, "lost capture retirement");
        Console.WriteLine(
            "[window-reattach] the stream remains live while waiting for the application to return.");
        _stateChanged.Set();
    }

    private void ReattachLoop()
    {
        CancellationToken ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ScreenCapture? active;
                WindowIdentity activeIdentity;
                WindowLossGate? lossGate;
                WindowReattachPolicy? policy;
                lock (_gate)
                {
                    active = _waiting ? null : _active;
                    activeIdentity = _activeIdentity;
                    lossGate = active is null ? null : _lossGate;
                    policy = _waiting ? _policy : null;
                }

                if (active is not null && lossGate is not null)
                {
                    if (!IsWindow(activeIdentity.Handle))
                        OnTargetLost(active, lossGate, WindowLossReason.InvalidWindowHandle);
                    WaitForWork(CandidatePollMs, ct);
                    continue;
                }

                if (policy is null)
                {
                    WaitForWork(Timeout.Infinite, ct);
                    continue;
                }

                WindowDescription? candidate;
                try
                {
                    List<WindowDescription> currentWindows = _enumerateWindows();
                    lock (_gate)
                    {
                        candidate = policy.SelectCandidate(currentWindows.Where(window =>
                            !_failedCandidates.Contains(WindowReattachPolicy.IdentityOf(window))));
                    }
                }
                catch (Exception ex)
                {
                    bool shouldLog;
                    lock (_gate)
                    {
                        shouldLog = !_candidateEnumerationFailureLogged;
                        _candidateEnumerationFailureLogged = true;
                    }
                    if (shouldLog)
                        Console.Error.WriteLine(
                            $"[window-reattach] candidate enumeration failed; still waiting: {SingleLine(ex.Message)}");
                    WaitForWork(CandidatePollMs, ct);
                    continue;
                }

                if (candidate is null)
                {
                    WaitForWork(CandidatePollMs, ct);
                    continue;
                }

                TryAttachCandidate(policy, candidate, ct);
                WaitForWork(CandidatePollMs, ct);
            }
        }
        finally
        {
            bool disposed;
            lock (_gate) disposed = _disposed;
            if (disposed) DisposeWorkerResources();
        }
    }

    private void TryAttachCandidate(
        WindowReattachPolicy policy,
        WindowDescription candidate,
        CancellationToken ct)
    {
        ScreenCapture? replacement = null;
        try
        {
            replacement = _createCapture(candidate.Handle, Width, Height);
            bool cursorEnabled;
            lock (_gate) cursorEnabled = _cursorEnabled;
            replacement.CursorEnabled = cursorEnabled;

            long deadline = Stopwatch.GetTimestamp()
                + CandidateFirstFrameMs * Stopwatch.Frequency / 1000;
            while (!ct.IsCancellationRequested
                && !replacement.TargetWasClosed
                && replacement.CaptureError is null
                && replacement.FrameVersion == 0
                && Stopwatch.GetTimestamp() < deadline)
            {
                replacement.WaitForFreshFrame(0, 100);
            }

            if (ct.IsCancellationRequested
                || replacement.TargetWasClosed
                || replacement.CaptureError is not null
                || replacement.FrameVersion == 0)
            {
                ReportCandidateFailure(candidate);
                return;
            }

            replacement.TargetClosed += OnTargetClosed;
            bool attached;
            lock (_gate)
            {
                attached = !_disposed && _waiting && ReferenceEquals(_policy, policy);
                if (attached)
                {
                    _active = replacement;
                    _activeIdentity = WindowReattachPolicy.IdentityOf(candidate);
                    _lossGate = new WindowLossGate();
                    _activeVersionBase = _waitingVersion;
                    _waiting = false;
                    _policy = null;
                }
            }

            if (!attached) return;
            ScreenCapture attachedCapture = replacement;
            replacement = null;
            if (attachedCapture.TargetWasClosed)
                OnTargetClosed(attachedCapture);
            else
                Console.WriteLine("[window-reattach] capture attached to the returned application window.");
            _stateChanged.Set();
        }
        catch (Exception ex)
        {
            ReportCandidateFailure(candidate, ex);
        }
        finally
        {
            if (replacement is not null)
            {
                replacement.TargetClosed -= OnTargetClosed;
                RetireInBackground(replacement, "unused candidate retirement");
            }
        }
    }

    private void ReportCandidateFailure(WindowDescription candidate, Exception? error = null)
    {
        WindowIdentity identity = WindowReattachPolicy.IdentityOf(candidate);
        lock (_gate)
        {
            if (!_failedCandidates.Add(identity)) return;
        }

        string detail = error is null ? "no frame arrived" : SingleLine(error.Message);
        Console.Error.WriteLine(
            $"[window-reattach] candidate attach failed; still waiting: {detail}");
    }

    private void WaitForWork(int timeoutMs, CancellationToken ct)
    {
        WaitHandle.WaitAny([ct.WaitHandle, _stateChanged], timeoutMs);
    }

    private long CurrentVersionLocked() => _waiting || _active is null
        ? _waitingVersion
        : _activeVersionBase + _active.FrameVersion;

    private void AccumulateProgress(ScreenCapture capture)
    {
        CaptureProgressSnapshot snapshot = ((ICaptureDiagnostics)capture).GetProgressSnapshot();
        _completedProgress = SumProgress(_completedProgress, snapshot);
    }

    CaptureProgressSnapshot ICaptureDiagnostics.GetProgressSnapshot()
    {
        lock (_gate)
        {
            if (_active is null) return _completedProgress;
            return SumProgress(
                _completedProgress,
                ((ICaptureDiagnostics)_active).GetProgressSnapshot());
        }
    }

    private static CaptureProgressSnapshot SumProgress(
        CaptureProgressSnapshot completed,
        CaptureProgressSnapshot current) => new(
        completed.CallbacksStarted + current.CallbacksStarted,
        completed.FramesReady + current.FramesReady,
        completed.ReadbacksStarted + current.ReadbacksStarted,
        completed.ReadbacksCompleted + current.ReadbacksCompleted,
        Math.Max(completed.LastCallbackTicks, current.LastCallbackTicks),
        Math.Max(completed.LastFrameReadyTicks, current.LastFrameReadyTicks),
        Math.Max(completed.LastReadbackStartedTicks, current.LastReadbackStartedTicks),
        Math.Max(completed.LastReadbackCompletedTicks, current.LastReadbackCompletedTicks),
        current.CallbackStage,
        current.ReadbackStage);

    private static byte[] CreateWaitingFrame(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(16, 20, 28));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float titleSize = Math.Clamp(height / 18f, 18f, 56f);
            float bodySize = Math.Clamp(height / 32f, 12f, 30f);
            using var titleFont = new Font("Segoe UI Semibold", titleSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var bodyFont = new Font("Segoe UI", bodySize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var accentPen = new Pen(Color.FromArgb(91, 181, 255), Math.Max(2f, height / 240f));
            using var titleBrush = new SolidBrush(Color.FromArgb(235, 241, 248));
            using var bodyBrush = new SolidBrush(Color.FromArgb(174, 187, 202));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            float iconWidth = Math.Clamp(width * 0.12f, 60f, 150f);
            float iconHeight = iconWidth * 0.62f;
            float iconX = (width - iconWidth) / 2f;
            float iconY = height * 0.18f;
            graphics.DrawRectangle(accentPen, iconX, iconY, iconWidth, iconHeight);
            graphics.DrawLine(
                accentPen,
                iconX + iconWidth * 0.36f,
                iconY + iconHeight + height * 0.025f,
                iconX + iconWidth * 0.64f,
                iconY + iconHeight + height * 0.025f);

            graphics.DrawString(
                "STREAM IS LIVE",
                titleFont,
                titleBrush,
                new RectangleF(0, height * 0.43f, width, height * 0.12f),
                format);
            graphics.DrawString(
                "Shared window closed\nWaiting for the application to return",
                bodyFont,
                bodyBrush,
                new RectangleF(width * 0.08f, height * 0.56f, width * 0.84f, height * 0.18f),
                format);
        }

        byte[] frame = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        var bounds = new Rectangle(0, 0, width, height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                IntPtr source = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(source, frame, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return frame;
    }

    private static void RetireInBackground(ScreenCapture capture, string action)
    {
        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[window-reattach] {action} failed: {SingleLine(ex.Message)}");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    public void Dispose()
    {
        ScreenCapture? active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _waiting = false;
            _policy = null;
            active = _active;
            _active = null;
        }

        _cts.Cancel();
        _stateChanged.Set();
        if (active is not null)
        {
            active.TargetClosed -= OnTargetClosed;
            active.Dispose();
        }

        if (_worker != Thread.CurrentThread && _worker.Join(2000))
        {
            DisposeWorkerResources();
        }
        else if (_worker.IsAlive)
        {
            Console.Error.WriteLine(
                "[window-reattach] background worker did not stop within 2 seconds; cleanup will finish when its current capture call returns.");
        }
    }

    private void DisposeWorkerResources()
    {
        if (Interlocked.Exchange(ref _workerResourcesDisposed, 1) != 0) return;
        _stateChanged.Dispose();
        _cts.Dispose();
        _sharedDevice?.Dispose();
    }
}
