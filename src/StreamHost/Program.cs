using System.Runtime.InteropServices;
using StreamHost.Capture;
using StreamHost.Ui;

namespace StreamHost;

internal static class Program
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [STAThread]
    private static int Main(string[] args)
    {
        timeBeginPeriod(1);

        // Double-click (no args) → the app window. CLI args → console mode.
        if (args.Length == 0)
        {
            ShowWindow(GetConsoleWindow(), 0 /* SW_HIDE */);
            Util.ConsoleMirror.Install();
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new MainForm());
            return 0;
        }

        return RunConsole(args);
    }

    private static int RunConsole(string[] args)
    {
        Util.ConsoleMirror.Install();
        var opts = Options.Parse(args);

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
            AudioPid = audioPid,
            Fps = opts.Fps,
            BitrateKbps = opts.BitrateKbps,
            Port = opts.Port,
            OutHeight = opts.OutHeight,
            Encoder = opts.Encoder,
            FragMs = opts.FragMs,
            NoCursor = opts.NoCursor,
            CompatibilityCapture = opts.CompatCapture,
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
            Thread.Sleep(800); // let the port release
            done.Reset();
            var retry = new StreamSession(config with { Encoder = "libx264" });
            retry.Stopped += _ => done.Set();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; retry.Stop(); };
            retry.Start();
            done.Wait();
        }
        return 0;
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
        public bool NoAudio = false;
        public bool CompatCapture = false;
        public int FragMs = 50; // batched fragments: Firefox presents ~25fps with per-frame appends; --frag-ms 0 = per-frame

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
                switch (args[i])
                {
                    case "--monitor": o.Monitor = int.Parse(Next()); break;
                    case "--window": o.Window = Next(); break;
                    case "--fps": o.Fps = int.Parse(Next()); break;
                    case "--bitrate": o.BitrateKbps = int.Parse(Next()); break;
                    case "--port": o.Port = int.Parse(Next()); break;
                    case "--height": o.OutHeight = int.Parse(Next()); break;
                    case "--width": Next(); break; // legacy no-op; height drives scaling, AR preserved
                    case "--encoder": o.Encoder = Next(); break;
                    case "--list-monitors": o.ListMonitors = true; break;
                    case "--list-windows": o.ListWindows = true; break;
                    case "--no-cursor": o.NoCursor = true; break;
                    case "--audio": o.Audio = Next(); break;
                    case "--no-audio": o.NoAudio = true; break;
                    case "--compat-capture": o.CompatCapture = true; break;
                    case "--frag-ms": o.FragMs = int.Parse(Next()); break;
                    case "--help":
                        Console.WriteLine("StreamHost                     -> app window");
                        Console.WriteLine("StreamHost [--monitor N | --window \"title/exe\"] [--fps 30|60] [--bitrate kbps] [--port N]");
                        Console.WriteLine("           [--height 1080] [--encoder auto|h264_nvenc|h264_amf|h264_qsv|libx264]");
                        Console.WriteLine("           [--audio \"name\"] [--no-audio] [--no-cursor] [--frag-ms N]");
                        Console.WriteLine("           [--list-monitors] [--list-windows]");
                        Environment.Exit(0);
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument {args[i]} (try --help)");
                        Environment.Exit(1);
                        break;
                }
            }
            return o;
        }
    }
}
