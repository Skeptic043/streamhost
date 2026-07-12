using System.Runtime.InteropServices;
using StreamHost.Capture;
using StreamHost.Ui;

namespace StreamHost;

internal static class Program
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [STAThread]
    private static int Main(string[] args)
    {
        timeBeginPeriod(1);

        // Elevated self-invocation from the "Fix access" button: configure the
        // port silently and report via exit code. No console, no UI.
        if (args.Length >= 2 && args[0] == "--setup-port" && int.TryParse(args[1], out int setupPort))
        {
            string? setupUser = null;
            bool allowLan = false; // presence of --setup-lan opts into the LAN ranges
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--setup-user" && i + 1 < args.Length) setupUser = args[++i];
                else if (args[i] == "--setup-lan") allowLan = true;
            }
            return Util.PortSetup.Run(setupPort, setupUser, allowLan);
        }

        // Double-click (no args) → the app window. CLI args → console mode.
        if (args.Length == 0)
        {
            // Already running? Surface that window instead of starting a second
            // process that would just collide on the port and confuse the user.
            if (!Util.SingleInstance.TryAcquire())
                return 0;

            Util.ConsoleMirror.Install();
            InstallCrashLogging();
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            // AppRunContext owns the control panel's lifetime: the app exits when
            // its last window closes, and a second launch brings the panel back
            // even if it was closed while a Watch window kept the process alive.
            System.Windows.Forms.Application.Run(new Ui.AppRunContext());
            return 0;
        }

        return RunConsole(args);
    }

    /// <summary>Final crash boundary for the GUI: log every unhandled exception to
    /// the on-disk log (via ConsoleMirror's tee of Console.Error), and on a fatal
    /// UI-thread exception point the user at that log file. Reuses the existing
    /// logger; adds no new sink. Wired before the message loop starts.</summary>
    private static void InstallCrashLogging()
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(
            System.Windows.Forms.UnhandledExceptionMode.CatchException);

        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            LogFatal("ui-thread", e.Exception, fatal: true);
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    $"StreamHost hit an unexpected error.\n\n{e.Exception.Message}\n\n" +
                    $"Details were written to the log file:\n{Util.ConsoleMirror.LogFilePath}",
                    "StreamHost", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("background", e.ExceptionObject as Exception, fatal: e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("task", e.Exception, fatal: false);
            e.SetObserved();
        };
    }

    /// <summary>Writes one crash record to the log: version, thread, whatever
    /// session state is cheaply reachable, and the exception. Never throws (a
    /// logger that crashes the crash handler helps nobody).</summary>
    private static void LogFatal(string origin, Exception? ex, bool fatal)
    {
        try
        {
            var w = Console.Error;
            var t = Thread.CurrentThread;
            w.WriteLine($"[crash] {(fatal ? "FATAL" : "unhandled")} on {origin} thread '{t.Name ?? "?"}' (#{Environment.CurrentManagedThreadId})");
            w.WriteLine($"[crash] StreamHost {MainForm.AppVersion()} on {Environment.OSVersion.VersionString}");
            w.WriteLine($"[crash] {Ui.AppRunContext.Current?.DescribeState() ?? "no window context"}");
            w.WriteLine($"[crash] {ex?.ToString() ?? "(no exception object)"}");
        }
        catch { }
    }

    /// <summary>The exe is WinExe (no console window on double-click), so console
    /// mode has to attach to the launching terminal — or create a console when
    /// started with args from Explorer. When stdout was redirected at launch
    /// (piping, > file) the handles already work and nothing is touched.</summary>
    private static void EnsureConsole()
    {
        IntPtr stdout = GetStdHandle(-11 /* STD_OUTPUT_HANDLE */);
        if (stdout != IntPtr.Zero && stdout != new IntPtr(-1)) return; // redirected: already usable
        if (!AttachConsole(-1)) AllocConsole();
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* no usable console; the log file still records everything */ }
    }

    private static int RunConsole(string[] args)
    {
        EnsureConsole();
        Util.ConsoleMirror.Install();

        Options opts;
        try { opts = Options.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Run StreamHost --help for usage.");
            return 2;
        }
        if (opts.ShowHelp) { PrintHelp(); return 0; }

        if (opts.ListWindows)
        {
            Console.WriteLine("Windows:");
            foreach (var w in WindowEnumerator.GetWindows())
                Console.WriteLine($"  [{w.ProcessName}] {w.Title}");
            return 0;
        }

        var monitors = MonitorEnumerator.GetMonitors();
        Console.WriteLine("Monitors:");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"  [{i}] {monitors[i].DeviceName} {monitors[i].Width}x{monitors[i].Height}{(monitors[i].IsPrimary ? " (primary)" : "")}");
        if (opts.ListMonitors) return 0;

        IntPtr monitorHandle = IntPtr.Zero, windowHandle = IntPtr.Zero;
        string sourceName;
        uint audioPid = 0;
        if (!string.IsNullOrEmpty(opts.Window))
        {
            var window = WindowEnumerator.FindByTitle(opts.Window);
            if (window is null)
            {
                Console.Error.WriteLine($"No visible window matching '{opts.Window}' (try --list-windows).");
                return 1;
            }
            windowHandle = window.Handle;
            sourceName = $"window '{window.Title}' [{window.ProcessName}]";
            audioPid = window.Pid; // default: audio follows the captured game
        }
        else
        {
            if (opts.Monitor < 0 || opts.Monitor >= monitors.Count)
            {
                Console.Error.WriteLine($"Monitor index {opts.Monitor} out of range.");
                return 1;
            }
            monitorHandle = monitors[opts.Monitor].Handle;
            sourceName = monitors[opts.Monitor].DeviceName;
        }

        if (opts.NoAudio) audioPid = 0;
        else if (!string.IsNullOrEmpty(opts.Audio))
        {
            var match = WindowEnumerator.FindByTitle(opts.Audio);
            if (match is null)
            {
                Console.Error.WriteLine($"No window/process matching '{opts.Audio}' for audio (try --list-windows).");
                return 1;
            }
            audioPid = match.Pid;
            Console.WriteLine($"[audio] source: {match.ProcessName} (pid {audioPid})");
        }
        if (audioPid == 0 && !opts.NoAudio && string.IsNullOrEmpty(opts.Window))
            Console.WriteLine("[audio] no audio source (monitor share) — use --audio \"game name\" to add game audio");

        var config = new SessionConfig
        {
            MonitorHandle = monitorHandle,
            WindowHandle = windowHandle,
            SourceName = sourceName,
            StreamName = opts.Name,
            AudioPid = audioPid,
            Fps = opts.Fps,
            BitrateKbps = opts.BitrateKbps,
            Port = opts.Port,
            OutHeight = opts.OutHeight,
            Encoder = opts.Encoder,
            FragMs = opts.FragMs,
            NoCursor = opts.NoCursor,
            CompatibilityCapture = opts.CompatCapture,
            ViewKey = opts.NoKey ? null : SessionConfig.NewViewKey(),
        };

        var session = new StreamSession(config);
        using var done = new ManualResetEventSlim(false);
        string? stopReason = null;
        session.Stopped += r => { stopReason = r; done.Set(); };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Stop(); };
        Console.WriteLine("[ready] Ctrl+C to stop");
        session.Start();
        done.Wait();

        // Same one-shot safety net as the GUI: a stalled/dead GPU encoder restarts
        // the stream on the CPU encoder instead of just exiting.
        bool encoderFailed = stopReason == "encoder-stall" ||
                             (stopReason?.StartsWith("encoder exited") ?? false);
        if (encoderFailed && opts.Encoder != "libx264")
        {
            Console.WriteLine("[encoder] GPU encoder produced no video — restarting with the CPU encoder (libx264)…");
            // Auto mode trusts a cached probe verdict; a live stall just disproved
            // it, so drop the cache to force a fresh probe next launch. Explicit
            // encoder mode never touched the cache, so there is nothing to clear.
            if (string.IsNullOrEmpty(opts.Encoder) || opts.Encoder == "auto")
                StreamHost.Encode.FfmpegEncoder.InvalidateProbeCache();
            // libx264 at 1440p and up may not sustain the same resolution/fps the
            // GPU was handling — warn instead of presenting fallback as recovery.
            if (session.OutputHeight >= 1440)
                Console.WriteLine($"[encoder] warning: libx264 (CPU) may not keep up at {session.OutputWidth}x{session.OutputHeight}@{opts.Fps} — lower the Preset if playback is choppy.");
            Thread.Sleep(800); // let the port release
            done.Reset();
            var retry = new StreamSession(config with { Encoder = "libx264" });
            retry.Stopped += r => { stopReason = r; done.Set(); };
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; retry.Stop(); };
            retry.Start();
            done.Wait();
        }

        // Exit 0 only for a clean, user-initiated stop; any pipeline failure
        // (encoder, capture, splitter, or server) exits nonzero so scripts and
        // callers can tell a broken run from a normal Ctrl+C.
        return stopReason is null or "stopped" ? 0 : 1;
    }

    private sealed record Options
    {
        public int Monitor = 0;
        public string Window = "";
        public int Fps = 60;
        public int BitrateKbps = 12000;
        public int Port = 8093;
        public int OutHeight = 0;
        public string Encoder = "auto";
        public bool ListMonitors = false;
        public bool ListWindows = false;
        public bool NoCursor = false;
        public string Audio = "";
        public string Name = "";
        public bool NoAudio = false;
        public bool NoKey = false;
        public bool CompatCapture = false;
        public bool ShowHelp = false;
        public int FragMs = 50; // batched fragments: Firefox presents ~25fps with per-frame appends; --frag-ms 0 = per-frame

        // Range guards with friendly one-line messages; RunConsole turns a thrown
        // ArgumentException into a nonzero exit. Kept out of the switch so the
        // parser stays a plain flag reader.
        private static int Positive(string flag, int n) =>
            n > 0 ? n : throw new ArgumentException($"{flag} must be greater than 0 (got {n})");
        private static int InRange(string flag, int n, int lo, int hi) =>
            n >= lo && n <= hi ? n : throw new ArgumentException($"{flag} must be between {lo} and {hi} (got {n})");

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
                int Int(string flag) => int.TryParse(Next(), out int n)
                    ? n : throw new ArgumentException($"{flag} expects a whole number, got '{args[i]}'");
                switch (args[i])
                {
                    case "--monitor": o.Monitor = Int("--monitor"); break;
                    case "--window": o.Window = Next(); break;
                    case "--fps": o.Fps = Positive("--fps", Int("--fps")); break;
                    case "--bitrate": o.BitrateKbps = Int("--bitrate"); break;
                    case "--port": o.Port = InRange("--port", Int("--port"), 1, 65535); break;
                    case "--height": o.OutHeight = Positive("--height", Int("--height")); break;
                    case "--width": Positive("--width", Int("--width")); break; // legacy no-op; height drives scaling, AR preserved
                    case "--encoder": o.Encoder = Next(); break;
                    case "--list-monitors": o.ListMonitors = true; break;
                    case "--list-windows": o.ListWindows = true; break;
                    case "--no-cursor": o.NoCursor = true; break;
                    case "--audio": o.Audio = Next(); break;
                    case "--name": o.Name = Next(); break;
                    case "--no-audio": o.NoAudio = true; break;
                    case "--no-key": o.NoKey = true; break;
                    case "--compat-capture": o.CompatCapture = true; break;
                    case "--frag-ms": o.FragMs = InRange("--frag-ms", Int("--frag-ms"), 0, 1000); break;
                    case "--help": o.ShowHelp = true; return o;
                    default: throw new ArgumentException($"unknown argument '{args[i]}' (try --help)");
                }
            }
            return o;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("StreamHost                     -> app window");
        Console.WriteLine("StreamHost [--monitor N | --window \"title/exe\"] [--fps 30|60] [--bitrate kbps, 0=auto] [--port N]");
        Console.WriteLine("           [--height 1080] [--encoder auto|h264_nvenc|h264_amf|h264_qsv|libx264]");
        Console.WriteLine("           [--name \"shown to viewers\"] [--audio \"app\"] [--no-audio] [--no-cursor] [--frag-ms N]");
        Console.WriteLine("           [--no-key (viewer links work without ?k=)] [--list-monitors] [--list-windows]");
        Console.WriteLine("StreamHost --setup-port N [--setup-user \"DOMAIN\\user\"] [--setup-lan]  -> reserve URL + firewall (admin)");
    }
}
