using System.Runtime.InteropServices;

namespace StreamHost.Capture;

/// <summary>Detects "a fullscreen app is focused on this monitor" — the signal that
/// standard capture starvation is a fullscreen problem rather than a static screen.</summary>
public static class ForegroundProbe
{
    public static bool FullscreenAppOnMonitor(IntPtr hMonitor)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */) != hMonitor) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(hMonitor, ref mi)) return false;
        if (!GetWindowRect(fg, out RECT wr)) return false;

        // Fullscreen-ish: the window covers the monitor (small tolerance for borders).
        return wr.left <= mi.rcMonitor.left + 2 && wr.top <= mi.rcMonitor.top + 2 &&
               wr.right >= mi.rcMonitor.right - 2 && wr.bottom >= mi.rcMonitor.bottom - 2;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
}
