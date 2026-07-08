using System.Text;

namespace StreamHost.Util;

/// <summary>
/// Tees Console.Out/Error line-by-line to an event so the UI log box can show
/// everything the pipeline logs, while console/redirected output keeps working.
/// </summary>
public static class ConsoleMirror
{
    public static event Action<string>? LineWritten;
    private static StreamWriter? _logFile;
    public static string? LogFilePath { get; private set; }

    public static void Install(bool withLogFile = true)
    {
        if (withLogFile)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "logs");
                Directory.CreateDirectory(logDir);
                foreach (var old in new DirectoryInfo(logDir).GetFiles("*.log")
                             .OrderByDescending(f => f.CreationTimeUtc).Skip(9))
                    try { old.Delete(); } catch { }
                LogFilePath = Path.Combine(logDir, $"streamhost-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _logFile = new StreamWriter(LogFilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
            }
            catch { _logFile = null; }
        }
        Console.SetOut(new TeeWriter(Console.Out));
        Console.SetError(new TeeWriter(Console.Error));
        if (LogFilePath is not null)
            Console.WriteLine($"[log] writing to {LogFilePath}");
    }

    private sealed class TeeWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly Lock _gate = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                inner.Write(value);
                if (value == '\n')
                {
                    string line = _line.ToString().TrimEnd('\r');
                    try { _logFile?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); } catch { }
                    LineWritten?.Invoke(line);
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (char c in value) Write(c);
        }

        public override void Flush() { lock (_gate) inner.Flush(); }
    }
}
