using System.Runtime.InteropServices;

namespace Spectari.Util;

/// <summary>
/// One GUI instance per user session. A second launch pings the first — which
/// restores and focuses its window — then exits, so a user who lost track of
/// the window (minimized, buried behind a game) gets the existing one back
/// instead of starting a rival process that collides on the stream port.
/// Console mode (any CLI arg) is unaffected: several console streams on
/// different ports are legitimate.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex; // held for the whole process lifetime

    /// <summary>System-wide message id (same string → same id in every process),
    /// broadcast by a late launch and caught by the running window's WndProc.</summary>
    public static readonly int ShowMessage = RegisterWindowMessage("Spectari.Show.9C2F1A");

    /// <summary>True if we are the first instance (and now hold the lock). False
    /// if one was already running — it has been pinged to surface its window.</summary>
    public static bool TryAcquire()
    {
        try
        {
            // Local\ = per session, so two logged-in users don't block each other.
            _mutex = new Mutex(initiallyOwned: true, @"Local\Spectari.SingleInstance", out bool createdNew);
            if (createdNew) return true;
        }
        catch
        {
            // If the lock can't be created, fail open — better a possible second
            // instance than refusing to launch at all.
            return true;
        }
        PostMessage(HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
