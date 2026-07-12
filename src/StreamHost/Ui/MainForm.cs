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
    private bool _stopping;               // RequestStop sent; waiting for the session's Stopped event
    private SessionConfig? _lastConfig;   // last launched config, reused for the fix-port restart
    private SessionConfig? _pendingSwitch; // queued source switch, launched when Stopped fires
    private string _savedBitrateTier = "med"; // from settings, applied on the first populate
    // Persisted "LAN access was actually applied" flag: set only on a successful
    // Fix access with Allow LAN checked, never on a mere checkbox toggle. Gates
    // whether LAN addresses are treated as reachable for links and status.
    private bool _allowLanApplied;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Visible window bounds without the invisible resize border, so the Native
    // preset label shows the size capture will actually see.
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, BackColor = Bg, ColumnCount = 4, Padding = new Padding(12, 4, 8, 0) };
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
        _encoderCombo.Items.AddRange(Encoders);
        _encoderCombo.SelectedIndex = 0;

        ApplyDarkTheme(this);
        _startButton.BackColor = AccentDark;
        _logBox.BackColor = Color.FromArgb(16, 18, 21);
        _logBox.ForeColor = Color.Gainsboro;
        _linkBox.BackColor = Card;
        _linkBox.ForeColor = Dim;

        // Nothing gets disabled — the selected radio decides which combo is USED.
        _windowCombo.DropDown += (_, _) => PopulateWindows();   // fresh list every open
        _monitorCombo.DropDown += (_, _) => PopulateMonitors(); // monitors change too (dock/undock)
        _rbWindow.CheckedChanged += (_, _) => { UpdateAudioModeLabel(); UpdateNativePresetLabel(); PopulateBitrateOptions(); };
        _windowCombo.SelectedIndexChanged += (_, _) => { UpdateNativePresetLabel(); PopulateBitrateOptions(); };
        _monitorCombo.SelectedIndexChanged += (_, _) => { UpdateNativePresetLabel(); PopulateBitrateOptions(); };
        _presetCombo.SelectedIndexChanged += (_, _) => PopulateBitrateOptions();
        _startButton.Click += (_, _) =>
        {
            // Clicking during a scheduled CPU retry cancels the retry.
            if (_pendingCpuRetry) { _pendingCpuRetry = false; OnSessionStopped(null); return; }
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
            session?.Stop();
            StopIdleServer();
            // These timers and the tooltip are not in a components container, so
            // nothing else disposes them.
            _statsTimer.Dispose();
            _idleRetryTimer.Dispose();
            _toolTip.Dispose();
        };

        PopulateSources();
        LoadSettings();
        UpdateLinkBox();
        UpdateNativePresetLabel();
        PopulateBitrateOptions();
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
        var config = BuildConfigFromUi((int)_portInput.Value, SessionConfig.NewViewKey());
        if (config is null) return;

        if (config.Port != 8093)
            AppendLog("Note: setup.bat / Fix access configure one port at a time — other ports need their own run.");

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
            else AppendLog($"[audio] '{audioKey}' is not running — streaming without audio");
        }

        IntPtr windowHandle = IntPtr.Zero, monitorHandle = IntPtr.Zero;
        string sourceName;
        if (_rbWindow.Checked)
        {
            if (_windowCombo.SelectedIndex < 0) { AppendLog("No window selected."); return null; }
            var w = _windows[_windowCombo.SelectedIndex];
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
                        AppendLog("GPU encoder produced no video — restarting with the CPU encoder (libx264)…");
                        // Auto mode trusts a cached probe verdict; a live stall just
                        // disproved it, so drop the cache to force a fresh probe next
                        // launch. Explicit encoder mode never cached, so nothing to clear.
                        if (string.IsNullOrEmpty(config.Encoder) || config.Encoder == "auto")
                            StreamHost.Encode.FfmpegEncoder.InvalidateProbeCache();
                        // libx264 at 1440p and up may not sustain the same resolution/fps
                        // the GPU handled — warn instead of calling fallback a recovery.
                        if (session.OutputHeight >= 1440)
                            AppendLog($"Warning: libx264 (CPU) may not keep up at {session.OutputWidth}x{session.OutputHeight}@{config.Fps} — lower the Preset if playback is choppy.");
                        var fallback = config with { Encoder = "libx264" };
                        var t = new System.Windows.Forms.Timer { Interval = 250 };
                        t.Tick += (_, _) =>
                        {
                            t.Stop(); t.Dispose();
                            if (_pendingCpuRetry) { _pendingCpuRetry = false; LaunchSession(fallback); }
                        };
                        t.Start();
                        return;
                    }
                    OnSessionStopped(userRequested ? null : reason);
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

    /// <summary>Guided switch: a small popup with just the source, preset, and
    /// audio pickers, prefilled from the current selections. OK writes the
    /// choices back to the main controls and goes through the normal switch
    /// (or plain start when idle), so both paths stay one code path.</summary>
    private void ShowSwitchDialog()
    {
        if (_stopping) return;
        PopulateSources(); // fresh window/monitor lists for the popup

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

        // Mirror the main combos item-for-item so OK can copy indexes back.
        foreach (object it in _windowCombo.Items) winCombo.Items.Add(it);
        foreach (object it in _monitorCombo.Items) monCombo.Items.Add(it);
        foreach (object it in _presetCombo.Items) presetCombo.Items.Add(it);
        foreach (object it in _audioCombo.Items) audioCombo.Items.Add(it);
        winCombo.SelectedIndex = _windowCombo.SelectedIndex;
        monCombo.SelectedIndex = _monitorCombo.SelectedIndex;
        presetCombo.SelectedIndex = _presetCombo.SelectedIndex;
        audioCombo.SelectedIndex = _audioCombo.SelectedIndex;
        // The dialog's Native entries track ITS pickers, same as the main window's.
        void UpdateDlgNativeLabel()
        {
            for (int idx = 0; idx < Presets.Length && idx < presetCombo.Items.Count; idx++)
            {
                if (Presets[idx].Height != 0) continue;
                int sel = presetCombo.SelectedIndex;
                presetCombo.Items[idx] = Presets[idx] with
                {
                    Label = ComputeNativeLabel(Presets[idx].Fps, rbWin.Checked, winCombo.SelectedIndex, monCombo.SelectedIndex),
                };
                presetCombo.SelectedIndex = sel;
            }
        }
        // The dialog's bitrate options track ITS pickers too.
        void UpdateDlgBitrate()
        {
            if (presetCombo.SelectedItem is not Preset p) return;
            string keep = (bitrateCombo.SelectedItem as BitrateChoice)?.Tier
                          ?? (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? "med";
            var choices = BuildBitrateChoices(p, rbWin.Checked, winCombo.SelectedIndex, monCombo.SelectedIndex);
            bitrateCombo.BeginUpdate();
            bitrateCombo.Items.Clear();
            foreach (var c in choices) bitrateCombo.Items.Add(c);
            bitrateCombo.EndUpdate();
            int i = choices.FindIndex(c => c.Tier == keep);
            bitrateCombo.SelectedIndex = i >= 0 ? i : 1;
        }
        rbWin.CheckedChanged += (_, _) =>
        {
            if (audioCombo.Items.Count >= 2)
                audioCombo.Items[1] = rbWin.Checked
                    ? "Captured window's audio"
                    : "No audio (monitor share: pick an app below)";
            UpdateDlgNativeLabel();
            UpdateDlgBitrate();
        };
        winCombo.SelectedIndexChanged += (_, _) => { UpdateDlgNativeLabel(); UpdateDlgBitrate(); };
        monCombo.SelectedIndexChanged += (_, _) => { UpdateDlgNativeLabel(); UpdateDlgBitrate(); };
        presetCombo.SelectedIndexChanged += (_, _) => UpdateDlgBitrate();
        UpdateDlgBitrate();

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

        _rbWindow.Checked = rbWin.Checked;
        _rbMonitor.Checked = rbMon.Checked;
        if (winCombo.SelectedIndex >= 0) _windowCombo.SelectedIndex = winCombo.SelectedIndex;
        if (monCombo.SelectedIndex >= 0) _monitorCombo.SelectedIndex = monCombo.SelectedIndex;
        if (presetCombo.SelectedIndex >= 0) _presetCombo.SelectedIndex = presetCombo.SelectedIndex;
        if (audioCombo.SelectedIndex >= 0) _audioCombo.SelectedIndex = audioCombo.SelectedIndex;
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

    private void OnSessionStopped(string? reason)
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
            _idleServer = new WebServer((int)_portInput.Value, null, null, _nameInput.Text.Trim());
            _idleCts = new CancellationTokenSource();
            _ = _idleServer.RunAsync(_idleCts.Token);
            _idleRetryTimer.Stop();
            _idleBindFailed = false;
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

    /// <summary>Rewrites the Native preset entries with the selected source's real
    /// resolution, e.g. "Native · 60 fps (2560x1440)". The bitrate dropdown next
    /// to it carries the Mbps numbers.</summary>
    private void UpdateNativePresetLabel()
    {
        for (int idx = 0; idx < Presets.Length && idx < _presetCombo.Items.Count; idx++)
        {
            if (Presets[idx].Height != 0) continue;
            string label = ComputeNativeLabel(Presets[idx].Fps, _rbWindow.Checked, _windowCombo.SelectedIndex, _monitorCombo.SelectedIndex);
            if (_presetCombo.Items[idx] is Preset p && p.Label == label) continue;
            int sel = _presetCombo.SelectedIndex;
            _presetCombo.Items[idx] = Presets[idx] with { Label = label };
            _presetCombo.SelectedIndex = sel;
        }
    }

    private string ComputeNativeLabel(int fps, bool windowChecked, int windowIdx, int monitorIdx)
    {
        var (w, h) = SelectedSourceSize(windowChecked, windowIdx, monitorIdx);
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
    /// window's visible bounds. (0,0) when nothing is resolvable.</summary>
    private (int W, int H) SelectedSourceSize(bool windowChecked, int windowIdx, int monitorIdx)
    {
        if (windowChecked)
        {
            if (windowIdx < 0 || windowIdx >= _windows.Count) return (0, 0);
            IntPtr hwnd = _windows[windowIdx].Handle;
            if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);
            return (r.Right - r.Left, r.Bottom - r.Top);
        }
        if (monitorIdx >= 0 && monitorIdx < _monitors.Count)
            return (_monitors[monitorIdx].Width, _monitors[monitorIdx].Height);
        return (0, 0);
    }

    /// <summary>The Low/Medium/High options for a preset applied to a source:
    /// the numbers come from the actual output size (pixel area, so a tall
    /// portrait window is billed by its real pixel count, not its height).</summary>
    private List<BitrateChoice> BuildBitrateChoices(Preset preset, bool windowChecked, int windowIdx, int monitorIdx)
    {
        var (srcW, srcH) = SelectedSourceSize(windowChecked, windowIdx, monitorIdx);
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
    private void PopulateBitrateOptions()
    {
        if (_presetCombo.SelectedItem is not Preset preset) return;
        string keepTier = (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? _savedBitrateTier;
        var choices = BuildBitrateChoices(preset, _rbWindow.Checked, _windowCombo.SelectedIndex, _monitorCombo.SelectedIndex);
        _bitrateCombo.BeginUpdate();
        _bitrateCombo.Items.Clear();
        foreach (var c in choices) _bitrateCombo.Items.Add(c);
        _bitrateCombo.EndUpdate();
        int idx = choices.FindIndex(c => c.Tier == keepTier);
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
            bool lanReachable = !tailscaleReachable && _allowLanApplied
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

    private string BuildUrl(string pathSuffix)
    {
        // Never hand out a network address the server can't actually answer on.
        // Prefer a Tailscale address (in scope in every firewall config); fall
        // back to a LAN address only if LAN access was actually applied; else
        // localhost. LocalOnly (no URL ACL) forces localhost regardless.
        string host;
        if (_session?.LocalOnly == true)
            host = "localhost";
        else
        {
            var tailscale = StreamSession.GetShareAddresses(includeLan: false);
            if (tailscale.Count > 0)
                host = tailscale[0];
            else if (_allowLanApplied &&
                     StreamSession.GetShareAddresses(includeLan: true)
                         .FirstOrDefault(a => !StreamSession.IsTailscaleAddress(a)) is { } lan)
                host = lan;
            else
                host = "localhost";
        }
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;
        string url = $"http://{host}:{port}/{pathSuffix}";
        if (pathSuffix.Length == 0 && _session?.ViewKey is { } key)
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
            AppendLog($"Clipboard failed ({ex.Message}) — link: {RedactKey(url)}");
        }
    }

    private static string RedactKey(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\?k=\w+", "?k=[key]");

    /// <summary>Relaunches this exe elevated to reserve the URL and open the
    /// firewall for the current port (same steps as setup.bat), then restarts
    /// the stream so the new binding takes effect. One UAC prompt, no file hunting.</summary>
    private void FixPortAccess()
    {
        // Clicking again while the elevated helper is still running would spawn a
        // second UAC prompt and helper. Ignore re-entry until this one finishes.
        if (_fixingPort) return;
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;

        // Don't blow away another app's URL reservation without asking. Reading
        // the reservation needs no admin, so we check here on the UI thread
        // before the UAC relaunch. Ours (or none) proceeds silently.
        string me = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string? owner = ReadReservationOwner(port);
        if (owner is not null && !owner.Equals(me, StringComparison.OrdinalIgnoreCase))
        {
            var choice = MessageBox.Show(this,
                $"Port {port} is already reserved by another account:\n\n{owner}\n\n" +
                "Replacing that reservation may break the app that created it. " +
                "Reserve this port for StreamHost anyway?",
                "StreamHost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                AppendLog($"Kept the existing reservation for port {port}; pick a different port instead.");
                return;
            }
        }

        AppendLog($"Asking for administrator approval to configure port {port}…");
        string arguments = $"--setup-port {port} --setup-user \"{me}\"";
        if (_allowLanCheck.Checked) arguments += " --setup-lan";
        var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        _fixingPort = true; // cleared when the helper finishes (or if we can't report back)
        new Thread(() =>
        {
            int code;
            try
            {
                using var p = System.Diagnostics.Process.Start(psi);
                code = p is not null && p.WaitForExit(60000) ? p.ExitCode : -1;
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
                    if (code == 0)
                    {
                        // Record the scope that was actually applied — only on
                        // success, so a declined/failed attempt never widens it.
                        _allowLanApplied = _allowLanCheck.Checked;
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
                        AppendLog("Administrator approval was declined — viewers on other machines stay blocked.");
                    else
                        AppendLog($"Port setup failed (code {code}). Fallback: run setup.bat {port} as administrator.");
                });
            }
            catch { _fixingPort = false; } // form gone before we could report back — release the guard
        })
        { IsBackground = true, Name = "fix-port" }.Start();
    }

    /// <summary>The account currently granted this port's URL reservation, or
    /// null if none is reserved / it can't be read. Reading it needs no
    /// elevation. Fails CLOSED: existence is tested against the reserved URL
    /// string (which is not localized), so on a non-English Windows where the
    /// "User:" line is labelled differently, a foreign reservation still returns
    /// a sentinel — the caller's confirm dialog then fires instead of silently
    /// replacing it. English systems parse the User: line first, unchanged.</summary>
    private static string? ReadReservationOwner(int port)
    {
        try
        {
            var r = Util.ProcessRunner.Run("netsh", $"http show urlacl url=http://+:{port}/", 5000);
            bool reserved = r.StdOut.Contains($"http://+:{port}/", StringComparison.OrdinalIgnoreCase);
            foreach (var line in r.StdOut.Split('\n'))
            {
                int idx = line.IndexOf("User:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string owner = line[(idx + 5)..].Trim();
                if (owner.Length > 0) return owner;
            }
            // Reserved but the owner line didn't parse (non-English locale): don't
            // pretend it's ours. A sentinel worded to read well in the dialog.
            return reserved ? "an account StreamHost could not identify" : null;
        }
        catch { }
        return null;
    }

    /// <summary>Everything needed to debug a report, one clipboard copy:
    /// version, OS, GPUs, ffmpeg, tailnet paths, encoder cache, session state,
    /// settings, log tail.</summary>
    private async void CopySupportBundle()
    {
        try
        {
            // The tailscale CLI can block for seconds; keep it off the UI thread.
            // ffmpeg -version + the binary hash also shell out, so batch them too.
            string tailnet = await Task.Run(() => RedactPeerNames(Util.StreamDiscovery.DescribeTailnetPaths()));
            var ffmpeg = await Task.Run(Encode.FfmpegEncoder.FfmpegBuildInfo);
            var gpu = PrimaryGpu();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"StreamHost {AppVersion()}");
            sb.AppendLine($"Windows:  {Environment.OSVersion.VersionString}");
            sb.AppendLine($"GPUs:     {string.Join("; ", GpuAdapters())}");
            sb.AppendLine($"ffmpeg:   {ffmpeg.version}");
            sb.AppendLine($"ffmpeg build: {ffmpeg.buildconf}");
            sb.AppendLine($"ffmpeg sha256: {ffmpeg.sha256}");
            sb.AppendLine($"tailnet:  {tailnet}");
            sb.AppendLine($"enc cache: {ReadSmallFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "encoder.cache"))}");
            // Expected token for the current adapter — a report can compare it to
            // the cached verdict above to spot a stale probe.
            sb.AppendLine($"enc expected: {Encode.FfmpegEncoder.ExpectedProbeToken(gpu.vendorId)}  (adapter LUID {gpu.luid}, driver {gpu.driver})");
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
                new[] { _session?.ViewKey, _lastConfig?.ViewKey });
            Clipboard.SetText(scrubbed);
            AppendLog("Log copied (scrubbed, with version, system, and encoder info) — paste it into a bug report.");
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
            int encIdx = Array.FindIndex(Encoders, e => e.Value == s.Encoder);
            _encoderCombo.SelectedIndex = encIdx >= 0 ? encIdx : 0;
            _allowLanApplied = s.AllowLan;
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
        if (_logBox.TextLength > 200_000) _logBox.Clear();
        _logBox.AppendText(line + Environment.NewLine);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
