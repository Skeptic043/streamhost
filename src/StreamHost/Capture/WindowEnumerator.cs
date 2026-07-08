using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamHost.Capture;

public sealed record WindowDescription(IntPtr Handle, string Title, string ProcessName, uint Pid);

public static class WindowEnumerator
{
    /// <summary>Visible, titled, non-cloaked top-level windows — the capturable set.</summary>
    public static List<WindowDescription> GetWindows()
    {
        var windows = new List<WindowDescription>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (IsCloaked(hWnd)) return true;

            int len = GetWindowTextLengthW(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string process = "?";
            try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }

            windows.Add(new WindowDescription(hWnd, title, process, pid));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    // Console/terminal windows echo the command line in their title, so a search
    // for --window "Game" would otherwise match our own console. Never our own
    // process, and terminals only as a last resort.
    private static readonly string[] TerminalProcesses =
        ["conhost", "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh", "StreamHost"];

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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
}
