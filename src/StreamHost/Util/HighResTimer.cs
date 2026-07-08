using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamHost.Util;

/// <summary>
/// Sub-millisecond wall-clock waits via a high-resolution waitable timer
/// (Windows 10 1803+). Thread.Sleep has ~1.5 ms jitter even with
/// timeBeginPeriod(1), which is visible in frame pacing at 60 fps.
/// </summary>
public sealed class HighResTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr r1, IntPtr r2, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly IntPtr _handle;
    public bool IsHighResolution { get; }

    public HighResTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        IsHighResolution = _handle != IntPtr.Zero;
    }

    /// <summary>Sleeps until the given Stopwatch timestamp (no-op if already past).</summary>
    public void WaitUntil(long stopwatchTimestamp)
    {
        long now = Stopwatch.GetTimestamp();
        if (stopwatchTimestamp <= now) return;

        long delta100ns = (stopwatchTimestamp - now) * 10_000_000 / Stopwatch.Frequency;
        if (IsHighResolution)
        {
            long due = -delta100ns; // negative = relative
            if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_handle, INFINITE);
                return;
            }
        }
        int ms = (int)(delta100ns / 10_000);
        if (ms > 0) Thread.Sleep(ms);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }
}
