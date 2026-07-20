using System.Globalization;
using System.Runtime.InteropServices;
using Spectari.Capture;
using Spectari.Ui;

namespace Spectari;

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
        Util.AppPaths.MigrateLegacyData();

        if (args.Length == 0)
        {
            Util.ConsoleMirror.Install();
            // Migration ran before any sink existed; land its outcome in the log.
            if (Util.AppPaths.MigrationNote is { } note) Console.WriteLine(note);
        }

        // WinForms needs Windows culture data for input-language messages, while
        // Spectari's own numeric, version, and command formatting stays invariant.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        timeBeginPeriod(1);

        // Elevated self-invocation from the "Open port" button: configure the
        // port silently and report via exit code. No console, no UI.
        if (args.Length >= 2 && args[0] == "--setup-port" && int.TryParse(args[1], out int setupPort))
        {
            string? setupUser = null;
            bool allowLan = false; // presence of --setup-lan opts into the LAN ranges
            bool confirm = false;  // --setup-confirm: prompt on the console before
                                   // replacing a foreign reservation (setup.bat path)
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--setup-user" && i + 1 < args.Length) setupUser = args[++i];
                else if (args[i] == "--setup-lan") allowLan = true;
                else if (args[i] == "--setup-confirm") confirm = true;
            }
            // setup.bat runs elevated in a visible console and passes --setup-confirm
            // so a reservation owned by someone else isn't silently destroyed. The
            // "Open port" button does its own owner check on the UI thread before
            // elevating, so its silent relaunch never passes the flag.
            if (confirm)
            {
                EnsureConsole();
                if (!ConfirmForeignReservation(setupPort))
                    return 4; // refused / couldn't confirm - nothing changed
            }
            return Util.PortSetup.Run(setupPort, setupUser, allowLan);
        }

        // Double-click (no args) → the app window. CLI args → console mode.
        if (args.Length == 0)
        {
            // Already running? Surface that window instead of starting a second
            // process that would just collide on the port and confuse the user.
            Console.WriteLine("[boot] single-instance check start");
            if (!Util.SingleInstance.TryAcquire())
            {
                Console.WriteLine("[boot] single-instance check found an existing instance");
                return 0;
            }

            Console.WriteLine("[boot] single-instance check complete");
            InstallCrashLogging();
            Console.WriteLine("[boot] crash handlers installed");
            Encode.FfmpegEncoder.WarmBuildInfo();
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
            // Stop the live session BEFORE the modal dialog blocks: a broken app must
            // not keep streaming to viewers while this waits for the OK click.
            // ExitAfterFatal calls the same stop again afterwards; it is idempotent
            // (guarded, and StreamSession.Stop just re-cancels + re-joins a dead thread).
            try { Ui.AppRunContext.Current?.StopSessionForShutdown(); } catch { }
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Spectari hit an unexpected error.\n\n{e.Exception.Message}\n\n" +
                    $"Details were written to the log file:\n{Util.ConsoleMirror.LogFilePath}",
                    "Spectari", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
            catch { }
            ExitAfterFatal();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogFatal("background", e.ExceptionObject as Exception, fatal: e.IsTerminating);
            ExitAfterFatal();
        };

        // Background task faults stay LOG-ONLY: they don't resume a message loop, so
        // there's no half-broken app to escape, and killing the process over one
        // would be a regression.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("task", e.Exception, fatal: false);
            e.SetObserved();
        };
    }

    /// <summary>Fatal-exception exit: don't resume the message
    /// loop into a half-broken app. Stop any live session gracefully - bounded, so a
    /// wedged session can't hang shutdown; ChildJob kills ffmpeg with the process
    /// regardless - then exit nonzero. The UI-thread handler already stops the session
    /// before its dialog, so this stop is often a harmless no-op; the background
    /// (dialog-less) path relies on this one. Never throws.</summary>
    private static void ExitAfterFatal()
    {
        try { Ui.AppRunContext.Current?.StopSessionForShutdown(); } catch { }
        Environment.Exit(1); // nonzero: a runtime failure, same code the console path uses for one
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
            w.WriteLine($"[crash] Spectari {Util.AppVersion.Current} on {Environment.OSVersion.VersionString}");
            w.WriteLine($"[crash] {Ui.AppRunContext.Current?.DescribeState() ?? "no window context"}");
            w.WriteLine($"[crash] {ex?.ToString() ?? "(no exception object)"}");
        }
        catch { }
    }

    /// <summary>The exe is WinExe (no console window on double-click), so console
    /// mode has to attach to the launching terminal - or create a console when
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

    /// <summary>Interactive (setup.bat) guard: refuse to replace a URL reservation
    /// owned by another account without a typed yes. The "Open port" button makes
    /// this same check before it elevates; the bat relies on this instead. Fails
    /// CLOSED - if the owner can't be read, or there's no console to read a reply
    /// from, it keeps the existing reservation rather than destroying a foreign
    /// one.</summary>
    private static bool ConfirmForeignReservation(int port)
    {
        // EnsureConsole rebinds stdout/stderr; also rebind stdin so ReadLine can
        // pick up the elevated console the bat handed us.
        try { Console.SetIn(new StreamReader(Console.OpenStandardInput())); } catch { }

        string me = $"{Environment.UserDomainName}\\{Environment.UserName}";
        HostReservationReview reservation = HostAccessService.ReviewReservation(port, me);

        // Nothing reserved, or already ours: nothing to confirm.
        if (reservation.Status == HostReservationStatus.AvailableOrOwned)
            return true;

        // Reserved but unidentifiable: don't gamble on a foreign reservation.
        if (reservation.Status == HostReservationStatus.UnknownOwner)
        {
            Console.WriteLine();
            Console.WriteLine($"  Port {port}'s URL reservation is held by an account Spectari could not read.");
            Console.WriteLine("  Not replacing it. Pick a different port instead.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"  Port {port} is already reserved by another account:");
        Console.WriteLine($"    {reservation.Owner}");
        Console.WriteLine("  Replacing it may break the app that created it.");
        Console.Write("  Replace it with Spectari's reservation? [y/N] ");
        string? answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            return true;

        // A no, or no readable input at all: keep what's there.
        Console.WriteLine("  Keeping the existing reservation. Pick a different port instead.");
        return false;
    }

    private static int RunConsole(string[] args)
    {
        EnsureConsole();
        Util.ConsoleMirror.Install();
        Util.ConsoleMirror.EnableViewerLinksInConsole();
        // Migration ran before any sink existed; land its outcome in the log.
        if (Util.AppPaths.MigrationNote is { } note) Console.WriteLine(note);

        Options opts;
        try { opts = Options.Parse(args); }
        catch (ArgumentException ex)
        {
            Util.ConsoleMirror.WriteTransientErrorLine($"Error: {ex.Message}");
            Util.ConsoleMirror.WriteTransientErrorLine("Run Spectari --help for usage.");
            return 2;
        }
        if (opts.ShowHelp) { PrintHelp(); return 0; }

        if (opts.ListWindows)
        {
            Console.WriteLine("Windows:");
            foreach (var w in WindowEnumerator.GetWindows())
                Util.ConsoleMirror.WriteTransientLine($"  [{w.ProcessName}] {w.Title}");
            return 0;
        }

        var monitors = MonitorEnumerator.GetMonitors();
        Console.WriteLine("Monitors:");
        for (int i = 0; i < monitors.Count; i++)
            Util.ConsoleMirror.WriteTransientLine($"  [{i}] {monitors[i].DeviceName} {monitors[i].Width}x{monitors[i].Height}{(monitors[i].IsPrimary ? " (primary)" : "")}");
        if (opts.ListMonitors) return 0;

        IntPtr monitorHandle = IntPtr.Zero, windowHandle = IntPtr.Zero;
        string sourceName;
        uint audioPid = 0;
        if (!string.IsNullOrEmpty(opts.Window))
        {
            var window = WindowEnumerator.FindByTitle(opts.Window);
            if (window is null)
            {
                Util.ConsoleMirror.WriteTransientErrorLine($"No visible window matching '{opts.Window}' (try --list-windows).");
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
                Util.ConsoleMirror.WriteTransientErrorLine($"No window/process matching '{opts.Audio}' for audio (try --list-windows).");
                return 1;
            }
            audioPid = match.Pid;
            Console.WriteLine($"[audio] source: {match.ProcessName} (pid {audioPid})");
        }
        if (audioPid == 0 && !opts.NoAudio && string.IsNullOrEmpty(opts.Window))
            Console.WriteLine("[audio] no audio source (monitor share); use --audio \"game name\" to add game audio");

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
            MaxViewers = opts.MaxViewers,
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

        CpuRecoveryPlan? recovery = StreamController.PlanCpuRecovery(
            stopReason,
            config,
            session.OutputWidth,
            session.OutputHeight,
            recoveryAlreadyUsed: false,
            userRequested: false);
        if (recovery is not null)
        {
            Console.WriteLine(recovery.ConsoleMessage);
            if (recovery.InvalidateAutoProbe)
                Spectari.Encode.FfmpegEncoder.InvalidateProbeCache();
            if (recovery.OutputHeight >= 1440)
                Console.WriteLine(recovery.ConsoleCapacityWarning);
            Thread.Sleep(800); // let the port release
            done.Reset();
            var retry = new StreamSession(recovery.FallbackConfig);
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
        public int MaxViewers = 24; // 0 = unlimited
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
        private static int NonNegative(string flag, int n) =>
            n >= 0 ? n : throw new ArgumentException($"{flag} must be 0 or greater, 0 = unlimited (got {n})");
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
                    case "--max-viewers": o.MaxViewers = NonNegative("--max-viewers", Int("--max-viewers")); break; // 0 = unlimited
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
        Console.WriteLine("Spectari                     -> app window");
        Console.WriteLine("Spectari [--monitor N | --window \"title/exe\"] [--fps 30|60] [--bitrate kbps, 0=auto] [--port N]");
        Console.WriteLine("           [--height 1080] [--encoder auto|h264_nvenc|h264_amf|h264_qsv|libx264] [--max-viewers N, 0=unlimited]");
        Console.WriteLine("           [--name \"shown to viewers\"] [--audio \"app\"] [--no-audio] [--no-cursor] [--frag-ms N]");
        Console.WriteLine("           [--no-key (viewer links work without ?k=)] [--list-monitors] [--list-windows]");
        Console.WriteLine("Spectari --setup-port N [--setup-user \"DOMAIN\\user\"] [--setup-lan] [--setup-confirm]  -> reserve URL + firewall (admin)");
    }
}
