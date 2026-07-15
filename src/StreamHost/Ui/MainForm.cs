using System.Runtime.InteropServices;
using System.Text.Json;
using StreamHost.Capture;
using StreamHost.Server;
using StreamHost.Util;

namespace StreamHost.Ui;

/// <summary>
/// The app window: pick a source, pick a preset, Start, copy the link.
/// Dark themed, minimizes to tray, remembers settings.
/// </summary>
public sealed class MainForm : Form
{
    // ---- palette ----------------------------------------------------------
    // Softened: lighter surfaces, lower-contrast text, muted status colors.
    // Rule: NEVER disable a control — WinForms paints disabled text in fixed
    // gray that's unreadable on dark backgrounds. Radios/logic decide what's
    // USED; everything stays clickable and readable.
    // One deliberate exception (v0.15, user request): while LIVE, the pickers
    // that only take effect through a restart are disabled — the dimming is
    // the point, it funnels changes through the Switch source button.
    private static readonly Color Bg = Color.FromArgb(31, 33, 39);
    private static readonly Color Card = Color.FromArgb(42, 45, 53);
    private static readonly Color Border = Color.FromArgb(60, 65, 74);
    private static readonly Color Fg = Color.FromArgb(212, 216, 222);
    private static readonly Color Dim = Color.FromArgb(150, 156, 165);
    private static readonly Color AccentDark = Color.FromArgb(48, 92, 146);
    private static readonly Color Green = Color.FromArgb(112, 186, 130);
    private static readonly Color Red = Color.FromArgb(210, 115, 115);

    // Presets are the TARGET output (resolution + fps); the bitrate lives in its
    // own Low/Medium/High dropdown whose numbers repopulate from the preset and
    // the selected source, so the streamer always sees the real Mbps before
    // starting. Height 0 = Native: the label gets rewritten with the selected
    // source's actual size (UpdateNativePresetLabel).
    private sealed record Preset(string Label, int Height, int Fps)
    {
        public override string ToString() => Label;
    }

    private static readonly Preset[] Presets =
    [
        new("720p · 30 fps", 720, 30),
        new("720p · 60 fps", 720, 60),
        new("1080p · 30 fps", 1080, 30),
        new("1080p · 60 fps", 1080, 60),
        new("1440p · 30 fps", 1440, 30),
        new("1440p · 60 fps", 1440, 60),
        new("Native · 30 fps", 0, 30),
        new("Native · 60 fps", 0, 60),
    ];

    private sealed record BitrateChoice(string Label, int Kbps, string Tier)
    {
        public override string ToString() => Label;
    }

    private sealed record EncoderChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private static readonly EncoderChoice[] Encoders =
    [
        new("Auto (recommended)", "auto"),
        new("NVIDIA (NVENC)", "h264_nvenc"),
        new("AMD (AMF)", "h264_amf"),
        new("Intel (QSV)", "h264_qsv"),
        new("CPU (x264)", "libx264"),
    ];

    private static readonly int DefaultPresetIndex =
        Array.FindIndex(Presets, p => p.Height == 1080 && p.Fps == 60);

    private sealed class AppSettings
    {
        public string SourceKind { get; set; } = "window";
        public string WindowProcess { get; set; } = "";
        public int MonitorIndex { get; set; }
        // Presets are stored by height+fps, not array index — adding a preset
        // used to silently shift everyone's saved choice.
        public int PresetHeight { get; set; } = 1080;
        public int PresetFps { get; set; } = 60;
        public string BitrateTier { get; set; } = "med"; // "low" | "med" | "high"
        public string AudioSource { get; set; } = "window"; // "none" | "window" | process name
        public int Port { get; set; } = 8093;
        public string StreamName { get; set; } = ""; // shown to viewers; empty = machine name
        public string Encoder { get; set; } = "auto";
        // Set only when a Fix access with "Allow LAN" checked actually succeeded,
        // so the app knows LAN addresses are reachable (not merely requested).
        public bool AllowLan { get; set; }
        // The exact port that Fix access last configured. LAN is advertised only
        // when AllowLan is set AND the port in play matches this, so editing the
        // port after a successful fix can't advertise LAN on a port never opened.
        // A missing value deserializes to 0, which matches no real port, so LAN
        // stays off until the next successful Fix access on that port.
        public int AllowLanPort { get; set; }
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "settings.json");

    private readonly RadioButton _rbWindow = new() { Text = "Game / window", Checked = true, AutoSize = true };
    private readonly RadioButton _rbMonitor = new() { Text = "Monitor", AutoSize = true };
    private readonly ComboBox _windowCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _bitrateCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _encoderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _nameInput = new() { Width = 150, MaxLength = 32, BorderStyle = BorderStyle.FixedSingle, Text = Environment.MachineName };
    private readonly Button _watchButton = new() { Text = "Watch streams", Width = 118, Height = 38 };
    // 26px: at 24 the label's descenders (p/y/g) clipped against the border.
    private readonly Button _bundleButton = new() { Text = "Copy log", Width = 82, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button _openLogsButton = new() { Text = "Open logs", Width = 82, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button _fixPortButton = new() { Text = "Fix access (open port)", Width = 142, Height = 26, Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    // Short label (the 760px window's status row is crowded); full meaning is in
    // the tooltip. Default off: Tailscale-only is the secure default. Shares the
    // Fix access button's visibility.
    private readonly CheckBox _allowLanCheck = new() { Text = "Allow LAN viewers", AutoSize = true, Checked = false, Visible = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 5, 8, 0) };
    private readonly ToolTip _toolTip = new();
    private readonly NumericUpDown _portInput = new() { Minimum = 1024, Maximum = 65535, Value = 8093, Width = 80 };
    private readonly Button _startButton = new() { Text = "▶  Start streaming", Width = 160, Height = 38 };
    private readonly Button _copyButton = new() { Text = "Copy link", Width = 92, Height = 38 };
    private readonly Button _switchButton = new() { Text = "Switch source", Width = 112, Height = 38 };
    private readonly TextBox _linkBox = new() { ReadOnly = true, Width = 260, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _statusLabel = new() { Text = "Not streaming.", AutoSize = true };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.None,
    };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    private List<WindowDescription> _windows = [];
    private List<MonitorDescription> _monitors = [];
    private StreamSession? _session;
    private WatchForm? _watchForm;
    private int _livePort; // pinned while streaming so link/copy ignore edits to the port box
    private bool _retriedCpu;       // guards the one-shot GPU→CPU encoder fallback
    private bool _pendingCpuRetry;  // a fallback restart is scheduled; cancelled if the user stops
    private System.Windows.Forms.Timer? _cpuRetryTimer; // the 250 ms delay before the CPU restart; stored so close can dispose it
    // Bumped on every session teardown and on a cancelled retry. The CPU-retry
    // timer captures it when armed and re-checks it when it fires: a generation,
    // not a bool, so a tick queued behind a stop/start cycle can't act on the new
    // one (AppRunContext keeps the process alive for an open Watch window, so a
    // stale tick could otherwise start an invisible session on a closed form).
    private int _lifecycleGen;
    private bool _stopping;               // RequestStop sent; waiting for the session's Stopped event
    private SessionConfig? _lastConfig;   // last launched config, reused for the fix-port restart
    private SessionConfig? _pendingSwitch; // queued source switch, launched when Stopped fires
    private string _savedBitrateTier = "med"; // from settings, applied on the first populate
    private bool _refreshingSourceOptions; // suppresses preset events caused by Native relabeling
    // Persisted "LAN access was actually applied" flag: set only on a successful
    // Fix access with Allow LAN checked, never on a mere checkbox toggle. Gates
    // whether LAN addresses are treated as reachable for links and status.
    private bool _allowLanApplied;
    // The port Fix access last configured. LAN gating is per-port (LanAppliedForPort):
    // _allowLanApplied alone let an edit to the port field advertise LAN on a port
    // that was never opened, since the helper had configured a different live port.
    private int _allowLanPort;

    // The viewer key for the NEXT stream, minted up front so a link copied while
    // idle already carries ?k=. Reused when the stream starts and rotated only
    // after a run that went live stops, so an early-opened link auto-connects
    // even for plain-LAN viewers (who can't learn a rotated key from /api/stats).
    // Stable across idle rebinds (port/name edits) so already-copied links stay
    // valid; a failed start keeps it too, since no stream ever served it.
    private string _pendingKey = SessionConfig.NewViewKey();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // A picked window can close before the stream launches (right after the Switch
    // dialog's OK, or the main selection went stale). Validate the handle at the
    // start boundary so the session doesn't hand a dead HWND to the capture backend.
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private const int WmSetRedraw = 0x000B;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;

    // The holding page: served while the app is open but no stream is running,
    // so links opened early (or left over from a previous stream) show
    // "not streaming yet" and connect themselves once a stream starts.
    private WebServer? _idleServer;
    private CancellationTokenSource? _idleCts;
    // When the port is momentarily taken (another app, a just-closed session),
    // keep retrying the holding page instead of giving up for the whole run.
    private readonly System.Windows.Forms.Timer _idleRetryTimer = new() { Interval = 15000 };
    private bool _idleBindFailed;

    // Held so it can be unsubscribed on close: ConsoleMirror.LineWritten is a
    // STATIC event, and AppRunContext recreates this form, so an anonymous
    // lambda would root every dead form forever and fan each log line out to all
    // of them.
    private Action<string>? _logHandler;
    private bool _fixingPort; // guards the elevated Fix-access helper against re-entry

    public MainForm()
    {
        Text = "StreamHost";
        MinimumSize = new Size(680, 600);
        Size = new Size(760, 690);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = SystemIcons.Application; }

        // Audio lives with the source pickers: what you share and what it sounds
        // like are one decision. Name/port are plumbing, off to their own box.
        var sourceGroup = MakeGroup("What to share", 140);
        var sourceGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceGrid.Controls.Add(_rbWindow, 0, 0);
        sourceGrid.Controls.Add(_windowCombo, 1, 0);
        sourceGrid.Controls.Add(_rbMonitor, 0, 1);
        sourceGrid.Controls.Add(_monitorCombo, 1, 1);
        sourceGrid.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 2);
        sourceGrid.Controls.Add(_audioCombo, 1, 2);
        _audioCombo.Width = 330;
        sourceGroup.Controls.Add(sourceGrid);

        var optionsRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 132, ColumnCount = 2, BackColor = Bg };
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var qualityGroup = MakeGroup("Quality", 0);
        qualityGroup.Dock = DockStyle.Fill;
        qualityGroup.Margin = new Padding(0, 0, 3, 0);
        var qualityGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        qualityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        qualityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qualityGrid.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 0);
        qualityGrid.Controls.Add(_presetCombo, 1, 0);
        qualityGrid.Controls.Add(new Label { Text = "Bitrate:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 1);
        qualityGrid.Controls.Add(_bitrateCombo, 1, 1);
        qualityGrid.Controls.Add(new Label { Text = "Encoder:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 2);
        qualityGrid.Controls.Add(_encoderCombo, 1, 2);
        _presetCombo.Width = 250; // the Native entry carries the source resolution
        _bitrateCombo.Width = 190;
        qualityGroup.Controls.Add(qualityGrid);

        var miscGroup = MakeGroup("Misc", 0);
        miscGroup.Dock = DockStyle.Fill;
        miscGroup.Margin = new Padding(3, 0, 0, 0);
        var miscGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        miscGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        miscGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        miscGrid.Controls.Add(new Label { Text = "Name:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 0);
        miscGrid.Controls.Add(_nameInput, 1, 0);
        miscGrid.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 1);
        miscGrid.Controls.Add(_portInput, 1, 1);
        _nameInput.Margin = new Padding(3, 5, 3, 0);
        miscGroup.Controls.Add(miscGrid);

        optionsRow.Controls.Add(qualityGroup, 0, 0);
        optionsRow.Controls.Add(miscGroup, 1, 0);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8, 5, 0, 0), BackColor = Bg };
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_switchButton);
        actionPanel.Controls.Add(_copyButton);
        actionPanel.Controls.Add(_watchButton);
        actionPanel.Controls.Add(_linkBox);
        _linkBox.Margin = new Padding(10, 9, 0, 0);
        _linkBox.Width = 180;

        // TableLayout keeps the right-side buttons visible regardless of window
        // size / DPI scaling (manual X-positions drifted off the edge).
        // Height/bottom padding leave room for the status text's descenders and a
        // couple px of gap above the log box (the amber LIVE line sat flush to it).
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, BackColor = Bg, ColumnCount = 4, Padding = new Padding(12, 4, 8, 2) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // [Allow LAN][Fix access] ride together in a right-anchored strip so the
        // extra checkbox doesn't cost a statusPanel column or shove Copy log.
        var fixAccessGroup = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0), Anchor = AnchorStyles.Right, BackColor = Bg,
        };
        fixAccessGroup.Controls.Add(_allowLanCheck);
        fixAccessGroup.Controls.Add(_fixPortButton);
        _toolTip.SetToolTip(_allowLanCheck,
            "Also allow devices on your local network, not just Tailscale, to reach this stream");
        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(fixAccessGroup, 1, 0);
        statusPanel.Controls.Add(_openLogsButton, 2, 0);
        statusPanel.Controls.Add(_bundleButton, 3, 0);
        _toolTip.SetToolTip(_openLogsButton, "Open the folder containing the log file");
        _statusLabel.Anchor = AnchorStyles.Left;
        _fixPortButton.Anchor = AnchorStyles.Right;
        _openLogsButton.Anchor = AnchorStyles.Right;
        _bundleButton.Anchor = AnchorStyles.Right;
        _statusLabel.ForeColor = Dim;

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8), BackColor = Bg };
        logPanel.Controls.Add(_logBox);

        Controls.Add(logPanel);
        Controls.Add(statusPanel);
        Controls.Add(actionPanel);
        Controls.Add(optionsRow);
        Controls.Add(sourceGroup);

        _presetCombo.Items.AddRange(Presets);
        _presetCombo.SelectedIndex = DefaultPresetIndex;
        PopulateEncoderChoices();

        ApplyDarkTheme(this);
        _startButton.BackColor = AccentDark;
        _logBox.BackColor = Color.FromArgb(16, 18, 21);
        _logBox.ForeColor = Color.Gainsboro;
        _linkBox.BackColor = Card;
        _linkBox.ForeColor = Dim;

        // Nothing gets disabled — the selected radio decides which combo is USED.
        _windowCombo.DropDown += (_, _) => PopulateWindows();   // fresh list every open
        _monitorCombo.DropDown += (_, _) => PopulateMonitors(); // monitors change too (dock/undock)
        _rbWindow.CheckedChanged += (_, _) => { UpdateAudioModeLabel(); RefreshSourceOptions(); };
        _windowCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _monitorCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _presetCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        // The bitrate combo is the single source of truth for the chosen tier:
        // remember every user pick so it survives repopulates (source/preset
        // changes re-fire PopulateBitrateOptions) and app restarts. Guarded so the
        // transient -1 during an Items.Clear repopulate can't wipe the saved tier.
        _bitrateCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_bitrateCombo.SelectedItem is BitrateChoice bc) _savedBitrateTier = bc.Tier;
        };
        _startButton.Click += (_, _) =>
        {
            // Clicking during a scheduled CPU retry cancels the retry.
            // Cancelling a scheduled CPU retry: the GPU run got far enough to
            // schedule a fallback, so treat its key as used and rotate it.
            if (_pendingCpuRetry) { CancelCpuRetry(); OnSessionStopped(null, wentLive: true); return; }
            if (_session is null) StartStream(); else StopStream();
        };
        _switchButton.Click += (_, _) => ShowSwitchDialog();
        _copyButton.Click += (_, _) => CopyLink(BuildUrl(""));
        _watchButton.Click += (_, _) => OpenWatchWindow();
        _bundleButton.Click += (_, _) => CopySupportBundle();
        _openLogsButton.Click += (_, _) => OpenLogsFolder();
        _fixPortButton.Click += (_, _) => FixPortAccess();
        _portInput.ValueChanged += (_, _) => { UpdateLinkBox(); RestartIdleServer(); };
        _nameInput.Leave += (_, _) => RestartIdleServer(); // idle stats carry the name
        _statsTimer.Tick += (_, _) => UpdateStatus();
        _idleRetryTimer.Tick += (_, _) => StartIdleServer();

        _logHandler = line =>
        {
            if (IsDisposed) return;
            try { BeginInvoke(() => AppendLog(line)); } catch { }
        };
        ConsoleMirror.LineWritten += _logHandler;

        // Closing the panel stops YOUR stream but leaves an open Watch window
        // alive so you can keep viewing. AppRunContext owns app exit (it ends
        // when the last user window closes) and can bring this panel back on a
        // second launch, so there is no Application.Exit wiring here.
        FormClosing += (_, _) =>
        {
            // Drop the static-event subscription first so this recreated-then-
            // closed form doesn't stay rooted receiving log lines forever.
            if (_logHandler is not null) ConsoleMirror.LineWritten -= _logHandler;
            SaveSettings();
            _statsTimer.Stop();
            // Detach the session before stopping it: Stop() joins the session
            // thread, whose Stopped callback posts back here via BeginInvoke. With
            // _session nulled, that callback's ReferenceEquals(_session, session)
            // guard short-circuits, so it can't re-enter the timers we dispose below.
            var session = _session;
            _session = null;
            // Fail closed: if teardown wedges past the join timeout and a Watch
            // window keeps the process alive, the detached session would keep
            // streaming with no handle left to stop it. Take the whole app down
            // (the ChildJob job object reaps ffmpeg on process exit); this also
            // closes any open Watch window, a cost accepted over an invisible stream.
            if (session is not null && !session.Stop())
            {
                Console.WriteLine("[shutdown] stream teardown did not finish in time; closing the app to avoid an invisible stream.");
                Environment.Exit(1);
            }
            StopIdleServer();
            // Cancel a scheduled CPU retry so its timer can't fire on this closed
            // form and start an invisible session (a Watch window may keep the
            // process, and its message loop, alive after this form is gone).
            CancelCpuRetry();
            // These timers and the tooltip are not in a components container, so
            // nothing else disposes them.
            _statsTimer.Dispose();
            _idleRetryTimer.Dispose();
            _toolTip.Dispose();
        };

        PopulateSources();
        LoadSettings();
        UpdateLinkBox();
        RefreshSourceOptions();
        StartIdleServer();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    private GroupBox MakeGroup(string title, int height) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        Height = height,
        Padding = new Padding(10),
        ForeColor = Dim,
        BackColor = Bg,
    };

    private static void ApplyDarkTheme(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = Card;
                    b.ForeColor = Fg;
                    break;
                case ComboBox cb:
                    cb.BackColor = Card;
                    cb.ForeColor = Fg;
                    break;
                case NumericUpDown n:
                    n.BackColor = Card;
                    n.ForeColor = Fg;
                    n.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckBox or RadioButton:
                    c.ForeColor = Fg;
                    break;
                case TextBox t:
                    t.BackColor = Card;
                    t.ForeColor = Fg;
                    break;
            }
            if (c.HasChildren) ApplyDarkTheme(c);
        }
    }

    /// <summary>Bring the panel back to the user (un-minimize, raise, focus).
    /// AppRunContext calls this when a second launch broadcasts
    /// SingleInstance.ShowMessage. The TopMost flip is the standard way past
    /// Windows' foreground-lock, which otherwise just flashes the taskbar button
    /// instead of actually raising the window.</summary>
    internal void ShowAndActivate()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        bool wasTop = TopMost;
        TopMost = true;
        TopMost = wasTop;
        Activate();
    }

    private void PopulateSources()
    {
        PopulateWindows();
        PopulateMonitors();
    }

    private void PopulateMonitors()
    {
        int keep = _monitorCombo.SelectedIndex;
        _monitors = MonitorEnumerator.GetMonitors();
        _monitorCombo.BeginUpdate();
        _monitorCombo.Items.Clear();
        foreach (var m in _monitors)
            _monitorCombo.Items.Add($"{m.DeviceName}  {m.Width}x{m.Height}{(m.IsPrimary ? "  (primary)" : "")}");
        _monitorCombo.EndUpdate();
        if (_monitorCombo.Items.Count > 0)
            _monitorCombo.SelectedIndex = keep >= 0 && keep < _monitorCombo.Items.Count ? keep : 0;
    }

    /// <summary>"Captured window's audio" is a trap during a monitor share (it
    /// resolves to silence) — relabel it so people pick an actual app instead.</summary>
    private void UpdateAudioModeLabel()
    {
        if (_audioCombo.Items.Count < 2) return;
        int keep = _audioCombo.SelectedIndex;
        _audioCombo.Items[1] = _rbWindow.Checked
            ? "Captured window's audio"
            : "No audio (monitor share: pick an app below)";
        if (keep >= 0) _audioCombo.SelectedIndex = keep;
    }

    private void PopulateWindows()
    {
        string? keepProcess = _windowCombo.SelectedIndex >= 0 && _windowCombo.SelectedIndex < _windows.Count
            ? _windows[_windowCombo.SelectedIndex].ProcessName : null;
        string? keepAudio = SelectedAudioKey();

        uint ownPid = (uint)Environment.ProcessId;
        _windows = WindowEnumerator.GetWindows()
            .Where(w => w.Pid != ownPid)
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _windowCombo.BeginUpdate();
        _windowCombo.Items.Clear();
        foreach (var w in _windows)
            _windowCombo.Items.Add($"{w.ProcessName} - {Truncate(w.Title, 58)}");
        _windowCombo.EndUpdate();

        int restore = keepProcess is null ? -1
            : _windows.FindIndex(w => w.ProcessName.Equals(keepProcess, StringComparison.OrdinalIgnoreCase));
        if (_windowCombo.Items.Count > 0)
            _windowCombo.SelectedIndex = restore >= 0 ? restore : 0;

        // Audio picker: none / follow the captured window / any running app.
        _audioCombo.BeginUpdate();
        _audioCombo.Items.Clear();
        _audioCombo.Items.Add("No audio");
        _audioCombo.Items.Add("Captured window's audio");
        foreach (var w in _windows)
            _audioCombo.Items.Add($"{w.ProcessName} - {Truncate(w.Title, 40)}");
        _audioCombo.EndUpdate();
        SelectAudioByKey(keepAudio ?? "window");
        UpdateAudioModeLabel();
    }

    /// <summary>"none", "window", or a process name.</summary>
    private string SelectedAudioKey() => _audioCombo.SelectedIndex switch
    {
        0 => "none",
        1 => "window",
        > 1 when _audioCombo.SelectedIndex - 2 < _windows.Count => _windows[_audioCombo.SelectedIndex - 2].ProcessName,
        _ => "window",
    };

    private void SelectAudioByKey(string key)
    {
        if (key == "none") { _audioCombo.SelectedIndex = 0; return; }
        if (key == "window") { _audioCombo.SelectedIndex = 1; return; }
        int idx = _windows.FindIndex(w => w.ProcessName.Equals(key, StringComparison.OrdinalIgnoreCase));
        _audioCombo.SelectedIndex = idx >= 0 ? idx + 2 : 1;
    }

    private void StartStream()
    {
        // Reuse the pending key so links copied while idle (they carry ?k=this)
        // stay valid the moment the stream goes live. Rotated after a live stop.
        var config = BuildConfigFromUi((int)_portInput.Value, _pendingKey);
        if (config is null) return;

        if (config.Port != 8093)
            AppendLog("Note: setup.bat / Fix access configure one port at a time; other ports need their own run.");

        _retriedCpu = false;
        SaveSettings();
        LaunchSession(config);
    }

    /// <summary>Reads the whole UI into a SessionConfig, or null (with a log line)
    /// when no source is selected. Shared by Start, Switch source, and restarts.</summary>
    private SessionConfig? BuildConfigFromUi(int port, string? viewKey)
    {
        var preset = (Preset)_presetCombo.SelectedItem!;

        // Resolve the audio source: none / follow captured window / a specific app.
        // "Captured window's audio" during a monitor share resolves to no audio.
        uint audioPid = 0;
        string audioKey = SelectedAudioKey();
        if (audioKey == "window")
        {
            if (_rbWindow.Checked && _windowCombo.SelectedIndex >= 0)
                audioPid = _windows[_windowCombo.SelectedIndex].Pid;
        }
        else if (audioKey != "none")
        {
            int idx = _windows.FindIndex(w => w.ProcessName.Equals(audioKey, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) audioPid = _windows[idx].Pid;
            else AppendLog($"[audio] '{audioKey}' is not running; streaming without audio");
        }

        IntPtr windowHandle = IntPtr.Zero, monitorHandle = IntPtr.Zero;
        string sourceName;
        if (_rbWindow.Checked)
        {
            if (_windowCombo.SelectedIndex < 0) { AppendLog("No window selected."); return null; }
            var w = _windows[_windowCombo.SelectedIndex];
            if (!IsWindow(w.Handle))
            {
                AppendLog("The selected window no longer exists. Pick another source.");
                PopulateSources();
                return null;
            }
            windowHandle = w.Handle;
            sourceName = $"window '{w.Title}' [{w.ProcessName}]";
        }
        else
        {
            if (_monitorCombo.SelectedIndex < 0) { AppendLog("No monitor selected."); return null; }
            var m = _monitors[_monitorCombo.SelectedIndex];
            monitorHandle = m.Handle;
            sourceName = m.DeviceName;
        }

        return new SessionConfig
        {
            WindowHandle = windowHandle,
            MonitorHandle = monitorHandle,
            SourceName = sourceName,
            StreamName = _nameInput.Text.Trim(),
            AudioPid = audioPid,
            Fps = preset.Fps,
            BitrateKbps = (_bitrateCombo.SelectedItem as BitrateChoice)?.Kbps ?? 0, // 0 = session auto (Medium)
            OutHeight = preset.Height,
            Port = port,
            Encoder = ((EncoderChoice)_encoderCombo.SelectedItem!).Value,
            ViewKey = viewKey,
        };
    }


    /// <summary>Starts a session and wires its Stopped event, including the automatic
    /// CPU-encoder retry when a GPU encoder stalls. Returns false if it couldn't start.</summary>
    private bool LaunchSession(SessionConfig config)
    {
        StopIdleServer(); // hand the port to the session
        StreamSession session;
        try
        {
            session = new StreamSession(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start: {ex.Message}");
            _session = null;
            StartIdleServer();
            return false;
        }

        // Stopped fires only after the session thread finished real teardown
        // (server disposed, port released), so everything below can restart
        // immediately without racing the old session for the port.
        session.Stopped += reason =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_session, session)) return; // superseded by a newer session
                    _session = null;
                    _lifecycleGen++; // this teardown voids any CPU-retry tick still queued from an earlier one
                    bool userRequested = _stopping;
                    _stopping = false;

                    // A queued source switch or fix-port restart takes priority.
                    if (_pendingSwitch is { } next)
                    {
                        _pendingSwitch = null;
                        LaunchSession(next);
                        return;
                    }

                    // GPU encoder stalled or died → restart once on the CPU encoder.
                    bool encoderFailed = reason == "encoder-stall" || reason.StartsWith("encoder exited");
                    if (!userRequested && encoderFailed && !_retriedCpu && config.Encoder != "libx264")
                    {
                        _retriedCpu = true;
                        _pendingCpuRetry = true;
                        AppendLog("GPU encoder produced no video; restarting with the CPU encoder (libx264)…");
                        // Watchdog-detected GPU stalls clear any prior positive verdict
                        // immediately. Keep this Auto-only call for unexpected ffmpeg
                        // exits, which reach fallback without passing through the watchdog.
                        if (string.IsNullOrEmpty(config.Encoder) || config.Encoder == "auto")
                            StreamHost.Encode.FfmpegEncoder.InvalidateProbeCache();
                        // libx264 at 1440p and up may not sustain the same resolution/fps
                        // the GPU handled — warn instead of calling fallback a recovery.
                        if (session.OutputHeight >= 1440)
                            AppendLog($"Warning: libx264 (CPU) may not keep up at {session.OutputWidth}x{session.OutputHeight}@{config.Fps}; lower the Preset if playback is choppy.");
                        var fallback = config with { Encoder = "libx264" };
                        int gen = _lifecycleGen; // the cycle this retry belongs to
                        _cpuRetryTimer?.Dispose(); // never expected here, but never leak one either
                        var timer = new System.Windows.Forms.Timer { Interval = 250 };
                        _cpuRetryTimer = timer;
                        timer.Tick += (_, _) =>
                        {
                            // Operate on the captured instance, never the shared field: a
                            // stale tick must not stop/dispose a newer timer that a later
                            // cycle has since placed in the field. Only null the field when
                            // it still references THIS timer.
                            timer.Stop();
                            timer.Dispose();
                            if (ReferenceEquals(_cpuRetryTimer, timer)) _cpuRetryTimer = null;
                            // A stale tick must do nothing: the form may have closed while a
                            // Watch window kept the loop alive, or a later stop/start cycle
                            // may have moved on. The generation catches the latter even when
                            // _pendingCpuRetry has been re-armed by a new cycle.
                            if (IsDisposed || gen != _lifecycleGen) return;
                            if (_pendingCpuRetry) { _pendingCpuRetry = false; LaunchSession(fallback); }
                        };
                        timer.Start();
                        return;
                    }
                    OnSessionStopped(userRequested ? null : reason, session.WentLive);
                });
            }
            catch { }
        };
        _session = session;
        _lastConfig = config;
        session.Start();

        _livePort = config.Port;
        _startButton.Text = "■  Stop";
        _startButton.BackColor = Color.FromArgb(104, 58, 58);
        SetLiveLock(true);
        _statsTimer.Start();
        UpdateLinkBox();
        return true;
    }

    /// <summary>Request a stop and wait for the session's Stopped event; the UI
    /// shows "Stopping…" until teardown really finished instead of pretending
    /// the stream is gone while the old session still owns the port.</summary>
    private void StopStream()
    {
        if (_session is null || _stopping) return;
        _pendingCpuRetry = false;
        _pendingSwitch = null;
        _stopping = true;
        _statsTimer.Stop();
        _startButton.Text = "…  Stopping";
        _statusLabel.Text = "Stopping…";
        _statusLabel.ForeColor = Color.Goldenrod;
        _session.RequestStop();
    }

    /// <summary>Cancel a scheduled GPU→CPU retry: clear the flag, stop and dispose
    /// the timer so a queued tick can't fire, and advance the lifecycle generation
    /// so a tick already dispatched no-ops. Safe to call when nothing is pending.</summary>
    private void CancelCpuRetry()
    {
        _pendingCpuRetry = false;
        _cpuRetryTimer?.Stop();
        _cpuRetryTimer?.Dispose();
        _cpuRetryTimer = null;
        _lifecycleGen++;
    }

    /// <summary>Guided switch: a small popup with just the source, preset, and
    /// audio pickers, prefilled from the current selections. OK writes the
    /// choices back to the main controls and goes through the normal switch
    /// (or plain start when idle), so both paths stay one code path.</summary>
    private void ShowSwitchDialog()
    {
        if (_stopping) return;

        // Enumerate fresh source lists for the popup WITHOUT touching the main
        // controls, so pressing Cancel leaves the main window's selection exactly
        // as it was. The main lists are refreshed only on OK, below.
        uint ownPid = (uint)Environment.ProcessId;
        var dlgWindows = WindowEnumerator.GetWindows()
            .Where(w => w.Pid != ownPid)
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dlgMonitors = MonitorEnumerator.GetMonitors();

        using var dlg = new Form
        {
            Text = "Switch source",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 244),
            BackColor = Bg,
            ForeColor = Fg,
        };
        dlg.HandleCreated += (_, _) => { int on = 1; _ = DwmSetWindowAttribute(dlg.Handle, 20, ref on, sizeof(int)); };

        var rbWin = new RadioButton { Text = "Game / window", AutoSize = true, Checked = _rbWindow.Checked };
        var rbMon = new RadioButton { Text = "Monitor", AutoSize = true, Checked = _rbMonitor.Checked };
        var winCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        var monCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        var presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, FlatStyle = FlatStyle.Flat };
        var bitrateCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, FlatStyle = FlatStyle.Flat };
        var audioCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };

        // Build the dialog combos off the dialog's own fresh lists (never the main
        // controls). Same item shapes as PopulateWindows/PopulateMonitors so the OK
        // path can translate picks back by HWND/DeviceName/key.
        foreach (var w in dlgWindows) winCombo.Items.Add($"{w.ProcessName} - {Truncate(w.Title, 58)}");
        foreach (var m in dlgMonitors) monCombo.Items.Add($"{m.DeviceName}  {m.Width}x{m.Height}{(m.IsPrimary ? "  (primary)" : "")}");
        foreach (object it in _presetCombo.Items) presetCombo.Items.Add(it);
        audioCombo.Items.Add("No audio");
        audioCombo.Items.Add(rbWin.Checked ? "Captured window's audio" : "No audio (monitor share: pick an app below)");
        foreach (var w in dlgWindows) audioCombo.Items.Add($"{w.ProcessName} - {Truncate(w.Title, 40)}");

        // Preselect the current capture source by exact identity. If it vanished
        // between enumerations, leave it unselected so the user must pick again.
        IntPtr? curWinHandle = _windowCombo.SelectedIndex >= 0 && _windowCombo.SelectedIndex < _windows.Count
            ? _windows[_windowCombo.SelectedIndex].Handle : null;
        winCombo.SelectedIndex = curWinHandle is null
            ? -1 : dlgWindows.FindIndex(w => w.Handle == curWinHandle.Value);
        string? curMonDevice = _monitorCombo.SelectedIndex >= 0 && _monitorCombo.SelectedIndex < _monitors.Count
            ? _monitors[_monitorCombo.SelectedIndex].DeviceName : null;
        monCombo.SelectedIndex = curMonDevice is null
            ? -1 : dlgMonitors.FindIndex(m => m.DeviceName.Equals(curMonDevice, StringComparison.OrdinalIgnoreCase));
        presetCombo.SelectedIndex = _presetCombo.SelectedIndex;
        string curAudioKey = SelectedAudioKey();
        audioCombo.SelectedIndex = curAudioKey switch
        {
            "none" => 0,
            "window" => 1,
            _ => dlgWindows.FindIndex(w => w.ProcessName.Equals(curAudioKey, StringComparison.OrdinalIgnoreCase)) is int ai && ai >= 0 ? ai + 2 : 1,
        };
        // Refresh the dialog's Native labels and bitrate choices from one immutable
        // enumerated size snapshot, matching the main controls.
        bool refreshingDlgSourceOptions = false;
        void RefreshDlgSourceOptions()
        {
            if (refreshingDlgSourceOptions) return;
            refreshingDlgSourceOptions = true;
            try
            {
                var sourceSize = SelectedSourceSize(rbWin.Checked, winCombo.SelectedIndex, monCombo.SelectedIndex, dlgWindows, dlgMonitors);
                for (int idx = 0; idx < Presets.Length && idx < presetCombo.Items.Count; idx++)
                {
                    if (Presets[idx].Height != 0) continue;
                    string label = ComputeNativeLabel(Presets[idx].Fps, sourceSize);
                    if (presetCombo.Items[idx] is Preset current && current.Label == label) continue;
                    int sel = presetCombo.SelectedIndex;
                    presetCombo.Items[idx] = Presets[idx] with { Label = label };
                    presetCombo.SelectedIndex = sel;
                }

                if (presetCombo.SelectedItem is not Preset p) return;
                string keep = (bitrateCombo.SelectedItem as BitrateChoice)?.Tier
                              ?? (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? "med";
                var choices = BuildBitrateChoices(p, sourceSize);
                bitrateCombo.BeginUpdate();
                bitrateCombo.Items.Clear();
                foreach (var c in choices) bitrateCombo.Items.Add(c);
                bitrateCombo.EndUpdate();
                int i = choices.FindIndex(c => c.Tier == keep);
                bitrateCombo.SelectedIndex = i >= 0 ? i : 1;
            }
            finally
            {
                refreshingDlgSourceOptions = false;
            }
        }
        rbWin.CheckedChanged += (_, _) =>
        {
            if (audioCombo.Items.Count >= 2)
                audioCombo.Items[1] = rbWin.Checked
                    ? "Captured window's audio"
                    : "No audio (monitor share: pick an app below)";
            RefreshDlgSourceOptions();
        };
        winCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        monCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        presetCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        RefreshDlgSourceOptions();

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(rbWin, 0, 0);
        grid.Controls.Add(winCombo, 1, 0);
        grid.Controls.Add(rbMon, 0, 1);
        grid.Controls.Add(monCombo, 1, 1);
        grid.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 2);
        grid.Controls.Add(presetCombo, 1, 2);
        grid.Controls.Add(new Label { Text = "Bitrate:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 3);
        grid.Controls.Add(bitrateCombo, 1, 3);
        grid.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 4);
        grid.Controls.Add(audioCombo, 1, 4);

        var ok = new Button { Text = _session is null ? "Start" : "Switch", Width = 96, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(buttons, 0, 5);
        dlg.Controls.Add(grid);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        ApplyDarkTheme(dlg);
        ok.BackColor = AccentDark;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // OK only. Refresh the main lists once (the single time this method mutates
        // them) so the picks resolve against a current enumeration, then VALIDATE
        // every specific pick BEFORE writing any main control: a source the dialog
        // captured but that has since vanished (window closed, monitor unplugged,
        // audio app exited) aborts the switch instead of silently falling through to
        // whatever the refreshed main list happens to select. All resolution reads
        // below happen before the first selected-value write, so an abort leaves the
        // running stream's effective configuration untouched.
        // Match the picked capture source by stable identity: HWND for a window and
        // device name for a monitor. Preset remains by index and audio by key.
        PopulateSources();

        // Window pick, resolved whether or not the window radio is active (so a later
        // switch back to it stays correct). Only an ACTIVE-but-unresolved pick aborts.
        int winTarget = -1;
        string? winProc = null;
        if (winCombo.SelectedIndex >= 0 && winCombo.SelectedIndex < dlgWindows.Count)
        {
            var pickedWindow = dlgWindows[winCombo.SelectedIndex];
            winProc = pickedWindow.ProcessName;
            winTarget = _windows.FindIndex(w => w.Handle == pickedWindow.Handle);
        }
        if (rbWin.Checked && winTarget < 0)
        {
            AppendLog($"Switch cancelled: '{winProc ?? "the picked window"}' is no longer available. Pick again.");
            return;
        }
        int monTarget = -1;
        if (monCombo.SelectedIndex >= 0 && monCombo.SelectedIndex < dlgMonitors.Count)
        {
            string deviceName = dlgMonitors[monCombo.SelectedIndex].DeviceName;
            monTarget = _monitors.FindIndex(m => m.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        }
        if (rbMon.Checked && monTarget < 0)
        {
            AppendLog("Switch cancelled: the picked monitor is no longer available. Pick again.");
            return;
        }
        string pickedAudioKey = audioCombo.SelectedIndex switch
        {
            0 => "none",
            1 => "window",
            > 1 when audioCombo.SelectedIndex - 2 < dlgWindows.Count => dlgWindows[audioCombo.SelectedIndex - 2].ProcessName,
            _ => "window",
        };
        if (pickedAudioKey is not ("none" or "window") &&
            _windows.FindIndex(w => w.ProcessName.Equals(pickedAudioKey, StringComparison.OrdinalIgnoreCase)) < 0)
        {
            AppendLog($"Switch cancelled: audio source '{pickedAudioKey}' is no longer available. Pick again.");
            return;
        }

        // All picks resolved: now apply them to the main controls.
        _rbWindow.Checked = rbWin.Checked;
        _rbMonitor.Checked = rbMon.Checked;
        if (winTarget >= 0) _windowCombo.SelectedIndex = winTarget;
        if (monTarget >= 0) _monitorCombo.SelectedIndex = monTarget;
        if (presetCombo.SelectedIndex >= 0) _presetCombo.SelectedIndex = presetCombo.SelectedIndex;
        SelectAudioByKey(pickedAudioKey);
        // Writing the source/preset back above repopulated the main bitrate combo;
        // now apply the tier the user picked in the dialog.
        if ((bitrateCombo.SelectedItem as BitrateChoice)?.Tier is { } tier)
        {
            int mi = -1;
            for (int j = 0; j < _bitrateCombo.Items.Count; j++)
                if ((_bitrateCombo.Items[j] as BitrateChoice)?.Tier == tier) { mi = j; break; }
            if (mi >= 0) _bitrateCombo.SelectedIndex = mi;
        }
        SwitchSource();
    }

    /// <summary>Restart with whatever the UI currently selects, keeping the port
    /// and viewer key, so viewer links survive and pages auto-reconnect after a
    /// short blip. Under the hood it is a clean stop + start.</summary>
    private void SwitchSource()
    {
        if (_session is null) { StartStream(); return; }
        if (_stopping) return;
        var config = BuildConfigFromUi(_livePort, _session.ViewKey);
        if (config is null) return;
        _retriedCpu = false;
        _pendingSwitch = config;
        _stopping = true;
        _statsTimer.Stop();
        _statusLabel.Text = "Switching source…";
        _statusLabel.ForeColor = Color.Goldenrod;
        AppendLog($"Switching to {config.SourceName}. Viewers reconnect automatically.");
        SaveSettings();
        _session.RequestStop();
    }

    private void OnSessionStopped(string? reason, bool wentLive)
    {
        _statsTimer.Stop();
        _session = null;
        _stopping = false;
        _startButton.Text = "▶  Start streaming";
        _startButton.BackColor = AccentDark;
        Text = "StreamHost";
        _livePort = 0;
        _fixPortButton.Visible = false;
        _allowLanCheck.Visible = false;
        SetLiveLock(false);
        // A run that actually served viewers rotates the viewer key, so links from
        // that run stop working (the per-run key model). A start that never went
        // live (bind failure, early capture error) keeps the pending key so an
        // already-copied idle link still works on the next attempt. Rotate before
        // the link box refreshes so it shows the new idle key.
        if (wentLive) _pendingKey = SessionConfig.NewViewKey();
        UpdateLinkBox();
        _statusLabel.Text = reason is null or "stopped" ? "Not streaming." : $"Stopped: {reason}";
        _statusLabel.ForeColor = reason is null or "stopped" ? Dim : Red;
        StartIdleServer(); // take the port back so open tabs see "not streaming yet"
    }

    /// <summary>Serve the holding page whenever the app is open but no stream is
    /// running: tabs opened early (or holding a link from a previous stream) get
    /// "not streaming yet" and connect themselves once a stream starts. The
    /// session and the idle server trade the port back and forth.</summary>
    private void StartIdleServer()
    {
        if (_session is not null || _idleServer is not null) { _idleRetryTimer.Stop(); return; }
        try
        {
            var server = new WebServer((int)_portInput.Value, null, null, _nameInput.Text.Trim());
            var cts = new CancellationTokenSource();
            _idleServer = server;
            _idleCts = cts;
            // Observe the run: a fault AFTER a successful bind used to be swallowed,
            // leaving _idleServer non-null so the guard above refused to rebuild and
            // the holding page stayed silently dead until restart. The continuation
            // resets it and kicks the retry path. Bind state is known now (the ctor
            // bound synchronously), so refresh the link to match localhost vs remote.
            _ = server.RunAsync(cts.Token).ContinueWith(
                t => OnIdleServerExited(server, cts.Token, t),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _idleRetryTimer.Stop();
            _idleBindFailed = false;
            UpdateLinkBox();
        }
        catch (Exception ex)
        {
            // Not fatal — usually another program owns the port right now. Log it
            // once (not every retry), then keep retrying quietly so the holding
            // page comes back on its own the moment the port frees up.
            if (!_idleBindFailed)
            {
                _idleBindFailed = true;
                AppendLog($"[http] holding page unavailable: {ex.Message}");
                AppendLog("[http] will keep trying to open the holding page until the port is free.");
            }
            _idleServer = null;
            _idleCts = null;
            _idleRetryTimer.Start();
        }
    }

    private void StopIdleServer()
    {
        _idleRetryTimer.Stop();
        _idleBindFailed = false;
        _idleCts?.Cancel();
        _idleCts = null;
        _idleServer?.Dispose();
        _idleServer = null;
    }

    /// <summary>Port or name changed while idle — rebind so the holding page follows.</summary>
    private void RestartIdleServer()
    {
        if (_session is not null || _stopping) return;
        StopIdleServer();
        StartIdleServer();
    }

    /// <summary>The idle holding page's accept loop ended. Deliberate teardown
    /// (StopIdleServer/RestartIdleServer cancels the token, or the instance was
    /// already replaced) is a no-op; a genuine fault after a good bind resets
    /// _idleServer and arms the retry timer so the existing path rebuilds it.</summary>
    private void OnIdleServerExited(WebServer server, CancellationToken token, Task run)
    {
        if (token.IsCancellationRequested) return; // we asked it to stop
        try
        {
            BeginInvoke(() =>
            {
                if (!ReferenceEquals(_idleServer, server)) return; // already replaced
                AppendLog($"[http] holding page stopped unexpectedly: {run.Exception?.GetBaseException().Message ?? "no error"}");
                _idleCts?.Cancel();
                _idleCts = null;
                _idleServer.Dispose();
                _idleServer = null;
                _idleRetryTimer.Start(); // the retry path rebuilds the holding page
            });
        }
        catch { /* form gone: nothing left to rebuild */ }
    }

    /// <summary>Refreshes all source-derived controls from one enumerated size
    /// snapshot so the Native label and bitrate class cannot disagree.</summary>
    private void RefreshSourceOptions()
    {
        if (_refreshingSourceOptions) return;
        _refreshingSourceOptions = true;
        try
        {
            var sourceSize = SelectedSourceSize(_rbWindow.Checked, _windowCombo.SelectedIndex, _monitorCombo.SelectedIndex, _windows, _monitors);
            UpdateNativePresetLabel(sourceSize);
            PopulateBitrateOptions(sourceSize);
        }
        finally
        {
            _refreshingSourceOptions = false;
        }
    }

    private void PopulateEncoderChoices()
    {
        var hardwareVendors = new HashSet<uint>();
        bool enumerationFailed = false;
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out Vortice.DXGI.IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                {
                    var description = adapter!.Description1;
                    uint vendorId = (uint)description.VendorId;
                    if ((description.Flags & Vortice.DXGI.AdapterFlags.Software) != 0 || vendorId == 0x1414)
                        continue;
                    hardwareVendors.Add(vendorId);
                }
            }
        }
        catch
        {
            enumerationFailed = true;
        }

        // Raw BGRA frames are piped to ffmpeg, so capture and encoder adapters
        // are independent. The picker only needs hardware from the encoder vendor.
        EncoderChoice[] choices = enumerationFailed
            ? Encoders
            : Encoders.Where(e => e.Value switch
            {
                "h264_nvenc" => hardwareVendors.Contains(0x10DE),
                "h264_amf" => hardwareVendors.Contains(0x1002),
                "h264_qsv" => hardwareVendors.Contains(0x8086),
                _ => true,
            }).ToArray();

        _encoderCombo.Items.AddRange(choices);
        _encoderCombo.SelectedIndex = 0;
        // A failed DXGI read must leave every manual choice available; the
        // encoder probe and watchdog remain the runtime guard.
        if (enumerationFailed)
            Console.WriteLine("[encoder] DXGI adapter enumeration failed; showing all encoder choices.");
    }

    /// <summary>Rewrites the Native preset entries with the selected source's real
    /// resolution, e.g. "Native · 60 fps (2560x1440)". The bitrate dropdown next
    /// to it carries the Mbps numbers.</summary>
    private void UpdateNativePresetLabel((int W, int H) sourceSize)
    {
        for (int idx = 0; idx < Presets.Length && idx < _presetCombo.Items.Count; idx++)
        {
            if (Presets[idx].Height != 0) continue;
            string label = ComputeNativeLabel(Presets[idx].Fps, sourceSize);
            if (_presetCombo.Items[idx] is Preset p && p.Label == label) continue;
            int sel = _presetCombo.SelectedIndex;
            _presetCombo.Items[idx] = Presets[idx] with { Label = label };
            _presetCombo.SelectedIndex = sel;
        }
    }

    private static string ComputeNativeLabel(int fps, (int W, int H) sourceSize)
    {
        var (w, h) = sourceSize;
        return w > 0 && h > 0 ? $"Native · {fps} fps  ({w}x{h})" : $"Native · {fps} fps  (source size)";
    }

    /// <summary>While live, the pickers that only take effect through a restart
    /// are locked so Switch source is the obvious path. Name, port, and encoder
    /// stay editable (name/port are read at the next start; the encoder has no
    /// place in the Switch popup).</summary>
    private void SetLiveLock(bool locked)
    {
        bool on = !locked;
        _rbWindow.Enabled = on;
        _rbMonitor.Enabled = on;
        _windowCombo.Enabled = on;
        _monitorCombo.Enabled = on;
        _presetCombo.Enabled = on;
        _bitrateCombo.Enabled = on;
        _audioCombo.Enabled = on;
    }

    /// <summary>Pixel size of the selected source: monitor resolution, or the
    /// window's stable enumeration snapshot. (0,0) when nothing is resolvable.</summary>
    private static (int W, int H) SelectedSourceSize(bool windowChecked, int windowIdx, int monitorIdx,
        List<WindowDescription> windows, List<MonitorDescription> monitors)
    {
        if (windowChecked)
        {
            if (windowIdx < 0 || windowIdx >= windows.Count) return (0, 0);
            WindowDescription window = windows[windowIdx];
            return (window.Width, window.Height);
        }
        if (monitorIdx >= 0 && monitorIdx < monitors.Count)
            return (monitors[monitorIdx].Width, monitors[monitorIdx].Height);
        return (0, 0);
    }

    /// <summary>The Low/Medium/High options for a preset applied to a source:
    /// the numbers come from the actual output size (pixel area, so a tall
    /// portrait window is billed by its real pixel count, not its height).</summary>
    private static List<BitrateChoice> BuildBitrateChoices(Preset preset, (int W, int H) sourceSize)
    {
        var (srcW, srcH) = sourceSize;
        if (srcH <= 0) (srcW, srcH) = (1920, 1080); // nothing resolved yet
        int outH = preset.Height > 0 && preset.Height < srcH ? preset.Height : srcH;
        int outW = (int)Math.Round((double)srcW * outH / srcH);
        var t = StreamSession.BitrateTiers(outW, outH, preset.Fps);
        return
        [
            new BitrateChoice($"Low · {Mb(t.Low)} Mbps", t.Low, "low"),
            new BitrateChoice($"Medium · {Mb(t.Medium)} Mbps", t.Medium, "med"),
            new BitrateChoice($"High · {Mb(t.High)} Mbps", t.High, "high"),
        ];
    }

    private static string Mb(int kbps) => kbps % 1000 == 0 ? (kbps / 1000).ToString() : (kbps / 1000.0).ToString("0.#");

    /// <summary>Refills the bitrate dropdown for the current preset + source,
    /// keeping the chosen tier (Low/Medium/High) across refills.</summary>
    private void PopulateBitrateOptions((int W, int H) sourceSize)
    {
        if (_presetCombo.SelectedItem is not Preset preset) return;
        var choices = BuildBitrateChoices(preset, sourceSize);
        _bitrateCombo.BeginUpdate();
        _bitrateCombo.Items.Clear();
        foreach (var c in choices) _bitrateCombo.Items.Add(c);
        _bitrateCombo.EndUpdate();
        // Reselect from _savedBitrateTier, not the dropdown's pre-clear value: the
        // tiers are always Low/Medium/High so the saved pick always resolves, and a
        // saved low/high loaded before the first populate is no longer lost.
        int idx = choices.FindIndex(c => c.Tier == _savedBitrateTier);
        _bitrateCombo.SelectedIndex = idx >= 0 ? idx : 1; // Medium
    }

    private void UpdateStatus()
    {
        var b = _session?.Broadcaster;
        if (b is null || _stopping) return;
        if (b.State == "starting")
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = "Starting: waiting for the first captured frame…";
            return;
        }
        if (_session!.LocalOnly)
        {
            _fixPortButton.Visible = true;
            _allowLanCheck.Visible = true;
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = $"LIVE, THIS PC ONLY: click Fix access to let viewers reach port {_livePort}";
        }
        else
        {
            // A Tailscale address is always reachable (99% path). Otherwise LAN
            // is reachable only if LAN access was actually applied. With neither,
            // the stream is up but nobody can reach it — warn instead of green.
            var addrs = StreamSession.GetShareAddresses(includeLan: true);
            bool tailscaleReachable = addrs.Any(StreamSession.IsTailscaleAddress);
            bool lanReachable = !tailscaleReachable && LanAppliedForPort(_livePort)
                && addrs.Any(a => !StreamSession.IsTailscaleAddress(a));
            if (tailscaleReachable || lanReachable)
            {
                _fixPortButton.Visible = false;
                _allowLanCheck.Visible = false;
                _statusLabel.ForeColor = Green;
                string lanTag = tailscaleReachable ? "" : " (LAN)";
                _statusLabel.Text = $"LIVE{lanTag} · {_session.Description} · {EncoderLabel(_session.ActiveEncoder)}   viewers: {b.ViewerCount}   source: {b.SourceFps} fps (dup {b.DupPercent}%)";
            }
            else
            {
                _fixPortButton.Visible = true;
                _allowLanCheck.Visible = true;
                _statusLabel.ForeColor = Color.Goldenrod;
                _statusLabel.Text = "LIVE, but no reachable address in the current scope. Start Tailscale, or enable Allow LAN then Fix access.";
            }
        }
        Text = $"StreamHost - LIVE ({b.ViewerCount} watching)";
    }

    /// <summary>What's actually encoding right now, in GPU/CPU terms.</summary>
    private static string EncoderLabel(string? encoder) => encoder switch
    {
        "h264_nvenc" => "GPU (NVENC)",
        "h264_amf" => "GPU (AMF)",
        "h264_qsv" => "GPU (QSV)",
        "libx264" => "CPU (x264)",
        null => "starting",
        _ => encoder,
    };

    /// <summary>LAN is reachable for a port only when a Fix access with Allow LAN
    /// succeeded for THAT port. Guards against advertising LAN on a port the helper
    /// never opened (e.g. the field was edited to a new port after the fix).</summary>
    private bool LanAppliedForPort(int port) => _allowLanApplied && port == _allowLanPort;

    private string BuildUrl(string pathSuffix)
    {
        // Never hand out a network address the server can't actually answer on.
        // Prefer a Tailscale address (in scope in every firewall config); fall
        // back to a LAN address only if LAN access was actually applied; else
        // localhost. LocalOnly (no URL ACL) forces localhost regardless: honor the
        // live session while streaming, else the holding page's own bind so an
        // idle localhost-only fallback isn't advertised as a network address.
        // Port in play: the live session's while streaming, else the box the link uses.
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;
        string host;
        bool localOnly = _session is { } live ? live.LocalOnly : _idleServer?.LocalOnly == true;
        if (localOnly)
            host = "localhost";
        else
        {
            var tailscale = StreamSession.GetShareAddresses(includeLan: false);
            if (tailscale.Count > 0)
                host = tailscale[0];
            else if (LanAppliedForPort(port) &&
                     StreamSession.GetShareAddresses(includeLan: true)
                         .FirstOrDefault(a => !StreamSession.IsTailscaleAddress(a)) is { } lan)
                host = lan;
            else
                host = "localhost";
        }
        string url = $"http://{host}:{port}/{pathSuffix}";
        // While streaming, the live session's key; while idle, the pending key the
        // next stream will accept, so a link copied now already carries ?k= and a
        // tab opened early self-connects when the stream starts.
        if (pathSuffix.Length == 0 && (_session?.ViewKey ?? _pendingKey) is { } key)
            url += $"?k={key}";
        return url;
    }

    private void UpdateLinkBox() => _linkBox.Text = BuildUrl("");

    private void OpenWatchWindow()
    {
        if (_watchForm is { IsDisposed: false })
        {
            _watchForm.Show();
            _watchForm.WindowState = FormWindowState.Normal;
            _watchForm.Activate();
            return;
        }
        // While live, scan the port actually bound (the box stays editable and can
        // drift from it); otherwise the input value. Same rule as the viewer link.
        _watchForm = new WatchForm(_livePort > 0 ? _livePort : (int)_portInput.Value);
        AppRunContext.Current?.Track(_watchForm); // count it toward app lifetime
        _watchForm.Show();
    }

    private void CopyLink(string url)
    {
        try
        {
            Clipboard.SetText(url);
            AppendLog($"Copied: {RedactKey(url)}" + (url.Contains("://100.") ? "  (Tailscale)" : ""));
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard failed ({ex.Message}); link: {RedactKey(url)}");
        }
    }

    // Delegate to the bundle scrubber so there is ONE key alphabet everywhere.
    // The old \w+ pattern dropped a hyphen in base64url keys, leaking the suffix.
    private static string RedactKey(string text) => Util.BundleScrubber.RedactKeyParam(text);

    /// <summary>Relaunches this exe elevated to reserve the URL and open the
    /// firewall for the current port (same steps as setup.bat), then restarts
    /// the stream so the new binding takes effect. One UAC prompt, no file hunting.</summary>
    private void FixPortAccess()
    {
        // Clicking again while the elevated helper is still running would spawn a
        // second UAC prompt and helper on the same URL reservation. Ignore
        // re-entry until this one has actually exited (see the wait below).
        if (_fixingPort) return;
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;

        // Don't blow away another app's URL reservation without asking. Reading
        // the reservation needs no admin, so we check here on the UI thread
        // before the UAC relaunch. Ours (or none) proceeds silently.
        string me = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string? owner = Util.PortSetup.ReadReservationOwner(port);
        if (owner is not null && !owner.Equals(me, StringComparison.OrdinalIgnoreCase))
        {
            // The sentinel means the reservation exists but its owner couldn't be
            // read (or even probed): word the prompt for that instead of splicing
            // the sentinel into "reserved by another account: <owner>".
            string body = owner == Util.PortSetup.UnknownOwner
                ? $"Port {port} is already reserved, but StreamHost could not read which account owns it.\n\n" +
                  "Replacing that reservation may break the app that created it. " +
                  "Reserve this port for StreamHost anyway?"
                : $"Port {port} is already reserved by another account:\n\n{owner}\n\n" +
                  "Replacing that reservation may break the app that created it. " +
                  "Reserve this port for StreamHost anyway?";
            var choice = MessageBox.Show(this, body,
                "StreamHost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                AppendLog($"Kept the existing reservation for port {port}; pick a different port instead.");
                return;
            }
        }

        // Snapshot the requested scope BEFORE launching. The helper can run for
        // many seconds; if the user toggles Allow LAN meanwhile, we must persist
        // what was actually applied, not the later checkbox state.
        bool requestedAllowLan = _allowLanCheck.Checked;

        AppendLog($"Asking for administrator approval to configure port {port}…");
        string arguments = $"--setup-port {port} --setup-user \"{me}\"";
        if (requestedAllowLan) arguments += " --setup-lan";
        var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        _fixingPort = true; // cleared only when the helper actually exits
        // Freeze the scope inputs while a fix is in flight so nothing changes out
        // from under the snapshot above.
        _allowLanCheck.Enabled = false;
        _portInput.Enabled = false;
        _fixPortButton.Enabled = false;
        new Thread(() =>
        {
            int code;
            try
            {
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) code = -1;
                else
                {
                    // The helper's work (two urlacl reads plus several netsh calls
                    // and a possible rollback) can outrun this wait. The helper is
                    // ELEVATED and this UI is not, so we cannot kill it (Process.Kill
                    // throws Access Denied across the elevation boundary). Giving up
                    // and clearing the guard here would let a re-click run a SECOND
                    // helper on the same URL reservation, so instead we keep waiting
                    // for the real exit and just tell the user it is still going.
                    if (!p.WaitForExit(60000))
                    {
                        try { BeginInvoke(() => AppendLog("Still configuring the port. Waiting for the administrator step to finish…")); }
                        catch { }
                        p.WaitForExit(); // unbounded: only a real exit clears the guard
                    }
                    code = p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception) // UAC declined
            {
                code = -2;
            }
            catch
            {
                code = -1;
            }
            try
            {
                BeginInvoke(() =>
                {
                    _fixingPort = false;
                    _allowLanCheck.Enabled = true;
                    _portInput.Enabled = true;
                    _fixPortButton.Enabled = true;
                    if (code == 0)
                    {
                        // Record the scope that was actually applied — the pre-launch
                        // snapshot, never the live checkbox, and only on success. Bind it
                        // to the exact port the helper configured (the snapshot `port`, not
                        // the current box), so a later port edit can't inherit this LAN grant.
                        // On a success with Allow LAN unchecked, applied stays false but the
                        // port is still recorded (the helper configured it Tailscale-only).
                        _allowLanApplied = requestedAllowLan;
                        _allowLanPort = port;
                        SaveSettings();
                        AppendLog($"Port {port} configured.");
                        if (_session is not null && _lastConfig is not null && !_stopping)
                        {
                            AppendLog("Restarting the stream so the new access takes effect…");
                            _pendingSwitch = _lastConfig;
                            _stopping = true;
                            _statsTimer.Stop();
                            _statusLabel.Text = "Restarting…";
                            _statusLabel.ForeColor = Color.Goldenrod;
                            _session.RequestStop();
                        }
                    }
                    else if (code == -2)
                        AppendLog("Administrator approval was declined; viewers on other machines stay blocked.");
                    else
                        AppendLog($"Port setup failed (code {code}). Fallback: run setup.bat {port} as administrator.");
                });
            }
            catch { _fixingPort = false; } // form gone before we could report back — release the guard
        })
        { IsBackground = true, Name = "fix-port" }.Start();
    }

    /// <summary>Everything needed to debug a report, one clipboard copy:
    /// version, OS, GPUs, ffmpeg, tailnet paths, encoder cache, session state,
    /// settings, log tail.</summary>
    private async void CopySupportBundle()
    {
        try
        {
            // Capture the active session identity as one immutable reference, then
            // keep every CLI/DXGI/hash operation off the UI thread. A missing active
            // identity falls back honestly to the first hardware adapter.
            var activeCapture = _session?.CaptureAdapter;
            var diagnostics = await Task.Run(() =>
            {
                string tailnet = RedactPeerNames(Util.StreamDiscovery.DescribeTailnetPaths());
                string gpus = string.Join("; ", GpuAdapters());
                var ffmpeg = Encode.FfmpegEncoder.FfmpegBuildInfo();
                (uint vendorId, string luid, string driver) gpu;
                string gpuSource;
                if (activeCapture is not null)
                {
                    gpu = (activeCapture.VendorId, activeCapture.Luid, activeCapture.DriverVersion);
                    gpuSource = "active capture adapter";
                }
                else
                {
                    gpu = PrimaryGpu();
                    gpuSource = "primary adapter fallback";
                }
                string expected = Encode.FfmpegEncoder.ExpectedProbeToken(
                    gpu.vendorId, gpu.luid, gpu.driver, ffmpeg)
                    ?? "(identity incomplete; probe will run)";
                return (tailnet, gpus, ffmpeg, gpu, gpuSource, expected);
            });
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"StreamHost {AppVersion()}");
            sb.AppendLine($"Windows:  {Environment.OSVersion.VersionString}");
            sb.AppendLine($"GPUs:     {diagnostics.gpus}");
            sb.AppendLine($"ffmpeg:   {diagnostics.ffmpeg.version}");
            sb.AppendLine($"ffmpeg build: {diagnostics.ffmpeg.buildconf}");
            sb.AppendLine($"ffmpeg sha256: {diagnostics.ffmpeg.sha256}");
            sb.AppendLine($"tailnet:  {diagnostics.tailnet}");
            sb.AppendLine($"enc cache: {ReadSmallFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "encoder.cache"))}");
            // A report can compare this to the cached verdict above. During a live
            // session it describes the exact capture adapter supplied to encoder selection;
            // while idle the label makes the primary-adapter fallback explicit.
            sb.AppendLine($"enc expected: {diagnostics.expected}  ({diagnostics.gpuSource}, adapter LUID {diagnostics.gpu.luid}, driver {diagnostics.gpu.driver})");
            sb.AppendLine($"session:  {DescribeSessionState()}");
            sb.AppendLine($"settings: {ReadSmallFile(SettingsPath)}");
            sb.AppendLine("---- last 200 log lines ----");
            // Prefer the on-disk log: it carries per-line timestamps (the box
            // does not), which is what makes a stall report diagnosable.
            string[] lines = ReadLogTail(200) ?? _logBox.Lines;
            foreach (string line in lines.Skip(Math.Max(0, lines.Length - 200)))
                sb.AppendLine(line);
            // Scrub the WHOLE blob in one place — keys, Tailscale IPs, and the
            // username/paths across every line — right before it hits the
            // clipboard and, from there, a public issue. The live keys are passed
            // as exact secrets so a raw key without a ?k= wrapper is caught too.
            string scrubbed = Util.BundleScrubber.Scrub(sb.ToString(),
                new[] { _session?.ViewKey, _lastConfig?.ViewKey, _pendingKey });
            Clipboard.SetText(scrubbed);
            AppendLog("Log copied (scrubbed, with version, system, and encoder info); paste it into a bug report.");
        }
        catch (Exception ex)
        {
            AppendLog($"Copy log failed: {ex.Message}");
        }
    }

    /// <summary>Opens the log file's containing folder in Explorer with the current
    /// log selected. Falls back to the app-data logs folder when there is no log
    /// file yet (log-file creation was disabled or failed).</summary>
    private void OpenLogsFolder()
    {
        try
        {
            string? path = ConsoleMirror.LogFilePath;
            if (path is not null && File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "logs");
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open the log folder: {ex.Message}");
        }
    }

    internal static string AppVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "dev";

    /// <summary>One-line description of the running session (or "not streaming"),
    /// shared by the support bundle and the crash log.</summary>
    internal string DescribeSessionState() =>
        _session is { } s
            ? $"{s.Description} via {s.ActiveEncoder}, state {s.Broadcaster?.State}, viewers {s.Broadcaster?.ViewerCount}, localOnly {s.LocalOnly}"
            : "not streaming";

    /// <summary>Fatal-crash path only: stop a live session before the process
    /// exits, so ffmpeg tears down and the port releases instead of being killed
    /// mid-write. Stop() is bounded (a join with a timeout) so a wedged session
    /// can't hang the exit. Guarded — a crash handler must never throw again.</summary>
    internal void StopSessionForShutdown()
    {
        try { _session?.Stop(); } catch { }
    }

    private static List<string> GpuAdapters()
    {
        var list = new List<string>();
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out Vortice.DXGI.IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter!.Description1;
                    if ((d.Flags & Vortice.DXGI.AdapterFlags.Software) == 0)
                        list.Add($"{d.Description} (vendor 0x{d.VendorId:X4}, driver {AdapterDriver(adapter)})");
                }
            }
        }
        catch { }
        if (list.Count == 0) list.Add("unknown");
        return list;
    }

    /// <summary>Best-effort UMD driver version for a DXGI adapter, decoded from the
    /// packed CheckInterfaceSupport long (same layout as the capture init log).
    /// Diagnostics only — never throws; returns "?" when unavailable.</summary>
    private static string AdapterDriver(Vortice.DXGI.IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(typeof(Vortice.DXGI.IDXGIDevice), out long umd))
                return $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
        }
        catch { }
        return "?";
    }

    /// <summary>Identity of the first hardware adapter (vendor id, LUID, driver)
    /// for the probe-cache fingerprint. Diagnostics only — never throws; returns
    /// zero/"?" placeholders when DXGI can't be read.</summary>
    private static (uint vendorId, string luid, string driver) PrimaryGpu()
    {
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out Vortice.DXGI.IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter!.Description1;
                    if ((d.Flags & Vortice.DXGI.AdapterFlags.Software) != 0) continue;
                    return ((uint)d.VendorId, $"{d.Luid.HighPart}:{d.Luid.LowPart}", AdapterDriver(adapter));
                }
            }
        }
        catch { }
        return (0, "?", "?");
    }

    /// <summary>Genericizes remote peer HOSTNAMES in the tailnet line (friends'
    /// machine names) to peer1/peer2/... while keeping each direct/relay/idle path
    /// token — the diagnostic value is the path mix, not the names. Status messages
    /// like "tailscale not running (state: ...)" have no name: path rows and pass
    /// through untouched. Applied here, not in DescribeTailnetPaths, so its other
    /// callers still see the real names.</summary>
    private static string RedactPeerNames(string tailnet)
    {
        if (string.IsNullOrEmpty(tailnet)) return tailnet;
        var entries = tailnet.Split(", ");
        int n = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            int sep = entries[i].IndexOf(": ", StringComparison.Ordinal);
            if (sep < 0) continue;
            string path = entries[i][(sep + 2)..];
            // Only rewrite genuine "name: path" rows; leave any status text alone.
            if (path.StartsWith("direct") || path.StartsWith("idle")
                || path.StartsWith("relay") || path.StartsWith("unknown"))
                entries[i] = $"peer{++n}: {path}";
        }
        return string.Join(", ", entries);
    }

    private static string ReadSmallFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Replace("\r", "").Replace('\n', ' ') : "(none)"; }
        catch { return "(unreadable)"; }
    }

    /// <summary>Last N lines of the timestamped log file, or null if it can't be
    /// read (the caller then falls back to the on-screen log box). Opens shared
    /// for read/write because ConsoleMirror still has the file open.</summary>
    private static string[]? ReadLogTail(int count)
    {
        try
        {
            string? path = ConsoleMirror.LogFilePath;
            if (path is null || !File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var all = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) all.Add(line);
            return all.Skip(Math.Max(0, all.Count - count)).ToArray();
        }
        catch { return null; }
    }

    // ---- settings ---------------------------------------------------------

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (s is null) return;

            _rbMonitor.Checked = s.SourceKind == "monitor";
            _rbWindow.Checked = !_rbMonitor.Checked;
            if (s.MonitorIndex >= 0 && s.MonitorIndex < _monitorCombo.Items.Count)
                _monitorCombo.SelectedIndex = s.MonitorIndex;
            if (!string.IsNullOrEmpty(s.WindowProcess))
            {
                int idx = _windows.FindIndex(w => w.ProcessName.Equals(s.WindowProcess, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _windowCombo.SelectedIndex = idx;
            }
            // Set the saved tier first: changing the preset index below fires
            // PopulateBitrateOptions, which reads _savedBitrateTier to reselect.
            if (s.BitrateTier is "low" or "med" or "high") _savedBitrateTier = s.BitrateTier;
            int presetIdx = Array.FindIndex(Presets, p => p.Height == s.PresetHeight && p.Fps == s.PresetFps);
            _presetCombo.SelectedIndex = presetIdx >= 0 ? presetIdx : DefaultPresetIndex;
            SelectAudioByKey(s.AudioSource);
            if (s.Port is >= 1024 and <= 65535) _portInput.Value = s.Port;
            if (!string.IsNullOrWhiteSpace(s.StreamName)) _nameInput.Text = s.StreamName;
            int encIdx = -1;
            for (int i = 0; i < _encoderCombo.Items.Count; i++)
            {
                if (_encoderCombo.Items[i] is EncoderChoice e && e.Value == s.Encoder)
                {
                    encIdx = i;
                    break;
                }
            }
            _encoderCombo.SelectedIndex = encIdx >= 0 ? encIdx : 0;
            _allowLanApplied = s.AllowLan;
            _allowLanPort = s.AllowLanPort;
            _allowLanCheck.Checked = s.AllowLan;
            UpdateAudioModeLabel();
        }
        catch { /* corrupted settings are not worth crashing over */ }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new AppSettings
            {
                SourceKind = _rbMonitor.Checked ? "monitor" : "window",
                WindowProcess = _windowCombo.SelectedIndex >= 0 && _windowCombo.SelectedIndex < _windows.Count
                    ? _windows[_windowCombo.SelectedIndex].ProcessName : "",
                MonitorIndex = Math.Max(_monitorCombo.SelectedIndex, 0),
                PresetHeight = (_presetCombo.SelectedItem as Preset ?? Presets[DefaultPresetIndex]).Height,
                PresetFps = (_presetCombo.SelectedItem as Preset ?? Presets[DefaultPresetIndex]).Fps,
                BitrateTier = (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? "med",
                AudioSource = SelectedAudioKey(),
                Port = (int)_portInput.Value,
                StreamName = _nameInput.Text.Trim(),
                Encoder = ((EncoderChoice)_encoderCombo.SelectedItem!).Value,
                AllowLan = _allowLanApplied,
                AllowLanPort = _allowLanPort,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Write to a sibling temp file, then atomically swap it in, so a crash
            // mid-write can't leave a truncated/corrupt settings.json behind.
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch { }
    }

    private void AppendLog(string line)
    {
        if (_logBox.TextLength > 200_000)
        {
            _logBox.Clear();
            _logBox.AppendText(line + Environment.NewLine);
            return;
        }

        int bottomChar = _logBox.GetCharIndexFromPosition(
            new Point(0, Math.Max(0, _logBox.ClientSize.Height - 1)));
        int lastVisibleLine = _logBox.GetLineFromCharIndex(bottomChar);
        int lastLine = _logBox.GetLineFromCharIndex(_logBox.TextLength);
        if (lastVisibleLine >= lastLine - 1)
        {
            _logBox.AppendText(line + Environment.NewLine);
            return;
        }

        int selectionStart = _logBox.SelectionStart;
        int selectionLength = _logBox.SelectionLength;
        int firstVisibleLine = (int)SendMessage(
            _logBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);

        SendMessage(_logBox.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            _logBox.AppendText(line + Environment.NewLine);
            _logBox.Select(selectionStart, selectionLength);
            int currentFirstVisibleLine = (int)SendMessage(
                _logBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
            SendMessage(_logBox.Handle, EmLineScroll, IntPtr.Zero,
                new IntPtr(firstVisibleLine - currentFirstVisibleLine));
        }
        finally
        {
            SendMessage(_logBox.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            _logBox.Invalidate();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
