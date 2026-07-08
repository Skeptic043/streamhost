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
        new("1080p · 30 fps  (~8 Mbps)", 1080, 30, 8000),
        new("1080p · 60 fps  (~12 Mbps)", 1080, 60, 12000),
        new("1440p · 30 fps  (~12 Mbps)", 1440, 30, 12000),
        new("1440p · 60 fps  (~18 Mbps)", 1440, 60, 18000),
        new("Native · 60 fps (~12 Mbps)", 0, 60, 12000),
    ];

    private sealed class AppSettings
    {
        public string SourceKind { get; set; } = "window";
        public string WindowProcess { get; set; } = "";
        public int MonitorIndex { get; set; }
        public int PresetIndex { get; set; } = 1;
        public string AudioSource { get; set; } = "window"; // "none" | "window" | process name
        public int Port { get; set; } = 8093;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "settings.json");

    private readonly RadioButton _rbWindow = new() { Text = "Game / window", Checked = true, AutoSize = true };
    private readonly RadioButton _rbMonitor = new() { Text = "Whole monitor", AutoSize = true };
    private readonly ComboBox _windowCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly Button _refreshButton = new() { Text = "↻", Width = 34 };
    private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
    private readonly Button _watchButton = new() { Text = "Watch streams", Width = 118, Height = 38 };
    private readonly Button _copyLogButton = new() { Text = "Copy log", Width = 82, Height = 24, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly NumericUpDown _portInput = new() { Minimum = 1024, Maximum = 65535, Value = 8093, Width = 80 };
    private readonly Button _startButton = new() { Text = "▶  Start streaming", Width = 160, Height = 38 };
    private readonly Button _copyButton = new() { Text = "Copy link", Width = 92, Height = 38 };
    private readonly Button _gridButton = new() { Text = "Copy grid link", Width = 108, Height = 38 };
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm()
    {
        Text = "StreamHost";
        MinimumSize = new Size(660, 540);
        Size = new Size(680, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = SystemIcons.Application; }

        var sourceGroup = MakeGroup("What to share", 112);
        var sourceGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceGrid.Controls.Add(_rbWindow, 0, 0);
        sourceGrid.Controls.Add(_windowCombo, 1, 0);
        sourceGrid.Controls.Add(_refreshButton, 2, 0);
        sourceGrid.Controls.Add(_rbMonitor, 0, 1);
        sourceGrid.Controls.Add(_monitorCombo, 1, 1);
        sourceGroup.Controls.Add(sourceGrid);

        var settingsGroup = MakeGroup("Quality & options", 96);
        var settingsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        settingsFlow.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(0, 9, 4, 0), ForeColor = Dim });
        settingsFlow.Controls.Add(_presetCombo);
        settingsFlow.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(16, 9, 4, 0), ForeColor = Dim });
        settingsFlow.Controls.Add(_portInput);
        settingsFlow.SetFlowBreak(_portInput, true);
        settingsFlow.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(0, 9, 4, 0), ForeColor = Dim });
        settingsFlow.Controls.Add(_audioCombo);
        settingsGroup.Controls.Add(settingsFlow);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8, 5, 0, 0), BackColor = Bg };
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_copyButton);
        actionPanel.Controls.Add(_gridButton);
        actionPanel.Controls.Add(_watchButton);
        actionPanel.Controls.Add(_linkBox);
        _linkBox.Margin = new Padding(10, 9, 0, 0);
        _linkBox.Width = 180;

        // TableLayout keeps Copy log visible regardless of window size / DPI scaling
        // (the old manual X-position put it off the right edge for everyone).
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, BackColor = Bg, ColumnCount = 2, Padding = new Padding(12, 4, 8, 0) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(_copyLogButton, 1, 0);
        _statusLabel.Anchor = AnchorStyles.Left;
        _copyLogButton.Anchor = AnchorStyles.Right;
        _statusLabel.ForeColor = Dim;

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8), BackColor = Bg };
        logPanel.Controls.Add(_logBox);

        Controls.Add(logPanel);
        Controls.Add(statusPanel);
        Controls.Add(actionPanel);
        Controls.Add(settingsGroup);
        Controls.Add(sourceGroup);

        _presetCombo.Items.AddRange(Presets);
        _presetCombo.SelectedIndex = 1;

        ApplyDarkTheme(this);
        _startButton.BackColor = AccentDark;
        _logBox.BackColor = Color.FromArgb(16, 18, 21);
        _logBox.ForeColor = Color.Gainsboro;
        _linkBox.BackColor = Card;
        _linkBox.ForeColor = Dim;

        // Nothing gets disabled — the selected radio decides which combo is USED.
        _copyLogButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(_logBox.Text.Length > 0 ? _logBox.Text : "(log empty)"); AppendLog("Log copied to clipboard."); }
            catch (Exception ex) { AppendLog($"Clipboard failed: {ex.Message}"); }
        };
        _refreshButton.Click += (_, _) => PopulateSources();
        _windowCombo.DropDown += (_, _) => PopulateWindows(); // fresh list every open
        _startButton.Click += (_, _) => { if (_session is null) StartStream(); else StopStream(); };
        _copyButton.Click += (_, _) => CopyLink(BuildUrl(""));
        _gridButton.Click += (_, _) => CopyLink(BuildUrl("grid"));
        _watchButton.Click += (_, _) => OpenWatchWindow();
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
        trayMenu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Close(); });
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
        _monitors = MonitorEnumerator.GetMonitors();
        _monitorCombo.Items.Clear();
        foreach (var m in _monitors)
            _monitorCombo.Items.Add($"{m.DeviceName}  {m.Width}x{m.Height}{(m.IsPrimary ? "  (primary)" : "")}");
        if (_monitorCombo.Items.Count > 0) _monitorCombo.SelectedIndex = 0;
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
        var preset = (Preset)_presetCombo.SelectedItem!;
        SessionConfig config;

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

        if (_rbWindow.Checked)
        {
            if (_windowCombo.SelectedIndex < 0) { AppendLog("No window selected."); return; }
            var w = _windows[_windowCombo.SelectedIndex];
            config = new SessionConfig
            {
                WindowHandle = w.Handle,
                SourceName = $"window '{w.Title}' [{w.ProcessName}]",
                AudioPid = audioPid,
                Fps = preset.Fps,
                BitrateKbps = preset.Kbps,
                OutHeight = preset.Height,
                Port = (int)_portInput.Value,
            };
        }
        else
        {
            if (_monitorCombo.SelectedIndex < 0) { AppendLog("No monitor selected."); return; }
            var m = _monitors[_monitorCombo.SelectedIndex];
            config = new SessionConfig
            {
                MonitorHandle = m.Handle,
                SourceName = m.DeviceName,
                AudioPid = audioPid,
                Fps = preset.Fps,
                BitrateKbps = preset.Kbps,
                OutHeight = preset.Height,
                Port = (int)_portInput.Value,
            };
        }

        if (config.Port != 8093)
            AppendLog("Note: setup.bat opened port 8093 — other ports need their own urlacl/firewall entries.");

        _retriedCpu = false;
        SaveSettings();
        if (!LaunchSession(config)) return;
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

        session.Stopped += reason =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_session, session)) return; // superseded by a newer session
                    // GPU encoder stalled or died → restart once on the CPU encoder.
                    bool encoderFailed = reason == "encoder-stall" || reason.StartsWith("encoder exited");
                    if (encoderFailed && !_retriedCpu && config.Encoder != "libx264")
                    {
                        _retriedCpu = true;
                        _pendingCpuRetry = true;
                        AppendLog("GPU encoder produced no video — restarting with the CPU encoder (libx264)…");
                        var fallback = config with { Encoder = "libx264" };
                        var t = new System.Windows.Forms.Timer { Interval = 800 }; // let the port release
                        t.Tick += (_, _) =>
                        {
                            t.Stop(); t.Dispose();
                            if (_pendingCpuRetry) { _pendingCpuRetry = false; LaunchSession(fallback); }
                        };
                        t.Start();
                        return;
                    }
                    OnSessionStopped(reason);
                });
            }
            catch { }
        };
        _session = session;
        session.Start();

        _livePort = config.Port;
        _startButton.Text = "■  Stop";
        _startButton.BackColor = Color.FromArgb(104, 58, 58);
        _statsTimer.Start();
        UpdateLinkBox();
        return true;
    }

    private void StopStream()
    {
        _statsTimer.Stop();
        _pendingCpuRetry = false; // cancel any scheduled CPU-encoder fallback
        var session = _session;
        _session = null; // detach first so the Stopped event's identity check fails
        session?.Stop();
        OnSessionStopped(null);
    }

    private void OnSessionStopped(string? reason)
    {
        _statsTimer.Stop();
        _session = null;
        _startButton.Text = "▶  Start streaming";
        _startButton.BackColor = AccentDark;
        Text = "StreamHost";
        _tray.Text = "StreamHost";
        _livePort = 0;
        UpdateLinkBox();
        _statusLabel.Text = reason is null or "stopped" ? "Not streaming." : $"Stopped: {reason}";
        _statusLabel.ForeColor = reason is null or "stopped" ? Dim : Red;
    }

    private void UpdateStatus()
    {
        var b = _session?.Broadcaster;
        if (b is null) return;
        if (b.State == "starting")
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = "Starting — waiting for the first captured frame…";
            return;
        }
        if (_session!.LocalOnly)
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = $"LIVE, THIS PC ONLY — run setup.bat {_livePort} as admin, then restart the stream";
        }
        else
        {
            _statusLabel.ForeColor = Green;
            _statusLabel.Text = $"LIVE — {_session.Description}   viewers: {b.ViewerCount}   source: {b.SourceFps} fps (dup {b.DupPercent}%)";
        }
        Text = $"StreamHost — LIVE ({b.ViewerCount} watching)";
        _tray.Text = TruncateTray($"StreamHost — LIVE, {b.ViewerCount} watching");
    }

    private static string TruncateTray(string s) => s.Length <= 63 ? s : s[..63];

    private string BuildUrl(string pathSuffix)
    {
        // Never hand out a network address the server can't actually answer on.
        var addrs = StreamSession.GetShareAddresses();
        string host = _session?.LocalOnly == true ? "localhost"
            : addrs.Count > 0 ? addrs[0] : "localhost";
        int port = _livePort > 0 ? _livePort : (int)_portInput.Value;
        return $"http://{host}:{port}/{pathSuffix}";
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
        _watchForm = new WatchForm();
        _watchForm.Show();
    }

    private void CopyLink(string url)
    {
        try
        {
            Clipboard.SetText(url);
            AppendLog($"Copied: {url}" + (url.Contains("://100.") ? "  (Tailscale)" : ""));
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard failed ({ex.Message}) — link: {url}");
        }
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
            if (s.PresetIndex >= 0 && s.PresetIndex < Presets.Length) _presetCombo.SelectedIndex = s.PresetIndex;
            SelectAudioByKey(s.AudioSource);
            if (s.Port is >= 1024 and <= 65535) _portInput.Value = s.Port;
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
                PresetIndex = Math.Max(_presetCombo.SelectedIndex, 0),
                AudioSource = SelectedAudioKey(),
                Port = (int)_portInput.Value,
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
