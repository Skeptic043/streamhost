using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StreamHost.Capture;

internal enum IdlePreviewPollState
{
    NoChange,
    Frame,
    Minimized,
    Unavailable,
}

internal readonly record struct IdlePreviewPollResult(IdlePreviewPollState State, Bitmap? Image = null);

/// <summary>
/// Preview-only wrapper around WGC. The capture keeps its latest frame on the GPU;
/// MainForm asks for a small bitmap at a low rate. Nothing here is shared with a
/// StreamSession, and disposing this object synchronously releases the WGC session.
/// </summary>
internal sealed class IdlePreviewCapture : IDisposable
{
    private const int MaxPreviewWidth = 640;
    private const int MaxPreviewHeight = 360;

    private readonly ScreenCapture _capture;
    private readonly IntPtr _windowHandle;
    private readonly byte[] _buffer;
    private readonly GCHandle _bufferPin;
    private readonly Bitmap _sourceBitmap;
    private long _lastFrameVersion = -1;
    private IdlePreviewPollState _lastState = IdlePreviewPollState.NoChange;
    private bool _disposed;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    public static IdlePreviewCapture ForMonitor(IntPtr monitorHandle)
    {
        ScreenCapture capture = ScreenCapture.ForPreviewMonitor(monitorHandle);
        try { return new IdlePreviewCapture(capture, IntPtr.Zero); }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public static IdlePreviewCapture ForWindow(IntPtr windowHandle)
    {
        ScreenCapture capture = ScreenCapture.ForPreviewWindow(windowHandle);
        try { return new IdlePreviewCapture(capture, windowHandle); }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private IdlePreviewCapture(ScreenCapture capture, IntPtr windowHandle)
    {
        _capture = capture;
        _windowHandle = windowHandle;

        int byteCount = checked(capture.Width * capture.Height * 4);
        _buffer = GC.AllocateUninitializedArray<byte>(byteCount);
        _bufferPin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            _sourceBitmap = new Bitmap(
                capture.Width,
                capture.Height,
                capture.Width * 4,
                PixelFormat.Format32bppArgb,
                _bufferPin.AddrOfPinnedObject());
        }
        catch
        {
            _bufferPin.Free();
            throw;
        }
    }

    public IdlePreviewPollResult Poll()
    {
        if (_disposed) return ChangedState(IdlePreviewPollState.Unavailable);

        if (_windowHandle != IntPtr.Zero)
        {
            if (!IsWindow(_windowHandle))
                return ChangedState(IdlePreviewPollState.Unavailable);
            if (IsIconic(_windowHandle))
                return ChangedState(IdlePreviewPollState.Minimized);
        }

        if (_capture.CaptureError is not null)
            return ChangedState(IdlePreviewPollState.Unavailable);

        long version = _capture.FrameVersion;
        if (version <= _lastFrameVersion)
            return default;

        if (!_capture.TryReadFrame(_buffer))
            return _capture.CaptureError is null
                ? default
                : ChangedState(IdlePreviewPollState.Unavailable);

        _lastFrameVersion = version;
        _lastState = IdlePreviewPollState.Frame;
        return new IdlePreviewPollResult(IdlePreviewPollState.Frame, CreateScaledBitmap());
    }

    private IdlePreviewPollResult ChangedState(IdlePreviewPollState state)
    {
        if (_lastState == state) return default;
        _lastState = state;
        return new IdlePreviewPollResult(state);
    }

    private Bitmap CreateScaledBitmap()
    {
        double scale = Math.Min(
            1.0,
            Math.Min((double)MaxPreviewWidth / _capture.Width, (double)MaxPreviewHeight / _capture.Height));
        int width = Math.Max(1, (int)Math.Round(_capture.Width * scale));
        int height = Math.Max(1, (int)Math.Round(_capture.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                _sourceBitmap,
                new Rectangle(0, 0, width, height),
                0,
                0,
                _capture.Width,
                _capture.Height,
                GraphicsUnit.Pixel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _capture.Dispose();
        }
        finally
        {
            _sourceBitmap.Dispose();
            _bufferPin.Free();
        }
    }
}
