using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Spectari.Capture;

internal enum WindowCaptureMinUpdateIntervalStatus
{
    Applied,
    Unavailable,
    InterfaceQueryFailed,
    ApplyFailed,
}

internal readonly record struct WindowCaptureMinUpdateIntervalResult(
    WindowCaptureMinUpdateIntervalStatus Status,
    TimeSpan Interval,
    int TargetFramesPerSecond,
    int HResult = 0);

internal static class WindowCaptureMinUpdateInterval
{
    private const int NoInterface = unchecked((int)0x80004002);
    internal const int TargetFramesPerSecond = 60;
    private const int PutMinUpdateIntervalSlot = 7;
    private static readonly Guid InterfaceId = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");

    internal static TimeSpan TargetInterval { get; } = TimeSpan.FromTicks(
        (long)Math.Round(TimeSpan.TicksPerSecond / (double)TargetFramesPerSecond));

    internal static WindowCaptureMinUpdateIntervalResult Apply(GraphicsCaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        nint session5 = 0;
        WindowCaptureMinUpdateIntervalStatus failureStatus =
            WindowCaptureMinUpdateIntervalStatus.InterfaceQueryFailed;
        try
        {
            nint unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
            int result = Marshal.QueryInterface(unknown, in InterfaceId, out session5);
            if (result == NoInterface)
            {
                return new(
                    WindowCaptureMinUpdateIntervalStatus.Unavailable,
                    TargetInterval,
                    TargetFramesPerSecond);
            }

            if (result < 0)
            {
                return new(
                    WindowCaptureMinUpdateIntervalStatus.InterfaceQueryFailed,
                    TargetInterval,
                    TargetFramesPerSecond,
                    result);
            }

            failureStatus = WindowCaptureMinUpdateIntervalStatus.ApplyFailed;
            result = SetMinUpdateInterval(session5, TargetInterval.Ticks);
            return result < 0
                ? new(failureStatus, TargetInterval, TargetFramesPerSecond, result)
                : new(
                    WindowCaptureMinUpdateIntervalStatus.Applied,
                    TargetInterval,
                    TargetFramesPerSecond);
        }
        catch (Exception ex)
        {
            return new(failureStatus, TargetInterval, TargetFramesPerSecond, ex.HResult);
        }
        finally
        {
            if (session5 != 0)
                Marshal.Release(session5);
        }
    }

    private static unsafe int SetMinUpdateInterval(nint session5, long intervalTicks)
    {
        nint vtable = Marshal.ReadIntPtr(session5);
        // IInspectable owns slots 0 through 5; this interface adds get at 6 and put at 7.
        nint method = Marshal.ReadIntPtr(vtable, PutMinUpdateIntervalSlot * nint.Size);
        return ((delegate* unmanaged[Stdcall]<nint, long, int>)method)(session5, intervalTicks);
    }
}
