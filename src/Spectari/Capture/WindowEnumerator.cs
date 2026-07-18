using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Spectari.Capture;

public sealed record WindowDescription(
    IntPtr Handle, string Title, string ProcessName, uint Pid, int Width, int Height);

public static class WindowEnumerator
{
    private readonly record struct WindowIdentity(IntPtr Handle, uint Pid);
    private readonly record struct WindowSize(int Width, int Height);

    // DWM reports minimized windows with unusable bounds. Keep the last good
    // enumerated size by real window identity so a minimized picker refresh does
    // not silently move the source into a lower bitrate class. The PID prevents a
    // recycled HWND from inheriting a window from another process.
    private static readonly object SizeCacheLock = new();
    private static readonly Dictionary<WindowIdentity, WindowSize> SizeCache = [];

    /// <summary>Visible, titled, non-cloaked top-level windows - the capturable set.</summary>
    public static List<WindowDescription> GetWindows()
    {
        lock (SizeCacheLock)
        {
            var windows = new List<WindowDescription>();
            var seen = new HashSet<WindowIdentity>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (IsCloaked(hWnd)) return true;
                // Monitor sharing owns full-desktop capture; Explorer's desktop
                // hosts are shell infrastructure, not application windows.
                if (IsDesktopShellWindow(hWnd)) return true;

                int len = GetWindowTextLengthW(hWnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowTextW(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                string process = "?";
                try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }

                var identity = new WindowIdentity(hWnd, pid);
                seen.Add(identity);
                WindowSize size = GetStableSize(hWnd, identity);
                if (size.Width < 2 || size.Height < 2) return true;
                windows.Add(new WindowDescription(hWnd, title, process, pid, size.Width, size.Height));
                return true;
            }, IntPtr.Zero);

            // A closed, hidden, or otherwise no-longer-capturable window must not
            // leave a size behind for a later handle reuse.
            foreach (var stale in SizeCache.Keys.Where(k => !seen.Contains(k)).ToArray())
                SizeCache.Remove(stale);

            return windows;
        }
    }

    private static WindowSize GetStableSize(IntPtr hWnd, WindowIdentity identity)
    {
        bool canCache = identity.Pid != 0;
        WindowSize cached = default;
        bool hasCached = canCache && SizeCache.TryGetValue(identity, out cached);

        // Never sample live bounds while minimized: those can be tiny placeholder
        // rectangles. A good cached size wins until the window is restored.
        if (!IsIconic(hWnd) && TryGetLiveSize(hWnd, out WindowSize live))
        {
            if (canCache) SizeCache[identity] = live;
            return live;
        }

        if (hasCached) return cached;

        // GetWindowPlacement preserves the restored rectangle for a window first
        // encountered while minimized, and is also a safe last resort if a live
        // bounds read temporarily fails.
        if (TryGetRestoredSize(hWnd, out WindowSize restored))
        {
            if (canCache) SizeCache[identity] = restored;
            return restored;
        }

        return default;
    }

    private static bool TryGetLiveSize(IntPtr hWnd, out WindowSize size)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS = 9: visible bounds without the invisible
        // resize border, matching what Native previously showed while restored.
        if (DwmGetWindowAttribute(hWnd, 9, out RECT bounds, Marshal.SizeOf<RECT>()) == 0 &&
            TrySize(bounds, out size))
            return true;

        if (GetWindowRect(hWnd, out bounds) && TrySize(bounds, out size))
            return true;

        size = default;
        return false;
    }

    private static bool TryGetRestoredSize(IntPtr hWnd, out WindowSize size)
    {
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hWnd, ref placement) && TrySize(placement.NormalPosition, out size))
            return true;

        size = default;
        return false;
    }

    private static bool TrySize(RECT bounds, out WindowSize size)
    {
        long width = (long)bounds.Right - bounds.Left;
        long height = (long)bounds.Bottom - bounds.Top;
        if (width > 0 && width <= int.MaxValue && height > 0 && height <= int.MaxValue)
        {
            size = new WindowSize((int)width, (int)height);
            return true;
        }

        size = default;
        return false;
    }

    // Console/terminal windows echo the command line in their title, so a search
    // for --window "Game" would otherwise match our own console. Never our own
    // process, and terminals only as a last resort.
    private static readonly string[] TerminalProcesses =
        ["conhost", "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh", "Spectari"];

    public static WindowDescription? FindByTitle(string titleSubstring)
    {
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr ownConsole = GetConsoleWindow();
        return GetWindows()
            .Where(w => w.Pid != ownPid && w.Handle != ownConsole)
            .Where(w => w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase) ||
                        w.ProcessName.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => TerminalProcesses.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static bool IsCloaked(IntPtr hWnd)
    {
        // DWMWA_CLOAKED = 14: filters invisible UWP/background windows
        return DwmGetWindowAttribute(hWnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool IsDesktopShellWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(256);
        if (GetClassNameW(hWnd, className, className.Capacity) == 0) return false;
        return className.ToString() is "Progman" or "WorkerW";
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
}
