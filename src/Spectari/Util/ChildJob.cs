using System.Runtime.InteropServices;

namespace Spectari.Util;

/// <summary>
/// One kill-on-close job object for all child processes. Windows closes the
/// job handle when Spectari dies - cleanly, by crash, or by Task Manager -
/// and kills every adopted child with it, so a force-quit can never leave an
/// orphaned ffmpeg behind holding pipes or confusing the next launch.
/// </summary>
public static class ChildJob
{
    private static readonly IntPtr Handle = Create();
    private static bool _warned; // one-time honesty warning if orphan protection is off

    /// <summary>Best effort: a process that can't be adopted still runs. When the
    /// job couldn't be created, or a process refuses to join it (already in a job
    /// that forbids nesting), the "a force-quit can never orphan ffmpeg" guarantee
    /// does not hold for this run - say so once so the log is honest.</summary>
    public static void Adopt(System.Diagnostics.Process process)
    {
        try
        {
            if (Handle == IntPtr.Zero)
            {
                WarnOnce("job object could not be created");
                return;
            }
            if (!AssignProcessToJobObject(Handle, process.Handle))
                WarnOnce($"process {process.Id} could not be adopted (error {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) { WarnOnce(ex.Message); }
    }

    private static void WarnOnce(string detail)
    {
        if (_warned) return;
        _warned = true;
        Console.Error.WriteLine($"[childjob] orphan protection not active: {detail}; a force-quit may leave ffmpeg running");
    }

    private static IntPtr Create()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x2000, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                },
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)len))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
