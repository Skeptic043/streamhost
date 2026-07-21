using System.Net;
using Spectari.Encode;

namespace Spectari;

/// <summary>Publishes logical session completion once, including when the
/// physical session thread has to be abandoned.</summary>
internal sealed class SessionTerminationGate
{
    private const int Running = 0;
    private const int SalvageClaimed = 1;
    private const int Completed = 2;

    private readonly ManualResetEventSlim _ended = new(false);
    private int _state;

    public event Action<string>? Stopped;

    public bool IsCompleted => Volatile.Read(ref _state) == Completed;

    public bool Wait(int timeoutMs) => _ended.Wait(timeoutMs);

    public bool TryClaimSalvage() =>
        Interlocked.CompareExchange(ref _state, SalvageClaimed, Running) == Running;

    public bool CompleteFromSession(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, Completed, Running) != Running)
            return false;

        Publish(reason);
        return true;
    }

    public bool CompleteFromSalvage(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, Completed, SalvageClaimed) != SalvageClaimed)
            return false;

        Publish(reason);
        return true;
    }

    private void Publish(string reason)
    {
        Console.WriteLine(reason == "stopped"
            ? "[shutdown] done"
            : $"[shutdown] stopped: {reason}");
        _ended.Set();
        Stopped?.Invoke(reason);
    }
}

/// <summary>Gives normal teardown and stalled-session salvage exclusive
/// ownership of one cleanup operation.</summary>
internal sealed class SalvageableCleanup(Action cleanup) : IDisposable
{
    private const int Active = 0;
    private const int Disposing = 1;
    private const int Disposed = 2;
    private const int Abandoned = 3;

    private readonly ManualResetEventSlim _finished = new(false);
    private int _state;
    private int _succeeded;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, Disposing, Active) != Active)
            return;

        Execute(() =>
        {
            cleanup();
            return true;
        }, rethrow: true);
    }

    public bool TryDisposeForSalvage(int timeoutMs) =>
        TryRunForSalvage(() =>
        {
            cleanup();
            return true;
        }, timeoutMs);

    public bool TryRunForSalvage(Func<bool> salvage, int timeoutMs)
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            switch (state)
            {
                case Active:
                    if (Interlocked.CompareExchange(ref _state, Disposing, Active) == Active)
                        return Execute(salvage, rethrow: false);
                    break;
                case Disposing:
                    return _finished.Wait(timeoutMs) && Volatile.Read(ref _succeeded) != 0;
                case Disposed:
                    return Volatile.Read(ref _succeeded) != 0;
                case Abandoned:
                    return false;
            }
        }
    }

    public bool Abandon()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            switch (state)
            {
                case Active:
                    if (Interlocked.CompareExchange(ref _state, Abandoned, Active) == Active)
                    {
                        _finished.Set();
                        return true;
                    }
                    break;
                case Disposing:
                    // The owning thread is already inside this cleanup. It and the
                    // resource are abandoned together; no second caller may enter.
                    return true;
                case Disposed:
                case Abandoned:
                    return true;
            }
        }
    }

    private bool Execute(Func<bool> operation, bool rethrow)
    {
        bool succeeded = false;
        try
        {
            succeeded = operation();
            return succeeded;
        }
        catch
        {
            if (rethrow) throw;
            return false;
        }
        finally
        {
            if (succeeded) Volatile.Write(ref _succeeded, 1);
            Volatile.Write(ref _state, Disposed);
            _finished.Set();
        }
    }
}

/// <summary>Owns the deadline and ordered external cleanup for a session whose
/// pipeline thread did not respond to cancellation.</summary>
internal sealed class StallTeardownCoordinator
{
    private const int SalvageStepTimeoutMs = 5000;
    private const int SalvageOverallTimeoutMs = 15000;

    private readonly SessionTerminationGate _termination;
    private readonly int _teardownTimeoutMs;
    private readonly int _port;
    private readonly FfmpegEncoder _ffmpeg;
    private readonly SalvageableCleanup _ffmpegLifetime;
    private readonly string _serverPrefix;
    private readonly SalvageableCleanup _serverLifetime;
    private readonly SalvageableCleanup _audioLifetime;
    private readonly SalvageableCleanup _writerLifetime;
    private readonly SalvageableCleanup _hardwareVideoLifetime;
    private readonly SalvageableCleanup _captureLifetime;
    private int _armed;

    public StallTeardownCoordinator(SessionTerminationGate termination,
        int teardownTimeoutMs, int port, FfmpegEncoder ffmpeg,
        SalvageableCleanup ffmpegLifetime, string serverPrefix,
        SalvageableCleanup serverLifetime, SalvageableCleanup audioLifetime,
        SalvageableCleanup writerLifetime,
        SalvageableCleanup hardwareVideoLifetime,
        SalvageableCleanup captureLifetime)
    {
        _termination = termination;
        _teardownTimeoutMs = teardownTimeoutMs;
        _port = port;
        _ffmpeg = ffmpeg;
        _ffmpegLifetime = ffmpegLifetime;
        _serverPrefix = serverPrefix;
        _serverLifetime = serverLifetime;
        _audioLifetime = audioLifetime;
        _writerLifetime = writerLifetime;
        _hardwareVideoLifetime = hardwareVideoLifetime;
        _captureLifetime = captureLifetime;
    }

    public void Arm(string stopReason, Func<string> activeStages)
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        new Thread(() => RunDeadline(stopReason, activeStages))
        { IsBackground = true, Name = "stall-teardown-deadline" }.Start();
    }

    private void RunDeadline(string stopReason, Func<string> activeStages)
    {
        if (_termination.Wait(_teardownTimeoutMs)) return;
        if (!_termination.TryClaimSalvage()) return;

        string stages = Snapshot(activeStages);
        SalvageResult result = default;
        using var salvageFinished = new ManualResetEventSlim(false);
        new Thread(() =>
        {
            try { result = Salvage(); }
            catch (Exception ex)
            {
                result = SalvageResult.Failed(
                    $"stalled-session salvage threw while releasing resources ({ex.Message})");
            }
            finally { salvageFinished.Set(); }
        })
        { IsBackground = true, Name = "stall-teardown-salvage" }.Start();

        if (!salvageFinished.Wait(SalvageOverallTimeoutMs))
        {
            FailClosed("stalled-session salvage did not finish in time", stages);
            return;
        }
        if (!result.Succeeded)
        {
            FailClosed(result.Failure, stages);
            return;
        }

        Console.Error.WriteLine(
            $"[shutdown] salvaged stalled session: ffmpeg child stopped, web server stopped and port {_port} released, audio stopped, video input and hardware encoder resources stopped; abandoned capture object and parked session thread; active stages at deadline: {stages}.");
        _termination.CompleteFromSalvage(stopReason);
    }

    private SalvageResult Salvage()
    {
        bool ffmpegStopped = _ffmpegLifetime.TryRunForSalvage(() =>
        {
            _ffmpeg.AbortForStall();
            _ffmpeg.Dispose();
            return _ffmpeg.TerminationConfirmed;
        }, SalvageStepTimeoutMs);
        if (!ffmpegStopped || !_ffmpeg.TerminationConfirmed)
            return SalvageResult.Failed("could not confirm the ffmpeg child stopped");

        if (!_serverLifetime.TryDisposeForSalvage(SalvageStepTimeoutMs))
            return SalvageResult.Failed("web server cleanup did not finish");
        if (!TryConfirmPrefixAvailable(_serverPrefix))
            return SalvageResult.Failed($"could not confirm port {_port} was released");

        if (!_audioLifetime.TryDisposeForSalvage(SalvageStepTimeoutMs))
            return SalvageResult.Failed("audio cleanup did not finish");
        if (!_writerLifetime.TryDisposeForSalvage(SalvageStepTimeoutMs))
            return SalvageResult.Failed("ffmpeg frame-writer cleanup did not finish");
        if (!_hardwareVideoLifetime.TryDisposeForSalvage(SalvageStepTimeoutMs))
            return SalvageResult.Failed("hardware video cleanup did not finish");

        _captureLifetime.Abandon();
        return SalvageResult.Success;
    }

    private static bool TryConfirmPrefixAvailable(string prefix)
    {
        try
        {
            using var probe = new HttpListener();
            probe.Prefixes.Add(prefix);
            probe.Start();
            return probe.IsListening;
        }
        catch
        {
            // The caller logs this once as a port-release failure before fail-close.
            return false;
        }
    }

    private static string Snapshot(Func<string> activeStages)
    {
        try { return activeStages(); }
        catch { return "stage snapshot unavailable"; }
    }

    private static void FailClosed(string failure, string stages)
    {
        Console.Error.WriteLine($"[shutdown] stalled-session salvage failed: {failure}; active stages at deadline: {stages}.");
        Console.Error.WriteLine(
            "[shutdown] closing Spectari to prevent invisible or orphaned server or ffmpeg work. Reopen it and use Copy log for the stage diagnostics.");
        Environment.Exit(1);
    }

    private readonly record struct SalvageResult(bool Succeeded, string Failure)
    {
        public static SalvageResult Success => new(true, "");
        public static SalvageResult Failed(string failure) => new(false, failure);
    }
}
