using System.Diagnostics;

namespace Spectari.Capture;

/// <summary>Owns first-frame readiness and its user-facing failure diagnosis.</summary>
internal static class CaptureStartupGate
{
    internal static string? WaitForFirstFrame(
        ICaptureSource capture,
        bool captureDeviceSelected,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (capture.FrameVersion == 0)
        {
            if (cancellationToken.IsCancellationRequested) return "stopped";
            if (capture.CaptureError is not null)
            {
                return $"capture failed: {capture.CaptureError.Message} " +
                    $"(HRESULT 0x{capture.CaptureError.HResult:X8})";
            }
            if (Stopwatch.GetTimestamp() - started > Stopwatch.Frequency * 5)
            {
                Console.Error.WriteLine("[capture] no frames received within 5 seconds.");
                Console.Error.WriteLine(
                    $"[capture] backend started but never delivered a frame; adapter: {capture.AdapterName}.");
                Console.Error.WriteLine(captureDeviceSelected
                    ? "[capture] worth trying: reconnect the device, close other apps using it, or pick another device."
                    : "[capture] worth trying: a different source, the compatibility capture option, or a GPU driver update.");
                Console.Error.WriteLine(
                    "[capture] when reporting this, use Copy log in the app; the log file path is printed at startup.");
                return captureDeviceSelected
                    ? "no frames from capture device; see log"
                    : "no frames from screen capture; see log";
            }
            Thread.Sleep(100);
        }

        return null;
    }
}
