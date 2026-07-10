using System.Runtime.InteropServices;
using System.Text.Json;
using StreamHost.Capture;
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
    private static readonly Color Bg = Color.FromArgb(31, 33, 39);
    private static readonly Color Card = Color.FromArgb(42, 45, 53);
    private static readonly Color Border = Color.FromArgb(60, 65, 74);
    private static readonly Color Fg = Color.FromArgb(212, 216, 222);
    private static readonly Color Dim = Color.FromArgb(150, 156, 165);
    private static readonly Color AccentDark = Color.FromArgb(48, 92, 146);
    private static readonly Color Green = Color.FromArgb(112, 186, 130);
    private static readonly Color Red = Color.FromArgb(210, 115, 115);

    private sealed record Preset(string Label, int Height, int Fps, int Kbps)
    {
        public override string ToString() => Label;
    }

    private static readonly Preset[] Presets =
    [
        new("720p · 30 fps  (~4 Mbps)", 720, 30, 4000),
        new("720p · 60 fps  (~6 Mbps)", 720, 60, 6000),
        new("1080p · 30 fps  (~8 Mbps)", 1080, 30, 8000),
        new("1080p · 60 fps  (~12 Mbps)", 1080, 60, 12000),
        new("1440p · 30 fps  (~12 Mbps)", 1440, 30, 12000),
        new("1440p · 60 fps  (~18 Mbps)", 1440, 60, 18000),
        // Kbps 0 = auto: the session picks the bitrate from the real output
        // resolution (native on a 1440p monitor needs 18 Mbps, not 1080p's 12).
        new("Native · 60 fps  (bitrate matches resolution)", 0, 60, 0),
    ];

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
        public string AudioSource { get; set; } = "window"; // "none" | "window" | process name
        public int Port { get; set; } = 8093;
        public string StreamName { get; set; } = ""; // shown to viewers; empty = machine name
        public string Encoder { get; set; } = "auto";
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "settings.json");

    private readonly RadioButton _rbWindow = new() { Text = "Game / window", Checked = true, AutoSize = true };
    private readonly RadioButton _rbMonitor = new() { Text = "Whole monitor", AutoSize = true };
    private readonly ComboBox _windowCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _encoderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _nameInput = new() { Width = 150, MaxLength = 32, BorderStyle = BorderStyle.FixedSingle, Text = Environment.MachineName };
    private readonly Button _watchButton = new() { Text = "Watch streams", Width = 118, Height = 38 };
    private readonly Button _bundleButton = new() { Text = "Copy log", Width = 82, Height = 24, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button _fixPortButton = new() { Text = "Fix access (open port)", Width = 142, Height = 24, Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
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
    private readonly NotifyIcon _tray = new();
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm()
    {
        Text = "StreamHost";
        MinimumSize = new Size(680, 570);
        Size = new Size(700, 612);
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

        var optionsRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 2, BackColor = Bg };
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var qualityGroup = MakeGroup("Quality", 0);
        qualityGroup.Dock = DockStyle.Fill;
        qualityGroup.Margin = new Padding(0, 0, 3, 0);
        var qualityGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        qualityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        qualityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qualityGrid.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 0);
        qualityGrid.Controls.Add(_presetCombo, 1, 0);
        qualityGrid.Controls.Add(new Label { Text = "Encoder:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 1);
        qualityGrid.Controls.Add(_encoderCombo, 1, 1);
        _presetCombo.Width = 250;
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
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, BackColor = Bg, ColumnCount = 3, Padding = new Padding(12, 4, 8, 0) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(_fixPortButton, 1, 0);
        statusPanel.Controls.Add(_bundleButton, 2, 0);
        _statusLabel.Anchor = AnchorStyles.Left;
        _fixPortButton.Anchor = AnchorStyles.Right;
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
        _rbWindow.CheckedChanged += (_, _) => UpdateAudioModeLabel();
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
        _fixPortButton.Click += (_, _) => FixPortAccess();
        _portInput.ValueChanged += (_, _) => UpdateLinkBox();
        _statsTimer.Tick += (_, _) => UpdateStatus();

        ConsoleMirror.LineWritten += line =>
        {
            if (IsDisposed) return;
            try { BeginInvoke(() => AppendLog(line)); } catch { }
        };

        _tray.Icon = Icon;
        _tray.Text = "StreamHost";
        _tray.Visible = false;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Open logs folder", null, (_, _) =>
        {
            var dir = Path.GetDirectoryName(ConsoleMirror.LogFilePath);
            if (dir is not null)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
        });
        trayMenu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = trayMenu;

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                _tray.Visible = true;
                if (_session is not null)
                    _tray.ShowBalloonTip(1500, "StreamHost", "Still streaming — double-click to reopen.", ToolTipIcon.Info);
            }
        };
        FormClosing += (_, _) => { SaveSettings(); _statsTimer.Stop(); _session?.Stop(); _tray.Visible = false; };
        // Closing the panel stops YOUR stream but leaves an open Watch window
        // alive so you can keep viewing; the app exits with its last window.
        // (The message loop is Application.Run() without a form, see Program.)
        FormClosed += (_, _) =>
        {
            if (_watchForm is { IsDisposed: false } watch)
                watch.FormClosed += (_, _) => Application.Exit();
            else
                Application.Exit();
        };

        PopulateSources();
        LoadSettings();
        UpdateLinkBox();
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

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _tray.Visible = false;
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
            _windowCombo.Items.Add($"{w.ProcessName} — {Truncate(w.Title, 58)}");
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
            _audioCombo.Items.Add($"{w.ProcessName} — {Truncate(w.Title, 40)}");
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
            BitrateKbps = preset.Kbps,
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
        StreamSession session;
        try
        {
            session = new StreamSession(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start: {ex.Message}");
            _session = null;
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
            ClientSize = new Size(480, 208),
            BackColor = Bg,
            ForeColor = Fg,
        };
        dlg.HandleCreated += (_, _) => { int on = 1; _ = DwmSetWindowAttribute(dlg.Handle, 20, ref on, sizeof(int)); };

        var rbWin = new RadioButton { Text = "Game / window", AutoSize = true, Checked = _rbWindow.Checked };
        var rbMon = new RadioButton { Text = "Whole monitor", AutoSize = true, Checked = _rbMonitor.Checked };
        var winCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        var monCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        var presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, FlatStyle = FlatStyle.Flat };
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
        rbWin.CheckedChanged += (_, _) =>
        {
            if (audioCombo.Items.Count >= 2)
                audioCombo.Items[1] = rbWin.Checked
                    ? "Captured window's audio"
                    : "No audio (monitor share: pick an app below)";
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(rbWin, 0, 0);
        grid.Controls.Add(winCombo, 1, 0);
        grid.Controls.Add(rbMon, 0, 1);
        grid.Controls.Add(monCombo, 1, 1);
        grid.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 2);
        grid.Controls.Add(presetCombo, 1, 2);
        grid.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 3);
        grid.Controls.Add(audioCombo, 1, 3);

        var ok = new Button { Text = _session is null ? "Start" : "Switch", Width = 96, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(buttons, 0, 4);
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
        AppendLog($"Switching to {config.SourceName} — viewers reconnect automatically.");
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
        _tray.Text = "StreamHost";
        _livePort = 0;
        _fixPortButton.Visible = false;
        UpdateLinkBox();
        _statusLabel.Text = reason is null or "stopped" ? "Not streaming." : $"Stopped: {reason}";
        _statusLabel.ForeColor = reason is null or "stopped" ? Dim : Red;
    }

    private void UpdateStatus()
    {
        var b = _session?.Broadcaster;
        if (b is null || _stopping) return;
        if (b.State == "starting")
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = "Starting — waiting for the first captured frame…";
            return;
        }
        if (_session!.LocalOnly)
        {
            _fixPortButton.Visible = true;
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = $"LIVE, THIS PC ONLY — click Fix access to let viewers reach port {_livePort}";
        }
        else
        {
            _fixPortButton.Visible = false;
            _statusLabel.ForeColor = Green;
            _statusLabel.Text = $"LIVE — {_session.Description} · {EncoderLabel(_session.ActiveEncoder)}   viewers: {b.ViewerCount}   source: {b.SourceFps} fps (dup {b.DupPercent}%)";
        }
        Text = $"StreamHost — LIVE ({b.ViewerCount} watching)";
        _tray.Text = TruncateTray($"StreamHost — LIVE, {b.ViewerCount} watching");
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

    private static string TruncateTray(string s) => s.Length <= 63 ? s : s[..63];

    private string BuildUrl(string pathSuffix)
    {
        // Never hand out a network address the server can't actually answer on.
        var addrs = StreamSession.GetShareAddresses();
        string host = _session?.LocalOnly == true ? "localhost"
            : addrs.Count > 0 ? addrs[0] : "localhost";
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
        _watchForm = new WatchForm((int)_portInput.Value);
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
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;
        AppendLog($"Asking for administrator approval to configure port {port}…");
        var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,
            $"--setup-port {port} --setup-user \"{Environment.UserDomainName}\\{Environment.UserName}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
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
                    if (code == 0)
                    {
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
            catch { }
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
            // The tailscale CLI can block for seconds; keep it off the UI thread.
            string tailnet = await Task.Run(Util.StreamDiscovery.DescribeTailnetPaths);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"StreamHost {AppVersion()}");
            sb.AppendLine($"Windows:  {Environment.OSVersion.VersionString}");
            sb.AppendLine($"GPUs:     {string.Join("; ", GpuAdapters())}");
            sb.AppendLine($"ffmpeg:   {FfmpegVersionLine()}");
            sb.AppendLine($"tailnet:  {tailnet}");
            sb.AppendLine($"enc cache: {ReadSmallFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "encoder.cache"))}");
            sb.AppendLine(_session is { } s
                ? $"session:  {s.Description} via {s.ActiveEncoder}, state {s.Broadcaster?.State}, viewers {s.Broadcaster?.ViewerCount}, localOnly {s.LocalOnly}"
                : "session:  not streaming");
            sb.AppendLine($"settings: {ReadSmallFile(SettingsPath)}");
            sb.AppendLine("---- last 200 log lines ----");
            string[] lines = _logBox.Lines;
            foreach (string line in lines.Skip(Math.Max(0, lines.Length - 200)))
                sb.AppendLine(RedactKey(line));
            Clipboard.SetText(sb.ToString());
            AppendLog("Log copied (with version, system, and encoder info) — paste it into a bug report.");
        }
        catch (Exception ex)
        {
            AppendLog($"Copy log failed: {ex.Message}");
        }
    }

    private static string AppVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "dev";

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
                        list.Add($"{d.Description} (vendor 0x{d.VendorId:X4})");
                }
            }
        }
        catch { }
        if (list.Count == 0) list.Add("unknown");
        return list;
    }

    private static string FfmpegVersionLine()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Encode.FfmpegEncoder.FfmpegPath, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (p is null) return "not found";
            string? line = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return line ?? "no output";
        }
        catch (Exception ex)
        {
            return $"not runnable ({ex.Message})";
        }
    }

    private static string ReadSmallFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Replace("\r", "").Replace('\n', ' ') : "(none)"; }
        catch { return "(unreadable)"; }
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
            int presetIdx = Array.FindIndex(Presets, p => p.Height == s.PresetHeight && p.Fps == s.PresetFps);
            _presetCombo.SelectedIndex = presetIdx >= 0 ? presetIdx : DefaultPresetIndex;
            SelectAudioByKey(s.AudioSource);
            if (s.Port is >= 1024 and <= 65535) _portInput.Value = s.Port;
            if (!string.IsNullOrWhiteSpace(s.StreamName)) _nameInput.Text = s.StreamName;
            int encIdx = Array.FindIndex(Encoders, e => e.Value == s.Encoder);
            _encoderCombo.SelectedIndex = encIdx >= 0 ? encIdx : 0;
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
                AudioSource = SelectedAudioKey(),
                Port = (int)_portInput.Value,
                StreamName = _nameInput.Text.Trim(),
                Encoder = ((EncoderChoice)_encoderCombo.SelectedItem!).Value,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
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
