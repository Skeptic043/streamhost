using System.Diagnostics;

namespace Spectari.Util;

/// <summary>Result of a <see cref="ProcessRunner"/> run. <see cref="ExitCode"/> is
/// null when the process overran its timeout and was killed.</summary>
public readonly record struct ProcessResult(bool TimedOut, int? ExitCode, string StdOut, string StdErr);

/// <summary>
/// One deadlock-free, leak-free way to run a short-lived child process and
/// collect its output. Both stdout and stderr are drained concurrently (a child
/// that fills one pipe while the parent blocks on the other is a classic hang),
/// the child is adopted into <see cref="ChildJob"/> so a force-quit can't orphan
/// it, and an overrun is tree-killed instead of blocking the caller forever.
/// Callers keep their own try/catch: a missing exe throws out of here (as it
/// would from Process.Start), it is not swallowed.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Run <paramref name="fileName"/> with <paramref name="arguments"/>,
    /// giving up after <paramref name="timeoutMs"/>. Never blocks past the timeout.</summary>
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Process.Start throws on a missing exe (Win32Exception); let it - the
        // callers catch it. Start() with these options never returns null.
        using var p = Process.Start(psi)!;
        ChildJob.Adopt(p); // dies with us even if this runner somehow leaks it

        // Drain both pipes before waiting, so neither can fill and deadlock.
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            // Pipes are closing now the child is dead; give the readers a moment.
            try { Task.WaitAll(new Task[] { outTask, errTask }, 1000); } catch { }
            return new ProcessResult(
                TimedOut: true,
                ExitCode: null,
                StdOut: outTask.IsCompletedSuccessfully ? outTask.Result : "",
                StdErr: errTask.IsCompletedSuccessfully ? errTask.Result : "");
        }

        // Exited in time: the redirected streams hit EOF, so the readers finish.
        // GetResult flushes them.
        string stdout = outTask.GetAwaiter().GetResult();
        string stderr = errTask.GetAwaiter().GetResult();
        return new ProcessResult(false, p.ExitCode, stdout, stderr);
    }
}
