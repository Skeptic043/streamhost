namespace Spectari.Util;

public static class AppPaths
{
    private static readonly string ApplicationDataDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string LegacyRootDirectory =
        Path.Combine(ApplicationDataDirectory, "StreamHost");

    public static string RootDirectory { get; } =
        Path.Combine(ApplicationDataDirectory, "Spectari");

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");
    public static string EncoderCacheFile { get; } = Path.Combine(RootDirectory, "encoder.cache");
    public static string HardwareEncoderCacheFile { get; } =
        Path.Combine(RootDirectory, "mf-encoder.cache");
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string PeersFile { get; } = Path.Combine(RootDirectory, "peers.json");
    public static string WebView2UserDataDirectory { get; } = Path.Combine(RootDirectory, "webview2");

    /// <summary>Outcome line from MigrateLegacyData, deferred because migration
    /// must run before any console or log sink exists to receive it.</summary>
    public static string? MigrationNote { get; private set; }

    public static void MigrateLegacyData()
    {
        if (Directory.Exists(RootDirectory) || !Directory.Exists(LegacyRootDirectory))
            return;

        try
        {
            Directory.Move(LegacyRootDirectory, RootDirectory);
            MigrationNote = $"[data] moved app data from {LegacyRootDirectory} to {RootDirectory}.";
        }
        catch
        {
            try
            {
                CopyDirectory(LegacyRootDirectory, RootDirectory);
                MigrationNote = $"[data] copied app data from {LegacyRootDirectory} to {RootDirectory}; the old directory remains in place.";
            }
            catch
            {
                // A half-copied destination would shadow the intact legacy data on
                // every later run; remove it so the next start retries migration.
                try { Directory.Delete(RootDirectory, recursive: true); } catch { }
                MigrationNote = $"[data] could not migrate app data from {LegacyRootDirectory} to {RootDirectory}; continuing with defaults.";
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);

        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
