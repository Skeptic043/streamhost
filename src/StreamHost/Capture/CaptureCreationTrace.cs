namespace StreamHost.Capture;

internal sealed class CaptureCreationTrace
{
    // Per-step lines exist so the disk log names the exact wedged call even if
    // the whole process dies mid-creation. Monitor and window targets exercise
    // distinct driver paths, so each stays noisy until its own chain succeeds.
    private static volatile bool s_monitorChainProven;
    private static volatile bool s_windowChainProven;

    private string _lastCompletedStep = "none";
    private string _currentStep = "not started";

    public CaptureCreationTrace(string targetKind)
    {
        TargetKind = targetKind;
    }

    public string TargetKind { get; }
    public string LastCompletedStep => Volatile.Read(ref _lastCompletedStep);
    public string CurrentStep => Volatile.Read(ref _currentStep);

    public void Begin(string step)
    {
        Volatile.Write(ref _currentStep, step);
        if (!IsChainProven())
            Console.WriteLine($"[preview] {TargetKind} creation entering {step}");
    }

    public void Complete(string step)
    {
        Volatile.Write(ref _lastCompletedStep, step);
        Volatile.Write(ref _currentStep, "none");
    }

    public void MarkChainProven()
    {
        if (TargetKind == "window")
            s_windowChainProven = true;
        else
            s_monitorChainProven = true;
    }

    private bool IsChainProven() => TargetKind == "window"
        ? s_windowChainProven
        : s_monitorChainProven;
}
