using System.Runtime.InteropServices;

namespace StreamHost.Capture;

public sealed record MonitorDescription(IntPtr Handle, string DeviceName, int Width, int Height, bool IsPrimary);

public static class MonitorEnumerator
{
    public static List<MonitorDescription> GetMonitors()
    {
        var monitors = new List<MonitorDescription>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref RECT __, IntPtr ___) =>
            {
                var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    monitors.Add(new MonitorDescription(
                        hMonitor,
                        info.szDevice.TrimEnd('\0'),
                        info.rcMonitor.right - info.rcMonitor.left,
                        info.rcMonitor.bottom - info.rcMonitor.top,
                        (info.dwFlags & 1) != 0));
                }
                return true;
            }, IntPtr.Zero);
        // Primary first, then stable order
        return monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.DeviceName).ToList();
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
