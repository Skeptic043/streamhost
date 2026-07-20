using System.Text;

namespace Spectari.Util;

/// <summary>Routes console output to the sanitized diagnostic file and the
/// smaller operator event stream while preserving normal console output.</summary>
public static class ConsoleMirror
{
    public static event Action<string>? LineWritten;
    private const int OperatorHistoryLimit = 500;
    private static readonly Lock SinkGate = new();
    private static readonly Queue<string> OperatorHistory = new();
    private static StreamWriter? _logFile;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    internal static bool ShowViewerLinksInConsole { get; private set; }
    public static string? LogFilePath { get; private set; }

    public static void Install(bool withLogFile = true)
    {
        if (withLogFile)
        {
            try
            {
                string logDir = AppPaths.LogsDirectory;
                Directory.CreateDirectory(logDir);
                foreach (var old in new DirectoryInfo(logDir).GetFiles("*.log")
                             .OrderByDescending(f => f.CreationTimeUtc).Skip(9))
                    try { old.Delete(); } catch { }
                LogFilePath = Path.Combine(logDir, $"spectari-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _logFile = new StreamWriter(LogFilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
            }
            catch { _logFile = null; }
        }
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(new TeeWriter(_originalOut));
        Console.SetError(new TeeWriter(_originalError));
        WriteDiagnosticLine(DiagnosticLogEventText.AppStarted(AppVersion.Current));
        if (LogFilePath is not null)
            WriteTransientLine($"[log] writing to {LogFilePath}");
    }

    /// <summary>Writes an explicitly operator-facing event through both safe
    /// sinks without asking the text classifier to infer its audience.</summary>
    public static void WriteOperatorLine(string line)
    {
        string safeLine = BundleScrubber.Scrub(line);
        (_originalOut ?? Console.Out).WriteLine(safeLine);
        PublishSafeLine(safeLine, showToOperator: true);
    }

    internal static void WriteDiagnosticLine(string line)
    {
        string safeLine = BundleScrubber.Scrub(line);
        (_originalOut ?? Console.Out).WriteLine(safeLine);
        PublishSafeLine(safeLine, showToOperator: false);
    }

    /// <summary>Writes a component event after applying the shared audience
    /// classification policy.</summary>
    public static void WriteClassifiedLine(string line)
    {
        string safeLine = BundleScrubber.Scrub(line);
        (_originalOut ?? Console.Out).WriteLine(safeLine);
        PublishSafeLine(safeLine,
            LogEventClassifier.Classify(line) == LogAudience.OperatorAndDiagnostic);
    }

    /// <summary>Writes CLI-only output that must not enter either log sink.</summary>
    public static void WriteTransientLine(string line) =>
        (_originalOut ?? Console.Out).WriteLine(line);

    /// <summary>Writes CLI-only error output that must not enter either log sink.</summary>
    public static void WriteTransientErrorLine(string line) =>
        (_originalError ?? Console.Error).WriteLine(line);

    internal static void EnableViewerLinksInConsole() => ShowViewerLinksInConsole = true;

    public static string[] GetOperatorLines(int count)
    {
        lock (SinkGate)
            return OperatorHistory.Skip(Math.Max(0, OperatorHistory.Count - count)).ToArray();
    }

    private static void PublishLine(string line)
    {
        string safeLine = BundleScrubber.Scrub(line);
        PublishSafeLine(safeLine,
            LogEventClassifier.Classify(line) == LogAudience.OperatorAndDiagnostic);
    }

    private static void PublishSafeLine(string safeLine, bool showToOperator)
    {
        Action<string>? handler = null;
        lock (SinkGate)
        {
            try { _logFile?.WriteLine($"{DateTime.Now:HH:mm:ss} {safeLine}"); } catch { }
            if (showToOperator)
            {
                OperatorHistory.Enqueue(safeLine);
                while (OperatorHistory.Count > OperatorHistoryLimit)
                    OperatorHistory.Dequeue();
                handler = LineWritten;
            }
        }
        handler?.Invoke(safeLine);
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
                    PublishLine(line);
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
