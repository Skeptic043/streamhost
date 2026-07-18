using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StreamHost.Capture;

internal enum IdlePreviewPollState
{
    NoChange,
    Frame,
    Minimized,
    Unavailable,
}

internal enum IdlePreviewStartState
{
    Ready,
    Canceled,
    TimedOut,
    Failed,
}

internal readonly record struct IdlePreviewPollResult(IdlePreviewPollState State, Bitmap? Image = null);

/// <summary>
/// Owns preview-only WGC lifecycle work so capture creation and teardown never
/// run on the UI thread.
/// </summary>
internal sealed class IdlePreviewCapture : IDisposable
{
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(5);

    private readonly object _stateGate = new();
    private Task _lifecycleTail = Task.CompletedTask;
    private Task _attachedCleanup = Task.CompletedTask;
    private CancellationTokenSource? _activeCreation;
    private PreviewResources? _resources;
    private PreviewPollOperation? _activePoll;
    private int _generation;
    private bool _streamStartFenced;
    private bool _disposed;

    public bool IsReady
    {
        get
        {
            lock (_stateGate) return _resources is not null;
        }
    }

    public Task<IdlePreviewStartState> StartForMonitorAsync(IntPtr monitorHandle) =>
        StartAsync(
            "monitor",
            IntPtr.Zero,
            trace => ScreenCapture.ForPreviewMonitor(monitorHandle, trace));

    public Task<IdlePreviewStartState> StartForWindowAsync(IntPtr windowHandle) =>
        StartAsync(
            "window",
            windowHandle,
            trace => ScreenCapture.ForPreviewWindow(windowHandle, trace));

    private Task<IdlePreviewStartState> StartAsync(
        string targetKind,
        IntPtr windowHandle,
        Func<CaptureCreationTrace, ScreenCapture> createCapture)
    {
        CancellationTokenSource creationCts;
        Task canceled;
        Task<CreationOutcome> creation;
        CaptureCreationTrace trace;
        int generation;

        lock (_stateGate)
        {
            if (_disposed || _streamStartFenced)
                return Task.FromResult(IdlePreviewStartState.Canceled);

            _activeCreation?.Cancel();
            creationCts = new CancellationTokenSource();
            canceled = Task.Delay(Timeout.InfiniteTimeSpan, creationCts.Token);
            _activeCreation = creationCts;
            generation = ++_generation;

            PreviewResources? previousResources = _resources;
            _resources = null;
            PreviewPollOperation? previousPoll = DetachPoll(previousResources);
            Task previousLifecycle = _lifecycleTail;
            trace = new CaptureCreationTrace(targetKind);
            creation = Task.Run(
                () => CreateAfterPreviousAsync(
                    previousLifecycle,
                    previousResources,
                    previousPoll,
                    generation,
                    creationCts,
                    windowHandle,
                    createCapture,
                    trace));
            _lifecycleTail = creation;
        }

        return AwaitCreationAsync(
            targetKind,
            generation,
            creationCts,
            creation,
            canceled,
            trace);
    }

    private async Task<CreationOutcome> CreateAfterPreviousAsync(
        Task previousLifecycle,
        PreviewResources? previousResources,
        PreviewPollOperation? previousPoll,
        int generation,
        CancellationTokenSource creationCts,
        IntPtr windowHandle,
        Func<CaptureCreationTrace, ScreenCapture> createCapture,
        CaptureCreationTrace trace)
    {
        try
        {
            await previousLifecycle.ConfigureAwait(false);
            if (previousResources is not null &&
                !await DisposeAfterPollAsync(previousResources, previousPoll, "source-change teardown"))
            {
                return new CreationOutcome(IdlePreviewStartState.TimedOut);
            }

            if (!CanCreate(generation, creationCts))
                return CreationOutcome.Canceled;

            ScreenCapture capture = createCapture(trace);
            PreviewResources created;
            try
            {
                created = new PreviewResources(capture, windowHandle);
            }
            catch
            {
                capture.Dispose();
                throw;
            }

            if (!TryAttach(generation, creationCts, created))
            {
                created.Dispose();
                return CreationOutcome.Canceled;
            }

            return CreationOutcome.Ready;
        }
        catch (Exception ex)
        {
            return IsGenerationCurrent(generation)
                ? new CreationOutcome(IdlePreviewStartState.Failed, ex)
                : CreationOutcome.Canceled;
        }
        finally
        {
            CompleteCreation(creationCts);
        }
    }

    private async Task<IdlePreviewStartState> AwaitCreationAsync(
        string targetKind,
        int generation,
        CancellationTokenSource creationCts,
        Task<CreationOutcome> creation,
        Task canceled,
        CaptureCreationTrace trace)
    {
        Task timeout = Task.Delay(CreationTimeout);
        Task completed = await Task.WhenAny(creation, canceled, timeout);

        if (completed == creation)
            return ReportOutcome(targetKind, generation, await creation, trace);

        if (completed == canceled)
            return IdlePreviewStartState.Canceled;

        if (!CancelTimedOutGeneration(generation, creationCts))
        {
            if (creation.IsCompleted)
                return ReportOutcome(targetKind, generation, await creation, trace);
            return IdlePreviewStartState.Canceled;
        }

        Console.WriteLine(
            $"[preview] {targetKind} creation timed out after 5 seconds; preview disabled; " +
            $"pending step: {trace.CurrentStep}; last completed step: {trace.LastCompletedStep}");
        return IdlePreviewStartState.TimedOut;
    }

    private IdlePreviewStartState ReportOutcome(
        string targetKind,
        int generation,
        CreationOutcome outcome,
        CaptureCreationTrace trace)
    {
        if (!IsGenerationCurrent(generation))
            return IdlePreviewStartState.Canceled;

        if (outcome.State == IdlePreviewStartState.Failed && outcome.Error is not null)
        {
            Console.WriteLine(
                $"[preview] {targetKind} creation failed; preview disabled; " +
                $"pending step: {trace.CurrentStep}; last completed step: {trace.LastCompletedStep}; " +
                $"error: {SingleLine(outcome.Error.Message)}");
        }

        return outcome.State;
    }

    public IdlePreviewPollResult Poll()
    {
        PreviewPollOperation timedOut;
        CaptureProgressSnapshot progress;
        lock (_stateGate)
        {
            if (_resources is null)
                return default;

            if (_activePoll is null)
            {
                _activePoll = new PreviewPollOperation(_resources);
                return default;
            }

            if (_activePoll.Task.IsCompleted)
            {
                // Start the next readback before handing back this result, or the
                // preview cadence halves to one frame per two poll ticks.
                PreviewPollOperation completed = _activePoll;
                _activePoll = new PreviewPollOperation(_resources);
                return completed.TakeResult();
            }

            if (_activePoll.Elapsed < ReadbackTimeout)
                return default;

            timedOut = _activePoll;
            _activePoll = null;
            _resources = null;
            _generation++;
            timedOut.Abandon();
            progress = timedOut.Resources.GetProgressSnapshot();
        }

        Console.WriteLine(
            $"[preview] frame readback stuck for 5 seconds; {FormatCaptureProgress(progress)}; " +
            "capture abandoned; preview disabled.");
        return new IdlePreviewPollResult(IdlePreviewPollState.Unavailable);
    }

    public CaptureProgressSnapshot? GetProgressSnapshot()
    {
        lock (_stateGate)
            return _resources?.GetProgressSnapshot();
    }

    public void Stop()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            CancelCurrentGeneration();
            QueueAttachedCleanup("background teardown");
        }
    }

    public async Task<IdlePreviewStreamFence?> AcquireStreamStartFenceAsync()
    {
        Task cleanup;
        lock (_stateGate)
        {
            if (_disposed || _streamStartFenced)
                return null;

            _streamStartFenced = true;
            CancelCurrentGeneration();
            cleanup = QueueAttachedCleanup("stream-start teardown");
        }

        // Only an in-flight teardown is worth waiting for. A teardown that
        // already failed cannot be retried (the capture is as released as it
        // will get, and WGC tolerates source overlap), so it must not block
        // every future stream start.
        await cleanup.ConfigureAwait(true);

        lock (_stateGate)
        {
            if (_disposed)
            {
                _streamStartFenced = false;
                return null;
            }
        }

        return new IdlePreviewStreamFence(this);
    }

    private bool CanCreate(int generation, CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            return !_disposed &&
                   !_streamStartFenced &&
                   generation == _generation &&
                   ReferenceEquals(_activeCreation, creationCts) &&
                   !creationCts.IsCancellationRequested;
        }
    }

    private bool IsGenerationCurrent(int generation)
    {
        lock (_stateGate)
            return !_disposed && !_streamStartFenced && generation == _generation;
    }

    private bool TryAttach(
        int generation,
        CancellationTokenSource creationCts,
        PreviewResources resources)
    {
        lock (_stateGate)
        {
            if (_disposed ||
                _streamStartFenced ||
                generation != _generation ||
                !ReferenceEquals(_activeCreation, creationCts) ||
                creationCts.IsCancellationRequested)
            {
                return false;
            }

            _resources = resources;
            return true;
        }
    }

    private bool CancelTimedOutGeneration(int generation, CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            if (_disposed ||
                _streamStartFenced ||
                generation != _generation ||
                !ReferenceEquals(_activeCreation, creationCts))
            {
                return false;
            }

            creationCts.Cancel();
            _generation++;
            QueueAttachedCleanup("timeout teardown");
            return true;
        }
    }

    private void CancelCurrentGeneration()
    {
        _activeCreation?.Cancel();
        _generation++;
    }

    private void CompleteCreation(CancellationTokenSource creationCts)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeCreation, creationCts))
                _activeCreation = null;
        }
        creationCts.Dispose();
    }

    private Task QueueAttachedCleanup(string action)
    {
        PreviewResources? resources = _resources;
        _resources = null;
        if (resources is null)
            return _attachedCleanup;

        PreviewPollOperation? poll = DetachPoll(resources);

        Task cleanup = RunCleanupAfterAsync(_lifecycleTail, resources, poll, action);
        _lifecycleTail = cleanup;
        _attachedCleanup = cleanup;
        return cleanup;
    }

    private PreviewPollOperation? DetachPoll(PreviewResources? resources)
    {
        if (_activePoll is null || !ReferenceEquals(_activePoll.Resources, resources))
            return null;

        PreviewPollOperation poll = _activePoll;
        _activePoll = null;
        poll.DiscardResult();
        return poll;
    }

    private static async Task RunCleanupAfterAsync(
        Task previousLifecycle,
        PreviewResources resources,
        PreviewPollOperation? poll,
        string action) =>
        await Task.Run(async () =>
        {
            try
            {
                await previousLifecycle.ConfigureAwait(false);
                await DisposeAfterPollAsync(resources, poll, action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[preview] {action} failed: {SingleLine(ex.Message)}");
            }
        }).ConfigureAwait(false);

    private static async Task<bool> DisposeAfterPollAsync(
        PreviewResources resources,
        PreviewPollOperation? poll,
        string action)
    {
        if (poll is not null && !poll.Task.IsCompleted)
        {
            TimeSpan remaining = ReadbackTimeout - poll.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                Task finished = await Task.WhenAny(poll.Task, Task.Delay(remaining)).ConfigureAwait(false);
                if (finished == poll.Task)
                    await poll.Task.ConfigureAwait(false);
            }

            if (!poll.Task.IsCompleted)
            {
                if (poll.Abandon())
                {
                    CaptureProgressSnapshot progress = resources.GetProgressSnapshot();
                    Console.WriteLine(
                        $"[preview] frame readback stuck for 5 seconds during {action}; " +
                        $"{FormatCaptureProgress(progress)}; capture abandoned.");
                }
                return false;
            }
        }

        resources.Dispose();
        return true;
    }

    private void ReleaseStreamStartFence()
    {
        lock (_stateGate) _streamStartFenced = false;
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static string FormatCaptureProgress(CaptureProgressSnapshot progress) =>
        $"callback-stage={progress.CallbackStage}, readback-stage={progress.ReadbackStage}, " +
        $"callbacks={progress.CallbacksStarted}, frames-ready={progress.FramesReady}, " +
        $"readbacks={progress.ReadbacksCompleted}/{progress.ReadbacksStarted}";

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelCurrentGeneration();
            QueueAttachedCleanup("shutdown teardown");
        }
    }

    private readonly record struct CreationOutcome(IdlePreviewStartState State, Exception? Error = null)
    {
        public static CreationOutcome Ready => new(IdlePreviewStartState.Ready);
        public static CreationOutcome Canceled => new(IdlePreviewStartState.Canceled);
    }

    private sealed class PreviewPollOperation
    {
        private int _abandoned;
        private int _discardResult;
        private int _resultDisposed;

        public PreviewPollOperation(PreviewResources resources)
        {
            Resources = resources;
            StartedTicks = Stopwatch.GetTimestamp();
            Task = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    return resources.Poll();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[preview] frame readback worker failed: {SingleLine(ex.Message)}");
                    return new IdlePreviewPollResult(IdlePreviewPollState.Unavailable);
                }
            });
            _ = Task.ContinueWith(
                _ => DisposeDiscardedResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public PreviewResources Resources { get; }
        public Task<IdlePreviewPollResult> Task { get; }
        public long StartedTicks { get; }
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedTicks);

        public IdlePreviewPollResult TakeResult() => Task.GetAwaiter().GetResult();

        public bool Abandon()
        {
            bool first = Interlocked.Exchange(ref _abandoned, 1) == 0;
            DiscardResult();
            return first;
        }

        public void DiscardResult()
        {
            Volatile.Write(ref _discardResult, 1);
            DisposeDiscardedResult();
        }

        private void DisposeDiscardedResult()
        {
            if (Volatile.Read(ref _discardResult) == 0 || !Task.IsCompletedSuccessfully)
                return;
            if (Interlocked.Exchange(ref _resultDisposed, 1) == 0)
                Task.Result.Image?.Dispose();
        }
    }

    internal sealed class IdlePreviewStreamFence : IDisposable
    {
        private IdlePreviewCapture? _owner;

        public IdlePreviewStreamFence(IdlePreviewCapture owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseStreamStartFence();
        }
    }

    private sealed class PreviewResources : IDisposable
    {
        private const int MaxPreviewWidth = 640;
        private const int MaxPreviewHeight = 360;

        private readonly ScreenCapture _capture;
        private readonly IntPtr _windowHandle;
        private readonly byte[] _buffer;
        private readonly GCHandle _bufferPin;
        private readonly Bitmap _sourceBitmap;
        private long _lastFrameVersion = -1;
        private IdlePreviewPollState _lastState = IdlePreviewPollState.NoChange;
        private int _disposed;

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        public PreviewResources(ScreenCapture capture, IntPtr windowHandle)
        {
            _capture = capture;
            _windowHandle = windowHandle;

            int byteCount = checked(capture.Width * capture.Height * 4);
            _buffer = GC.AllocateUninitializedArray<byte>(byteCount);
            _bufferPin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                _sourceBitmap = new Bitmap(
                    capture.Width,
                    capture.Height,
                    capture.Width * 4,
                    PixelFormat.Format32bppArgb,
                    _bufferPin.AddrOfPinnedObject());
            }
            catch
            {
                _bufferPin.Free();
                throw;
            }
        }

        public IdlePreviewPollResult Poll()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return ChangedState(IdlePreviewPollState.Unavailable);

            if (_windowHandle != IntPtr.Zero)
            {
                if (!IsWindow(_windowHandle))
                    return ChangedState(IdlePreviewPollState.Unavailable);
                if (IsIconic(_windowHandle))
                    return ChangedState(IdlePreviewPollState.Minimized);
            }

            if (_capture.CaptureError is { } callbackError)
                return CaptureUnavailable("frame callback", callbackError);

            long version = _capture.FrameVersion;
            if (version <= _lastFrameVersion)
                return default;

            if (!_capture.TryReadFrame(_buffer))
            {
                return _capture.CaptureError is { } readbackError
                    ? CaptureUnavailable("frame readback", readbackError)
                    : default;
            }

            _lastFrameVersion = version;
            _lastState = IdlePreviewPollState.Frame;
            return new IdlePreviewPollResult(IdlePreviewPollState.Frame, CreateScaledBitmap());
        }

        public CaptureProgressSnapshot GetProgressSnapshot() =>
            ((ICaptureDiagnostics)_capture).GetProgressSnapshot();

        private IdlePreviewPollResult ChangedState(IdlePreviewPollState state)
        {
            if (_lastState == state) return default;
            _lastState = state;
            return new IdlePreviewPollResult(state);
        }

        private IdlePreviewPollResult CaptureUnavailable(string action, Exception error)
        {
            if (_lastState != IdlePreviewPollState.Unavailable)
                Console.WriteLine($"[preview] {action} failed: {SingleLine(error.Message)}");
            return ChangedState(IdlePreviewPollState.Unavailable);
        }

        private Bitmap CreateScaledBitmap()
        {
            double scale = Math.Min(
                1.0,
                Math.Min((double)MaxPreviewWidth / _capture.Width, (double)MaxPreviewHeight / _capture.Height));
            int width = Math.Max(1, (int)Math.Round(_capture.Width * scale));
            int height = Math.Max(1, (int)Math.Round(_capture.Height * scale));
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(result);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    _sourceBitmap,
                    new Rectangle(0, 0, width, height),
                    0,
                    0,
                    _capture.Width,
                    _capture.Height,
                    GraphicsUnit.Pixel);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _capture.Dispose();
            }
            finally
            {
                _sourceBitmap.Dispose();
                _bufferPin.Free();
            }
        }
    }
}
