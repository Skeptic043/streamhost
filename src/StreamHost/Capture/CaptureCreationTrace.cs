namespace StreamHost.Capture;

internal sealed class CaptureCreationTrace
{
    // Per-step lines exist so the disk log names the exact wedged call even if
    // the whole process dies mid-creation; once one full chain has completed in
    // this process the driver path is proven and the lines would be noise.
    private static volatile bool s_chainProven;

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
        if (!s_chainProven)
            Console.WriteLine($"[preview] {TargetKind} creation entering {step}");
    }

    public void Complete(string step)
    {
        Volatile.Write(ref _lastCompletedStep, step);
        Volatile.Write(ref _currentStep, "none");
    }

    public void MarkChainProven() => s_chainProven = true;
}
