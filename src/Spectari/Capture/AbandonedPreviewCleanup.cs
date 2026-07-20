namespace Spectari.Capture;

/// <summary>Retires preview resources without making a blocked GPU readback
/// part of the caller's lifecycle deadline.</summary>
internal static class AbandonedPreviewCleanup
{
    internal static Task Start(
        IDisposable resources,
        string action,
        Action<string> log) =>
        Task.Factory.StartNew(
            () =>
            {
                try
                {
                    resources.Dispose();
                }
                catch (Exception ex)
                {
                    log($"[preview] {action} background retirement failed: {SingleLine(ex.Message)}");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}
