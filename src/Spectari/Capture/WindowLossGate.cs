namespace Spectari.Capture;

internal enum WindowLossReason
{
    CaptureItemClosed = 1,
    InvalidWindowHandle = 2,
}

internal sealed class WindowLossGate
{
    private int _winner;

    internal WindowLossReason? Winner
    {
        get
        {
            int winner = Volatile.Read(ref _winner);
            return winner == 0 ? null : (WindowLossReason)winner;
        }
    }

    internal bool TryClaim(WindowLossReason reason) =>
        Interlocked.CompareExchange(ref _winner, (int)reason, 0) == 0;
}
