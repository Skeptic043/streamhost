using System.Diagnostics;
using Spectari.Capture;

namespace Spectari.Ui;

/// <summary>Reports a stalled UI pump without depending on that pump to write the diagnostic.</summary>
internal sealed class UiHangWatchdog : IDisposable
{
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(10);

    private readonly Control _ui;
    private readonly Func<CaptureProgressSnapshot?> _captureProgress;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _lastHeartbeatTicks = Stopwatch.GetTimestamp();
    private int _heartbeatPending;
    private int _operationSequence;
    private string? _currentOperation;
    private string _lastOperation = "none recorded";
    private int _diagnosticsFailureLogged;
    private int _disposed;

    public UiHangWatchdog(Control ui, Func<CaptureProgressSnapshot?> captureProgress)
    {
        _ui = ui;
        _captureProgress = captureProgress;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Spectari UI hang watchdog",
        };
        _thread.Start();
    }

    public IDisposable TrackOperation(string operation)
    {
        Volatile.Write(ref _lastOperation, operation);
        string token = $"{operation}\u001f{Interlocked.Increment(ref _operationSequence)}";
        Volatile.Write(ref _currentOperation, token);
        return new OperationScope(this, token);
    }

    private void Run()
    {
        long stalledSince = 0;
        long lastStallLog = 0;
        string stalledOperation = "none recorded";
        bool postFailureLogged = false;

        while (!_stop.Wait(250))
        {
            if (Volatile.Read(ref _heartbeatPending) == 0 &&
                Interlocked.CompareExchange(ref _heartbeatPending, 1, 0) == 0)
            {
                try
                {
                    _ui.BeginInvoke((Action)(() =>
                    {
                        Interlocked.Exchange(ref _lastHeartbeatTicks, Stopwatch.GetTimestamp());
                        Volatile.Write(ref _heartbeatPending, 0);
                    }));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    Volatile.Write(ref _heartbeatPending, 0);
                    if (Volatile.Read(ref _disposed) != 0)
                        return;
                    if (!postFailureLogged)
                    {
                        postFailureLogged = true;
                        Console.WriteLine(
                            $"[ui-watchdog] heartbeat post failed: {SingleLine(ex.Message)}");
                    }
                }
            }

            long now = Stopwatch.GetTimestamp();
            long lastHeartbeat = Interlocked.Read(ref _lastHeartbeatTicks);

            if (stalledSince != 0 && lastHeartbeat > stalledSince)
            {
                Console.WriteLine(
                    $"[ui-watchdog] UI thread recovered after {Seconds(lastHeartbeat - stalledSince):F1}s; " +
                    $"last risky operation: {stalledOperation}.");
                stalledSince = 0;
                lastStallLog = 0;
            }

            if (Stopwatch.GetElapsedTime(lastHeartbeat, now) < HangThreshold)
                continue;

            if (stalledSince == 0)
            {
                stalledSince = lastHeartbeat;
                stalledOperation = CurrentOrLastOperation();
            }

            if (lastStallLog == 0 || Stopwatch.GetElapsedTime(lastStallLog, now) >= RepeatInterval)
            {
                lastStallLog = now;
                LogStall(now - stalledSince, stalledOperation);
            }
        }
    }

    private void LogStall(long elapsedTicks, string operation)
    {
        CaptureProgressSnapshot? progress = null;
        try
        {
            progress = _captureProgress();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _diagnosticsFailureLogged, 1) == 0)
                Console.WriteLine(
                    $"[ui-watchdog] preview diagnostics read failed: {SingleLine(ex.Message)}");
        }

        string preview = progress is { } p
            ? $"; preview capture: callback-stage={p.CallbackStage}, readback-stage={p.ReadbackStage}, " +
              $"callbacks={p.CallbacksStarted}, frames-ready={p.FramesReady}, " +
              $"readbacks={p.ReadbacksCompleted}/{p.ReadbacksStarted}"
            : "";
        Console.WriteLine(
            $"[ui-watchdog] UI thread unresponsive for {Seconds(elapsedTicks):F1}s; " +
            $"last risky operation: {operation}{preview}.");
    }

    private string CurrentOrLastOperation()
    {
        string? current = Volatile.Read(ref _currentOperation);
        if (current is null)
            return Volatile.Read(ref _lastOperation);
        int separator = current.LastIndexOf('\u001f');
        return separator >= 0 ? current[..separator] : current;
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Set();
        Volatile.Write(ref _currentOperation, null);
    }

    private sealed class OperationScope(UiHangWatchdog owner, string token) : IDisposable
    {
        private UiHangWatchdog? _owner = owner;

        public void Dispose()
        {
            UiHangWatchdog? currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is not null)
                Interlocked.CompareExchange(ref currentOwner._currentOperation, null, token);
        }
    }
}
