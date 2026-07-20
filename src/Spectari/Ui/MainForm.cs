using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectari.Capture;
using Spectari.Server;
using Spectari.Util;

namespace Spectari.Ui;

/// <summary>
/// The app window: pick a source, pick a preset, Start, copy the link.
/// Dark themed, minimizes to tray, remembers settings.
/// </summary>
public sealed class MainForm : Form
{
    // ---- palette ----------------------------------------------------------
    // Softened: lighter surfaces, lower-contrast text, muted status colors.
    // Rule: NEVER disable a control - WinForms paints disabled text in fixed
    // gray that's unreadable on dark backgrounds. Radios/logic decide what's
    // USED; everything stays clickable and readable.
    // One deliberate exception (v0.15, user request): while LIVE, the pickers
    // that only take effect through a restart are disabled - the dimming is
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
        public string MonitorDeviceName { get; set; } = "";
        public string CaptureDeviceSymbolicLink { get; set; } = "";
        [JsonPropertyName("MonitorIndex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? LegacyMonitorIndex { get; set; }
        // Presets are stored by height+fps, not array index - adding a preset
        // used to silently shift everyone's saved choice.
        public int PresetHeight { get; set; } = 1080;
        public int PresetFps { get; set; } = 60;
        public string BitrateTier { get; set; } = "med"; // "low" | "med" | "high"
        public string AudioSource { get; set; } = "window"; // reserved mode key or process name
        public int Port { get; set; } = 8093;
        public string StreamName { get; set; } = ""; // shown to viewers; empty = machine name
        public string Encoder { get; set; } = "auto";
        // Set only when Open port with "Allow LAN" checked actually succeeded,
        // so the app knows LAN addresses are reachable (not merely requested).
        public bool AllowLan { get; set; }
        // The exact port that Open port last configured. LAN is advertised only
        // when AllowLan is set AND the port in play matches this, so editing the
        // port after a successful fix can't advertise LAN on a port never opened.
        // A missing value deserializes to 0, which matches no real port, so LAN
        // stays off until the next successful Open port on that port.
        public int AllowLanPort { get; set; }
        // Canonical four-component version dismissed from the update notice.
        public string SkipUpdateVersion { get; set; } = "";
    }

    private static readonly string SettingsPath = AppPaths.SettingsFile;
    private const int SourceGroupExpandedHeight = 170;
    private const int SwitchDialogExpandedHeight = 306;

    private readonly GroupBox _sourceGroup;
    private readonly RadioButton _rbWindow = new() { Text = "Game / window", Checked = true, AutoSize = true };
    private readonly RadioButton _rbMonitor = new() { Text = "Monitor", AutoSize = true };
    private readonly RadioButton _rbCaptureDevice = new() { Text = "Capture device", AutoSize = true };
    private readonly ComboBox _windowCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _captureDeviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _bitrateCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _encoderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _audioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _nameInput = new() { Width = 150, MaxLength = 32, BorderStyle = BorderStyle.FixedSingle, Text = Environment.MachineName };
    private readonly Button _watchButton = new() { Text = "Watch streams", Width = 118, Height = 38 };
    // 26px: at 24 the label's descenders (p/y/g) clipped against the border.
    private readonly Button _bundleButton = new() { Text = "Copy log", Width = 82, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button _openLogsButton = new() { Text = "Open logs", Width = 82, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button _fixPortButton = new() { Text = "Open port", Width = 82, Height = 26 };
    // Default off: Tailscale-only is the secure default. The tooltip carries the
    // full scope because the checkbox sits in the compact Misc group.
    private readonly CheckBox _allowLanCheck = new() { Text = "Allow LAN viewers", AutoSize = true, Checked = false, Margin = new Padding(8, 5, 0, 0) };
    private readonly ToolTip _toolTip = new();
    private readonly NumericUpDown _portInput = new() { Minimum = 1024, Maximum = 65535, Value = 8093, Width = 80 };
    private readonly Button _startButton = new() { Text = "▶  Start streaming", Width = 160, Height = 38 };
    private readonly Button _copyButton = new() { Text = "Copy link", Width = 68, Height = 38, Margin = new Padding(3, 3, 0, 3) };
    private readonly Button _copyMenuButton = new() { Text = "v", Width = 24, Height = 38, Margin = new Padding(0, 3, 3, 3), AccessibleName = "Copy link options" };
    private readonly Button _switchButton = new() { Text = "Switch source", Width = 112, Height = 38 };
    private readonly TextBox _linkBox = new() { ReadOnly = true, Width = 260, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _statusLabel = new() { Text = "Not streaming.", AutoSize = true };
    private readonly TableLayoutPanel _updatePanel = new()
    {
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        Height = 36, Visible = false, BackColor = Card,
        ColumnCount = 3, Padding = new Padding(12, 4, 8, 4),
    };
    private readonly Label _updateLabel = new() { AutoSize = true, ForeColor = Fg, Anchor = AnchorStyles.Left };
    private readonly Button _viewReleaseButton = new() { Text = "View release", Width = 88, Height = 26, Anchor = AnchorStyles.Right };
    private readonly Button _dismissUpdateButton = new() { Text = "Dismiss", Width = 70, Height = 26, Anchor = AnchorStyles.Right };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.None,
    };
    private readonly TableLayoutPanel _lowerPanel = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
        Padding = new Padding(8, 4, 8, 8), BackColor = Bg,
    };
    private readonly Panel _previewPanel = new()
    {
        Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0),
        BackColor = Color.FromArgb(16, 18, 21), BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly PictureBox _previewBox = new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(16, 18, 21), AccessibleName = "Selected source preview",
    };
    private readonly Label _previewPlaceholder = new()
    {
        Dock = DockStyle.Fill, Text = "Waiting for preview...", TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Dim, BackColor = Color.FromArgb(16, 18, 21), Padding = new Padding(12),
    };
    private readonly Panel _logPanel = new()
    {
        Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0),
        BackColor = Color.FromArgb(16, 18, 21),
    };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _previewDebounceTimer = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _previewPollTimer = new() { Interval = 500 };

    private readonly SourceSelectionModel _sourceSelection = new();
    private readonly HostAccessService _hostAccess;
    private readonly ShareLinkResolver _shareLinks;
    private readonly StreamController _streamController;
    private readonly CaptureDeviceChangeMonitor _captureDeviceChanges;
    private WatchForm? _watchForm;
    private int _livePort; // pinned while streaming so link/copy ignore edits to the port box
    private string _savedBitrateTier = "med"; // from settings, applied on the first populate
    private string _persistedCaptureDeviceSymbolicLink = "";
    private bool _refreshingSourceOptions; // suppresses preset events caused by Native relabeling

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
    private static extern bool IsIconic(IntPtr hwnd);

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
    private readonly IdlePreviewCapture _idlePreviewCapture = new();
    private UiHangWatchdog? _uiHangWatchdog;
    private IntPtr _previewWaitingWindow;
    private bool _previewPausedForForm = true;
    private bool _closing;

    // Held so it can be unsubscribed on close: ConsoleMirror.LineWritten is a
    // STATIC event, and AppRunContext recreates this form, so an anonymous
    // lambda would root every dead form forever and fan each log line out to all
    // of them.
    private Action<string>? _logHandler;
    private readonly CancellationTokenSource _updateCheckCts = new();
    private string? _availableUpdateVersion;
    private string? _skippedUpdateVersion;

    public MainForm()
    {
        Console.WriteLine("[boot] form constructor enter");
        Text = "Spectari";
        MinimumSize = new Size(680, 600);
        Size = new Size(840, 690);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = SystemIcons.Application; }

        // Audio lives with the source pickers: what you share and what it sounds
        // like are one decision. Name/port are plumbing, off to their own box.
        _sourceGroup = MakeGroup("What to share", SourceGroupExpandedHeight);
        var sourceGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceGrid.Controls.Add(_rbWindow, 0, 0);
        sourceGrid.Controls.Add(_windowCombo, 1, 0);
        sourceGrid.Controls.Add(_rbMonitor, 0, 1);
        sourceGrid.Controls.Add(_monitorCombo, 1, 1);
        sourceGrid.Controls.Add(_rbCaptureDevice, 0, 2);
        sourceGrid.Controls.Add(_captureDeviceCombo, 1, 2);
        sourceGrid.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 3);
        sourceGrid.Controls.Add(_audioCombo, 1, 3);
        _audioCombo.Width = 330;
        _sourceGroup.Controls.Add(sourceGrid);

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
        var miscGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        miscGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        miscGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        miscGrid.Controls.Add(new Label { Text = "Name:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 0);
        miscGrid.Controls.Add(_nameInput, 1, 0);
        miscGrid.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 1);
        miscGrid.Controls.Add(_portInput, 1, 1);
        miscGrid.Controls.Add(new Label { Text = "Access:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, 2);
        var portAccessGroup = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0), BackColor = Bg,
        };
        portAccessGroup.Controls.Add(_fixPortButton);
        portAccessGroup.Controls.Add(_allowLanCheck);
        miscGrid.Controls.Add(portAccessGroup, 1, 2);
        _nameInput.Margin = new Padding(3, 5, 3, 0);
        _toolTip.SetToolTip(_allowLanCheck,
            "Also allow devices on your local network, not just Tailscale, to reach this stream");
        _toolTip.SetToolTip(_fixPortButton,
            "Asks for administrator approval once and opens the current port for viewers (Tailscale-only unless Allow LAN is checked)");
        miscGroup.Controls.Add(miscGrid);

        optionsRow.Controls.Add(qualityGroup, 0, 0);
        optionsRow.Controls.Add(miscGroup, 1, 0);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8, 5, 0, 0), BackColor = Bg };
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_switchButton);
        actionPanel.Controls.Add(_copyButton);
        actionPanel.Controls.Add(_copyMenuButton);
        actionPanel.Controls.Add(_watchButton);
        actionPanel.Controls.Add(_linkBox);
        _linkBox.Margin = new Padding(10, 9, 0, 0);
        _linkBox.Width = 180;

        // TableLayout keeps the right-side buttons visible regardless of window
        // size / DPI scaling (manual X-positions drifted off the edge).
        // Height/bottom padding leave room for the status text's descenders and a
        // couple px of gap above the log box (the amber LIVE line sat flush to it).
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, BackColor = Bg, ColumnCount = 3, Padding = new Padding(12, 4, 8, 2) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(_openLogsButton, 1, 0);
        statusPanel.Controls.Add(_bundleButton, 2, 0);
        _toolTip.SetToolTip(_openLogsButton, "Open the folder containing the log file");
        _statusLabel.Anchor = AnchorStyles.Left;
        _openLogsButton.Anchor = AnchorStyles.Right;
        _bundleButton.Anchor = AnchorStyles.Right;
        _statusLabel.ForeColor = Dim;

        _updatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _updatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _updatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _updatePanel.Controls.Add(_updateLabel, 0, 0);
        _updatePanel.Controls.Add(_viewReleaseButton, 1, 0);
        _updatePanel.Controls.Add(_dismissUpdateButton, 2, 0);

        _previewPanel.Controls.Add(_previewBox);
        _previewPanel.Controls.Add(_previewPlaceholder);
        _previewPlaceholder.BringToFront();
        _logPanel.Controls.Add(_logBox);
        _lowerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        _lowerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        _lowerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _lowerPanel.Controls.Add(_previewPanel, 0, 0);
        _lowerPanel.Controls.Add(_logPanel, 1, 0);

        Controls.Add(_lowerPanel);
        Controls.Add(statusPanel);
        Controls.Add(_updatePanel);
        Controls.Add(actionPanel);
        Controls.Add(optionsRow);
        Controls.Add(_sourceGroup);

        _presetCombo.Items.AddRange(Presets);
        _presetCombo.SelectedIndex = DefaultPresetIndex;
        PopulateEncoderChoices();

        ApplyDarkTheme(this);
        _startButton.BackColor = AccentDark;
        _logBox.BackColor = Color.FromArgb(16, 18, 21);
        _logBox.ForeColor = Color.Gainsboro;
        _linkBox.BackColor = Card;
        _linkBox.ForeColor = Dim;

        _hostAccess = new HostAccessService(
            action =>
            {
                if (_closing || IsDisposed || Disposing) return;
                BeginInvoke(action);
            },
            ConsoleMirror.WriteClassifiedLine);
        _shareLinks = new ShareLinkResolver(_hostAccess);
        _hostAccess.SetupStarted += _ => SetHostAccessControlsEnabled(false);
        _hostAccess.SetupCompleted += RenderHostAccessSetupResult;

        _streamController = new StreamController(new StreamControllerHooks(
            AcquireStreamStartFenceAsync,
            StopIdleServer,
            action =>
            {
                if (IsDisposed || Disposing) return;
                BeginInvoke(action);
            },
            name => _uiHangWatchdog?.TrackOperation(name),
            ConsoleMirror.WriteClassifiedLine));
        _streamController.StateChanged += RenderStreamState;
        _streamController.SessionStarted += RenderStartedStream;
        _captureDeviceChanges = new CaptureDeviceChangeMonitor();
        _captureDeviceChanges.DevicesChanged += RefreshCaptureDevicesFromNotification;

        // Nothing gets disabled - the selected radio decides which combo is USED.
        _windowCombo.DropDown += (_, _) => PopulateWindows();
        _monitorCombo.DropDown += (_, _) => PopulateMonitors();
        _captureDeviceCombo.DropDown += (_, _) => PopulateCaptureDevices();
        void SelectSourceKind(SourceKind kind, RadioButton radio)
        {
            if (!radio.Checked) return;
            _sourceSelection.SelectKind(kind);
            RenderAudioPicker(_sourceSelection, _audioCombo);
            RefreshSourceOptions();
            ScheduleIdlePreviewRefresh();
        }
        _rbWindow.CheckedChanged += (_, _) => SelectSourceKind(SourceKind.Window, _rbWindow);
        _rbMonitor.CheckedChanged += (_, _) => SelectSourceKind(SourceKind.Monitor, _rbMonitor);
        _rbCaptureDevice.CheckedChanged += (_, _) =>
            SelectSourceKind(SourceKind.CaptureDevice, _rbCaptureDevice);
        _windowCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _windowCombo.SelectionChangeCommitted += (_, _) =>
        {
            bool changed = _sourceSelection.SelectWindowIndex(_windowCombo.SelectedIndex);
            RefreshSourceOptions();
            if (changed && _rbWindow.Checked) ScheduleIdlePreviewRefresh();
        };
        _monitorCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _monitorCombo.SelectionChangeCommitted += (_, _) =>
        {
            bool changed = _sourceSelection.SelectMonitorIndex(_monitorCombo.SelectedIndex);
            RefreshSourceOptions();
            if (changed && _rbMonitor.Checked) ScheduleIdlePreviewRefresh();
        };
        _captureDeviceCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        _captureDeviceCombo.SelectionChangeCommitted += (_, _) =>
        {
            bool changed = _sourceSelection.SelectCaptureDeviceIndex(_captureDeviceCombo.SelectedIndex);
            RefreshSourceOptions();
            if (changed && _rbCaptureDevice.Checked) ScheduleIdlePreviewRefresh();
        };
        _audioCombo.SelectedIndexChanged += (_, _) => _sourceSelection.SelectAudioIndex(_audioCombo.SelectedIndex);
        _presetCombo.SelectedIndexChanged += (_, _) => RefreshSourceOptions();
        // The bitrate combo is the single source of truth for the chosen tier:
        // remember every user pick so it survives repopulates (source/preset
        // changes re-fire PopulateBitrateOptions) and app restarts. Guarded so the
        // transient -1 during an Items.Clear repopulate can't wipe the saved tier.
        _bitrateCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_bitrateCombo.SelectedItem is BitrateChoice bc) _savedBitrateTier = bc.Tier;
        };
        _startButton.Click += async (_, _) =>
        {
            if (_streamController.HasSession || _streamController.IsCpuRetryPending)
                _streamController.Stop();
            else
                await StartStreamAsync();
        };
        _switchButton.Click += (_, _) => ShowSwitchDialog();
        _copyButton.Click += (_, _) => CopyLink(
            _shareLinks.ResolvePrimaryUrl(CurrentShareLinkContext(), ""));
        var copyMenu = new ContextMenuStrip
        {
            BackColor = Card,
            ForeColor = Fg,
            ShowImageMargin = false,
        };
        var copyLanItem = new ToolStripMenuItem("Copy LAN link") { BackColor = Card, ForeColor = Fg };
        copyLanItem.Click += (_, _) => CopyLanLink();
        copyMenu.Items.Add(copyLanItem);
        _copyMenuButton.Click += (_, _) => copyMenu.Show(_copyMenuButton, new Point(0, _copyMenuButton.Height));
        _watchButton.Click += (_, _) => OpenWatchWindow();
        _bundleButton.Click += (_, _) => CopySupportBundle();
        _openLogsButton.Click += (_, _) => OpenLogsFolder();
        _viewReleaseButton.Click += (_, _) => ViewLatestRelease();
        _dismissUpdateButton.Click += (_, _) => DismissUpdate();
        _fixPortButton.Click += (_, _) => FixPortAccess();
        _portInput.ValueChanged += (_, _) => { UpdateLinkBox(); RestartIdleServer(); };
        _nameInput.Leave += (_, _) => RestartIdleServer(); // idle stats carry the name
        _statsTimer.Tick += (_, _) => UpdateStatus();
        _idleRetryTimer.Tick += (_, _) => StartIdleServer();
        _previewDebounceTimer.Tick += (_, _) => StartIdlePreview();
        _previewPollTimer.Tick += (_, _) => PollIdlePreview();
        VisibleChanged += (_, _) => SyncIdlePreviewVisibility();
        Resize += (_, _) => SyncIdlePreviewVisibility();

        int logUiThreadId = Environment.CurrentManagedThreadId;
        _logHandler = line =>
        {
            if (IsDisposed) return;
            try
            {
                if (Environment.CurrentManagedThreadId == logUiThreadId) AppendLogToUi(line);
                else BeginInvoke(() => AppendLogToUi(line));
            }
            catch { }
        };
        ConsoleMirror.LineWritten += _logHandler;

        // Closing the panel stops YOUR stream but leaves an open Watch window
        // alive so you can keep viewing. AppRunContext owns app exit (it ends
        // when the last user window closes) and can bring this panel back on a
        // second launch, so there is no Application.Exit wiring here.
        FormClosing += (_, _) =>
        {
            _closing = true;
            _captureDeviceChanges.Dispose();
            _updateCheckCts.Cancel();
            _updateCheckCts.Dispose();
            // Drop the static-event subscription first so this recreated-then-
            // closed form doesn't stay rooted receiving log lines forever.
            if (_logHandler is not null) ConsoleMirror.LineWritten -= _logHandler;
            SaveSettings();
            _statsTimer.Stop();
            StopIdlePreview(hideLayout: true);
            _idlePreviewCapture.Dispose();
            // Fail closed: if teardown wedges past the join timeout and a Watch
            // window keeps the process alive, the detached session could keep
            // streaming with no handle left to stop it. Take the whole app down
            // (the ChildJob job object reaps ffmpeg on process exit); this also
            // closes any open Watch window, a cost accepted over an invisible stream.
            using IDisposable? stopOperation = _uiHangWatchdog?.TrackOperation("stream stop UI phase");
            if (!_streamController.StopForClose())
            {
                Console.WriteLine("[shutdown] stream teardown did not finish in time; closing the app to avoid an invisible stream.");
                Environment.Exit(1);
            }
            StopIdleServer();
            // These timers and the tooltip are not in a components container, so
            // nothing else disposes them.
            _streamController.Dispose();
            _statsTimer.Dispose();
            _idleRetryTimer.Dispose();
            _previewDebounceTimer.Dispose();
            _previewPollTimer.Dispose();
            _toolTip.Dispose();
            _uiHangWatchdog?.Dispose();
            _uiHangWatchdog = null;
        };

        Console.WriteLine("[boot] source enumeration start");
        PopulateSources();
        if (_sourceSelection.CaptureDevices.Count == 0)
            Console.WriteLine("[boot] no capture devices detected; capture source hidden");
        Console.WriteLine(
            $"[boot] source enumeration complete: {_sourceSelection.Windows.Count} windows, " +
            $"{_sourceSelection.Monitors.Count} monitors, {_sourceSelection.CaptureDevices.Count} capture devices");
        LoadSettings();
        Console.WriteLine("[boot] update check start");
        _ = CheckForUpdatesAsync();
        UpdateLinkBox();
        RefreshSourceOptions();
        SetPreviewLayoutVisible(true);
        StartIdleServer();
        Console.WriteLine("[boot] form constructor exit");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        _uiHangWatchdog ??= new UiHangWatchdog(this, _idlePreviewCapture.GetProgressSnapshot);
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

    private bool IsTrueIdle => _streamController.IsIdleSurface;

    private bool CanRunIdlePreview =>
        !_closing && IsTrueIdle && Visible && WindowState != FormWindowState.Minimized;

    /// <summary>Debounce source-picker churn and release the old source at once,
    /// so the preview never shows a stale pick while the new one settles.</summary>
    private void ScheduleIdlePreviewRefresh()
    {
        if (_closing) return;
        if (!CanRunIdlePreview)
        {
            if (!IsTrueIdle) StopIdlePreview(hideLayout: true);
            return;
        }

        SetPreviewLayoutVisible(true);
        StopIdlePreview(placeholder: "Loading preview...");
        _previewDebounceTimer.Start();
    }

    private async void StartIdlePreview()
    {
        _previewDebounceTimer.Stop();
        if (!CanRunIdlePreview) return;

        StopIdlePreview(placeholder: "Waiting for preview...");
        SetPreviewLayoutVisible(true);
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("preview start");

        IdlePreviewStartResult startResult;
        try
        {
            Console.WriteLine(
                $"[preview] start pick: kind={_sourceSelection.Kind.ToString().ToLowerInvariant()} " +
                $"windowIndex={_sourceSelection.SelectedWindowIndex} " +
                $"monitorIndex={_sourceSelection.SelectedMonitorIndex} " +
                $"captureDeviceIndex={_sourceSelection.SelectedCaptureDeviceIndex}");
            if (_sourceSelection.Kind == SourceKind.Window)
            {
                WindowDescription? window = _sourceSelection.SelectedWindow;
                if (window is null)
                {
                    SetPreviewPlaceholder("Select a source to preview.");
                    return;
                }

                IntPtr handle = window.Handle;
                if (!IsWindow(handle))
                {
                    SetPreviewPlaceholder("Source is closed or unavailable.");
                    return;
                }

                if (IsIconic(handle))
                {
                    _previewWaitingWindow = handle;
                    SetPreviewPlaceholder("Window is minimized.");
                    _previewPollTimer.Start();
                    return;
                }

                startResult = await _idlePreviewCapture.StartForWindowAsync(handle);
            }
            else if (_sourceSelection.Kind == SourceKind.Monitor)
            {
                MonitorDescription? monitor = _sourceSelection.SelectedMonitor;
                if (monitor is null)
                {
                    SetPreviewPlaceholder("Select a source to preview.");
                    return;
                }

                startResult = await _idlePreviewCapture.StartForMonitorAsync(monitor.Handle);
            }
            else
            {
                CaptureDeviceDescription? captureDevice = _sourceSelection.SelectedCaptureDevice;
                if (captureDevice is null)
                {
                    SetPreviewPlaceholder("Select a source to preview.");
                    return;
                }

                startResult = await _idlePreviewCapture.StartForCaptureDeviceAsync(
                    captureDevice.SymbolicLink);
            }

            IdlePreviewStartState startState = startResult.State;
            if (startState == IdlePreviewStartState.Canceled) return;
            if (startState is IdlePreviewStartState.TimedOut or IdlePreviewStartState.Failed)
            {
                SetPreviewPlaceholder(startResult.FailureMessage ?? "Preview unavailable.");
                return;
            }

            if (!CanRunIdlePreview)
            {
                _idlePreviewCapture.Stop();
                return;
            }

            _previewPollTimer.Start();
            PollIdlePreview();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[preview] start failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            StopIdlePreview(placeholder: "Preview unavailable.");
        }
    }

    private void PollIdlePreview()
    {
        if (!CanRunIdlePreview)
        {
            SyncIdlePreviewVisibility();
            return;
        }

        if (!_idlePreviewCapture.IsReady)
        {
            if (_previewWaitingWindow == IntPtr.Zero) return;
            if (!IsWindow(_previewWaitingWindow))
            {
                _previewWaitingWindow = IntPtr.Zero;
                _previewPollTimer.Stop();
                SetPreviewPlaceholder("Source is closed or unavailable.");
            }
            else if (!IsIconic(_previewWaitingWindow))
            {
                _previewWaitingWindow = IntPtr.Zero;
                StartIdlePreview();
            }
            return;
        }

        IdlePreviewPollResult result;
        try { result = _idlePreviewCapture.Poll(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[preview] frame poll failed: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            result = new IdlePreviewPollResult(IdlePreviewPollState.Unavailable);
        }

        switch (result.State)
        {
            case IdlePreviewPollState.Frame when result.Image is not null:
                SetPreviewImage(result.Image);
                break;
            case IdlePreviewPollState.Minimized:
                SetPreviewPlaceholder("Window is minimized.");
                break;
            case IdlePreviewPollState.Unavailable:
                _previewPollTimer.Stop();
                _idlePreviewCapture.Stop();
                SetPreviewPlaceholder(
                    _sourceSelection.Kind == SourceKind.CaptureDevice
                        ? "Capture device unavailable."
                        : "Source is closed or unavailable.");
                break;
        }
    }

    /// <summary>Pause means release, not merely stop painting. Restore creates a
    /// fresh WGC session after the same source debounce used by picker changes.</summary>
    private void SyncIdlePreviewVisibility()
    {
        if (_closing) return;
        bool paused = !Visible || WindowState == FormWindowState.Minimized;
        if (paused)
        {
            _previewPausedForForm = true;
            StopIdlePreview(placeholder: "Preview paused while the window is hidden.");
            return;
        }

        if (!_previewPausedForForm) return;
        _previewPausedForForm = false;
        ResumeIdlePreview();
    }

    private void ResumeIdlePreview()
    {
        if (_closing || !IsTrueIdle) return;
        SetPreviewLayoutVisible(true);
        if (Visible && WindowState != FormWindowState.Minimized)
            ScheduleIdlePreviewRefresh();
    }

    private void StopIdlePreview(bool hideLayout = false, string placeholder = "Preview available while idle.")
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("preview stop");
        _previewDebounceTimer.Stop();
        _previewPollTimer.Stop();
        _previewWaitingWindow = IntPtr.Zero;

        _idlePreviewCapture.Stop();
        SetPreviewPlaceholder(placeholder);
        if (hideLayout) SetPreviewLayoutVisible(false);
    }

    private void SetPreviewImage(Bitmap image)
    {
        Image? old = _previewBox.Image;
        _previewBox.Image = image;
        _previewPlaceholder.Visible = false;
        old?.Dispose();
    }

    private void SetPreviewPlaceholder(string text)
    {
        Image? old = _previewBox.Image;
        _previewBox.Image = null;
        old?.Dispose();
        _previewPlaceholder.Text = text;
        _previewPlaceholder.Visible = true;
        _previewPlaceholder.BringToFront();
    }

    private void SetPreviewLayoutVisible(bool visible)
    {
        _lowerPanel.SuspendLayout();
        _previewPanel.Visible = visible;
        _lowerPanel.ColumnStyles[0].SizeType = visible ? SizeType.Percent : SizeType.Absolute;
        _lowerPanel.ColumnStyles[0].Width = visible ? 35 : 0;
        _lowerPanel.ColumnStyles[1].SizeType = SizeType.Percent;
        _lowerPanel.ColumnStyles[1].Width = visible ? 65 : 100;
        _logPanel.Margin = visible ? new Padding(4, 0, 0, 0) : Padding.Empty;
        _lowerPanel.ResumeLayout();
    }

    private void PopulateSources()
    {
        PopulateWindows();
        PopulateMonitors();
        PopulateCaptureDevices();
    }

    private void RenderMainCaptureDeviceRow()
    {
        bool visible = _sourceSelection.CaptureDevices.Count > 0;
        _sourceGroup.SuspendLayout();
        _rbCaptureDevice.Visible = visible;
        _captureDeviceCombo.Visible = visible;
        _sourceGroup.Height = SourceGroupExpandedHeight -
            (visible ? 0 : PickerRowHeight(_captureDeviceCombo));
        if (visible)
        {
            _rbCaptureDevice.Enabled = _rbWindow.Enabled;
            _captureDeviceCombo.Enabled = _windowCombo.Enabled;
        }
        _sourceGroup.ResumeLayout(performLayout: true);
    }

    private static int PickerRowHeight(ComboBox combo) =>
        combo.PreferredHeight + combo.Margin.Vertical;

    private void PopulateMonitors()
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("monitor source repopulation");
        _sourceSelection.RefreshMonitors();
        RenderPicker(_monitorCombo, _sourceSelection.MonitorDisplayItems, _sourceSelection.SelectedMonitorIndex);
    }

    private void PopulateWindows()
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("window source repopulation");
        _sourceSelection.RefreshWindows();
        RenderPicker(_windowCombo, _sourceSelection.WindowDisplayItems, _sourceSelection.SelectedWindowIndex);
        RenderAudioPicker(_sourceSelection, _audioCombo);
    }

    private void PopulateCaptureDevices()
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("capture device repopulation");
        _sourceSelection.RefreshCaptureDevices();
        RenderPicker(
            _captureDeviceCombo,
            _sourceSelection.CaptureDeviceDisplayItems,
            _sourceSelection.SelectedCaptureDeviceIndex);
        RenderMainCaptureDeviceRow();
    }

    private void RefreshCaptureDevicesFromNotification(
        IReadOnlyList<CaptureDeviceDescription> devices)
    {
        if (_closing || IsDisposed || Disposing) return;
        _sourceSelection.RefreshCaptureDevices(devices);
        RenderMainSourceKind();
        RenderPicker(
            _captureDeviceCombo,
            _sourceSelection.CaptureDeviceDisplayItems,
            _sourceSelection.SelectedCaptureDeviceIndex);
        RenderMainCaptureDeviceRow();
    }

    private static void RenderSourcePickers(
        SourceSelectionModel sourceSelection,
        ComboBox windowCombo,
        ComboBox monitorCombo,
        ComboBox captureDeviceCombo,
        ComboBox audioCombo)
    {
        RenderPicker(windowCombo, sourceSelection.WindowDisplayItems, sourceSelection.SelectedWindowIndex);
        RenderPicker(monitorCombo, sourceSelection.MonitorDisplayItems, sourceSelection.SelectedMonitorIndex);
        RenderPicker(
            captureDeviceCombo,
            sourceSelection.CaptureDeviceDisplayItems,
            sourceSelection.SelectedCaptureDeviceIndex);
        RenderAudioPicker(sourceSelection, audioCombo);
    }

    private void RenderMainSourceKind()
    {
        _rbWindow.Checked = _sourceSelection.Kind == SourceKind.Window;
        _rbMonitor.Checked = _sourceSelection.Kind == SourceKind.Monitor;
        _rbCaptureDevice.Checked = _sourceSelection.Kind == SourceKind.CaptureDevice;
    }

    private static void RenderAudioPicker(SourceSelectionModel sourceSelection, ComboBox audioCombo) =>
        RenderPicker(audioCombo, sourceSelection.AudioDisplayItems, sourceSelection.SelectedAudioIndex);

    private static void RenderPicker(ComboBox combo, IReadOnlyList<string> items, int selectedIndex)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (string item in items) combo.Items.Add(item);
        combo.SelectedIndex = selectedIndex >= 0 && selectedIndex < items.Count ? selectedIndex : -1;
        combo.EndUpdate();
    }

    private async Task StartStreamAsync()
    {
        // Reuse the pending key so links copied while idle (they carry ?k=this)
        // stay valid the moment the stream goes live. Rotated after a live stop.
        var config = BuildConfigFromUi((int)_portInput.Value, _pendingKey);
        if (config is null)
        {
            ResumeIdlePreview();
            return;
        }

        if (config.Port != 8093)
            AppendLog("Note: setup.bat / Open port configure one port at a time; other ports need their own run.");

        SaveSettings();
        await _streamController.StartAsync(config);
    }

    /// <summary>Reads the whole UI into a SessionConfig, or null (with a log line)
    /// when no source is selected. Shared by Start, Switch source, and restarts.</summary>
    private SessionConfig? BuildConfigFromUi(int port, string? viewKey)
    {
        var preset = (Preset)_presetCombo.SelectedItem!;

        // Resolve the audio source: none / follow captured window / desktop / a specific app.
        // "Captured window's audio" during a monitor share resolves to no audio.
        uint audioPid = _sourceSelection.SelectedAudioPid;
        if (_sourceSelection.IsSelectedAudioProcessUnavailable)
            AppendLog("[audio] The selected audio source is not running; streaming without audio.");

        IntPtr windowHandle = IntPtr.Zero, monitorHandle = IntPtr.Zero;
        string captureDeviceSymbolicLink = "";
        string sourceName;
        if (_sourceSelection.Kind == SourceKind.Window)
        {
            WindowDescription? w = _sourceSelection.SelectedWindow;
            if (w is null) { AppendLog("No window selected."); return null; }
            if (!IsWindow(w.Handle))
            {
                AppendLog("The selected window no longer exists. Pick another source.");
                PopulateSources();
                return null;
            }
            windowHandle = w.Handle;
            sourceName = $"window '{w.Title}' [{w.ProcessName}]";
        }
        else if (_sourceSelection.Kind == SourceKind.Monitor)
        {
            MonitorDescription? m = _sourceSelection.SelectedMonitor;
            if (m is null) { AppendLog("No monitor selected."); return null; }
            monitorHandle = m.Handle;
            sourceName = m.DeviceName;
        }
        else
        {
            CaptureDeviceDescription? device = _sourceSelection.SelectedCaptureDevice;
            if (device is null) { AppendLog("No capture device selected."); return null; }
            captureDeviceSymbolicLink = device.SymbolicLink;
            string friendlyName = string.IsNullOrWhiteSpace(device.FriendlyName)
                ? "Capture device"
                : device.FriendlyName.Trim();
            sourceName = $"capture device '{friendlyName}'";
        }

        return new SessionConfig
        {
            WindowHandle = windowHandle,
            MonitorHandle = monitorHandle,
            CaptureDeviceSymbolicLink = captureDeviceSymbolicLink,
            SourceName = sourceName,
            StreamName = _nameInput.Text.Trim(),
            AudioPid = audioPid,
            CaptureDesktopAudio = _sourceSelection.IsDesktopAudioSelected,
            Fps = preset.Fps,
            BitrateKbps = (_bitrateCombo.SelectedItem as BitrateChoice)?.Kbps ?? 0, // 0 = session auto (Medium)
            OutHeight = preset.Height,
            Port = port,
            Encoder = ((EncoderChoice)_encoderCombo.SelectedItem!).Value,
            ViewKey = viewKey,
        };
    }

    private async Task<IDisposable?> AcquireStreamStartFenceAsync()
    {
        _previewDebounceTimer.Stop();
        _previewPollTimer.Stop();
        _previewWaitingWindow = IntPtr.Zero;
        SetPreviewPlaceholder("Preview available while idle.");
        SetPreviewLayoutVisible(false);
        return await _idlePreviewCapture.AcquireStreamStartFenceAsync();
    }

    private void RenderStreamState(StreamControllerStateChange change)
    {
        switch (change.Current)
        {
            case StreamControllerState.Stopping:
                _statsTimer.Stop();
                _startButton.Text = "…  Stopping";
                _statusLabel.Text = "Stopping…";
                _statusLabel.ForeColor = Color.Goldenrod;
                break;
            case StreamControllerState.Switching:
                _statsTimer.Stop();
                _statusLabel.Text = change.Trigger == StreamController.SourceSwitchTrigger
                    ? "Switching source…"
                    : "Restarting…";
                _statusLabel.ForeColor = Color.Goldenrod;
                break;
            case StreamControllerState.Idle or StreamControllerState.Failed
                when change.RenderCompletion:
                RenderStoppedStream(change.Reason, change.WentLive);
                break;
        }
    }

    private void RenderStartedStream(SessionConfig config)
    {
        _livePort = config.Port;
        _startButton.Text = "■  Stop";
        _startButton.BackColor = Color.FromArgb(104, 58, 58);
        SetLiveLock(true);
        _statsTimer.Start();
        UpdateLinkBox();
    }

    private void RenderStoppedStream(string? reason, bool wentLive)
    {
        _statsTimer.Stop();
        _startButton.Text = "▶  Start streaming";
        _startButton.BackColor = AccentDark;
        Text = "Spectari";
        _livePort = 0;
        SetLiveLock(false);
        // A live run's viewer key must be rotated before idle links render.
        if (wentLive) _pendingKey = SessionConfig.NewViewKey();
        UpdateLinkBox();
        _statusLabel.Text = reason is null or "stopped" ? "Not streaming." : $"Stopped: {reason}";
        _statusLabel.ForeColor = reason is null or "stopped" ? Dim : Red;
        StartIdleServer();
        ResumeIdlePreview();
    }

    /// <summary>Guided switch: a small popup with the source, preset, bitrate,
    /// encoder, and audio pickers, prefilled from the current selections. OK writes the
    /// choices back to the main controls and goes through the normal switch
    /// (or plain start when idle), so both paths stay one code path.</summary>
    private async void ShowSwitchDialog()
    {
        if (_streamController.IsStopping) return;

        SourceSelectionModel dialogSources;
        using (_uiHangWatchdog?.TrackOperation("source dialog repopulation"))
            dialogSources = _sourceSelection.CreateFreshSnapshot();
        bool showCaptureDeviceRow = dialogSources.CaptureDevices.Count > 0;

        using var dlg = new Form
        {
            Text = "Switch source",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, SwitchDialogExpandedHeight),
            BackColor = Bg,
            ForeColor = Fg,
        };
        dlg.HandleCreated += (_, _) => { int on = 1; _ = DwmSetWindowAttribute(dlg.Handle, 20, ref on, sizeof(int)); };

        var rbWin = new RadioButton { Text = "Game / window", AutoSize = true, Checked = dialogSources.Kind == SourceKind.Window };
        var rbMon = new RadioButton { Text = "Monitor", AutoSize = true, Checked = dialogSources.Kind == SourceKind.Monitor };
        using var rbCaptureDevice = new RadioButton
        {
            Text = "Capture device",
            AutoSize = true,
            Checked = showCaptureDeviceRow && dialogSources.Kind == SourceKind.CaptureDevice,
            Visible = showCaptureDeviceRow,
        };
        var winCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        var monCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };
        using var captureDeviceCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 330,
            FlatStyle = FlatStyle.Flat,
            Visible = showCaptureDeviceRow,
        };
        var presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, FlatStyle = FlatStyle.Flat };
        var bitrateCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, FlatStyle = FlatStyle.Flat };
        var encoderCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, FlatStyle = FlatStyle.Flat };
        var audioCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, FlatStyle = FlatStyle.Flat };

        RenderSourcePickers(dialogSources, winCombo, monCombo, captureDeviceCombo, audioCombo);
        foreach (object it in _presetCombo.Items) presetCombo.Items.Add(it);
        foreach (object it in _encoderCombo.Items) encoderCombo.Items.Add(it);
        presetCombo.SelectedIndex = _presetCombo.SelectedIndex;
        encoderCombo.SelectedIndex = _encoderCombo.SelectedIndex;
        // Refresh the dialog's Native labels and bitrate choices from one immutable
        // enumerated size snapshot, matching the main controls.
        bool refreshingDlgSourceOptions = false;
        void RefreshDlgSourceOptions()
        {
            if (refreshingDlgSourceOptions) return;
            refreshingDlgSourceOptions = true;
            try
            {
                var sourceSize = dialogSources.SelectedSourceSize;
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
        void SelectDialogKind(SourceKind kind, RadioButton radio)
        {
            if (!radio.Checked) return;
            dialogSources.SelectKind(kind);
            RenderAudioPicker(dialogSources, audioCombo);
            RefreshDlgSourceOptions();
        }
        rbWin.CheckedChanged += (_, _) => SelectDialogKind(SourceKind.Window, rbWin);
        rbMon.CheckedChanged += (_, _) => SelectDialogKind(SourceKind.Monitor, rbMon);
        rbCaptureDevice.CheckedChanged += (_, _) =>
            SelectDialogKind(SourceKind.CaptureDevice, rbCaptureDevice);
        winCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        winCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (dialogSources.SelectWindowIndex(winCombo.SelectedIndex))
                RefreshDlgSourceOptions();
        };
        monCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        monCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (dialogSources.SelectMonitorIndex(monCombo.SelectedIndex))
                RefreshDlgSourceOptions();
        };
        captureDeviceCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        captureDeviceCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (dialogSources.SelectCaptureDeviceIndex(captureDeviceCombo.SelectedIndex))
                RefreshDlgSourceOptions();
        };
        audioCombo.SelectedIndexChanged += (_, _) => dialogSources.SelectAudioIndex(audioCombo.SelectedIndex);
        presetCombo.SelectedIndexChanged += (_, _) => RefreshDlgSourceOptions();
        RefreshDlgSourceOptions();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(10),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int index = 0; index < 7; index++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        int row = 0;
        grid.Controls.Add(rbWin, 0, row);
        grid.Controls.Add(winCombo, 1, row++);
        grid.Controls.Add(rbMon, 0, row);
        grid.Controls.Add(monCombo, 1, row++);
        int captureDeviceRow = row;
        grid.Controls.Add(rbCaptureDevice, 0, row);
        grid.Controls.Add(captureDeviceCombo, 1, row++);
        grid.Controls.Add(new Label { Text = "Audio:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, row);
        grid.Controls.Add(audioCombo, 1, row++);
        grid.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, row);
        grid.Controls.Add(presetCombo, 1, row++);
        grid.Controls.Add(new Label { Text = "Bitrate:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, row);
        grid.Controls.Add(bitrateCombo, 1, row++);
        grid.Controls.Add(new Label { Text = "Encoder:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim }, 0, row);
        grid.Controls.Add(encoderCombo, 1, row++);

        var ok = new Button { Text = _streamController.HasSession ? "Switch" : "Start", Width = 96, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(buttons, 0, row);
        dlg.Controls.Add(grid);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        ApplyDarkTheme(dlg);
        ok.BackColor = AccentDark;

        void RenderDialogCaptureDeviceRow()
        {
            bool visible = dialogSources.CaptureDevices.Count > 0;
            rbWin.Checked = dialogSources.Kind == SourceKind.Window;
            rbMon.Checked = dialogSources.Kind == SourceKind.Monitor;
            rbCaptureDevice.Checked = visible && dialogSources.Kind == SourceKind.CaptureDevice;
            grid.SuspendLayout();
            rbCaptureDevice.Visible = visible;
            captureDeviceCombo.Visible = visible;
            RowStyle rowStyle = grid.RowStyles[captureDeviceRow];
            rowStyle.SizeType = visible ? SizeType.AutoSize : SizeType.Absolute;
            rowStyle.Height = 0;
            dlg.ClientSize = new Size(
                480,
                SwitchDialogExpandedHeight - (visible ? 0 : PickerRowHeight(captureDeviceCombo)));
            grid.ResumeLayout(performLayout: true);
        }

        void RefreshDialogCaptureDevices(IReadOnlyList<CaptureDeviceDescription> devices)
        {
            if (dlg.IsDisposed || dlg.Disposing) return;
            dialogSources.RefreshCaptureDevices(devices);
            RenderPicker(
                captureDeviceCombo,
                dialogSources.CaptureDeviceDisplayItems,
                dialogSources.SelectedCaptureDeviceIndex);
            RenderDialogCaptureDeviceRow();
            RefreshDlgSourceOptions();
        }

        RenderDialogCaptureDeviceRow();

        var completion = new TaskCompletionSource<DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(DialogResult result)
        {
            if (!completion.TrySetResult(result)) return;
            dlg.Hide();
        }
        ok.Click += (_, _) => Complete(DialogResult.OK);
        cancel.Click += (_, _) => Complete(DialogResult.Cancel);
        dlg.FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Complete(DialogResult.Cancel);
        };

        DialogResult result;
        _captureDeviceChanges.DevicesChanged += RefreshDialogCaptureDevices;
        try
        {
            this.Enabled = false;
            dlg.Show(this);
            result = await completion.Task;
        }
        finally
        {
            _captureDeviceChanges.DevicesChanged -= RefreshDialogCaptureDevices;
            this.Enabled = true;
        }
        if (result != DialogResult.OK) return;

        // The dialog has only held descriptions so far. Release any idle capture
        // source before its accepted pick is applied to the main controls and
        // can flow into Start/Switch acquisition.
        StopIdlePreview(hideLayout: true);

        // OK only. Refresh the main lists once (the single time this method mutates
        // them) so the picks resolve against a current enumeration, then VALIDATE
        // every specific pick BEFORE writing any main control: a source the dialog
        // captured but that has since vanished (window closed, monitor unplugged,
        // audio app exited) aborts the switch instead of silently falling through to
        // whatever the refreshed main list happens to select. All resolution reads
        // below happen before the first selected-value write, so an abort leaves the
        // running stream's effective configuration untouched.
        // Match the picked capture source by stable identity: HWND for a window,
        // device name for a monitor, and symbolic link for a capture device.
        PopulateSources();
        SourceSelection pickedSelection = dialogSources.CurrentSelection;
        SourceSelectionApplyFailure applyFailure = _sourceSelection.TryApplySelection(pickedSelection);
        if (applyFailure != SourceSelectionApplyFailure.None)
        {
            string message = applyFailure switch
            {
                SourceSelectionApplyFailure.WindowUnavailable =>
                    $"Switch cancelled: '{dialogSources.SelectedWindow?.ProcessName ?? "the picked window"}' is no longer available. Pick again.",
                SourceSelectionApplyFailure.MonitorUnavailable =>
                    "Switch cancelled: the picked monitor is no longer available. Pick again.",
                SourceSelectionApplyFailure.CaptureDeviceUnavailable =>
                    "Switch cancelled: the picked capture device is no longer available. Pick again.",
                SourceSelectionApplyFailure.AudioUnavailable =>
                    $"Switch cancelled: audio source '{pickedSelection.AudioKey}' is no longer available. Pick again.",
                _ => "Switch cancelled: the picked source is no longer available. Pick again.",
            };
            AppendLog(message);
            ResumeIdlePreview();
            return;
        }

        RenderMainSourceKind();
        RenderSourcePickers(
            _sourceSelection,
            _windowCombo,
            _monitorCombo,
            _captureDeviceCombo,
            _audioCombo);
        if (presetCombo.SelectedIndex >= 0) _presetCombo.SelectedIndex = presetCombo.SelectedIndex;
        if (encoderCombo.SelectedIndex >= 0) _encoderCombo.SelectedIndex = encoderCombo.SelectedIndex;
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
    private async void SwitchSource()
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("stream switch UI phase");
        StreamSession? session = _streamController.CurrentSession;
        if (session is null) { await StartStreamAsync(); return; }
        if (_streamController.IsStopping) return;
        var config = BuildConfigFromUi(_livePort, session.ViewKey);
        if (config is null) return;
        _streamController.Switch(
            config,
            StreamController.SourceSwitchTrigger,
            () =>
            {
                AppendLog("Switching sources. Viewers reconnect automatically.");
                SaveSettings();
            });
    }

    /// <summary>Serve the holding page whenever the app is open but no stream is
    /// running: tabs opened early (or holding a link from a previous stream) get
    /// "not streaming yet" and connect themselves once a stream starts. The
    /// session and the idle server trade the port back and forth.</summary>
    private void StartIdleServer()
    {
        if (_streamController.HasSession || _idleServer is not null) { _idleRetryTimer.Stop(); return; }
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
            Console.WriteLine($"[http] holding page listening on port {_portInput.Value}");
        }
        catch (Exception ex)
        {
            // Not fatal - usually another program owns the port right now. Log it
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

    /// <summary>Port or name changed while idle - rebind so the holding page follows.</summary>
    private void RestartIdleServer()
    {
        if (_streamController.HasSession || _streamController.IsStopping) return;
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
            var sourceSize = _sourceSelection.SelectedSourceSize;
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
    /// are locked so Switch source is the obvious path. Name and port stay
    /// editable because they are read at the next start.</summary>
    private void SetLiveLock(bool locked)
    {
        bool on = !locked;
        _rbWindow.Enabled = on;
        _rbMonitor.Enabled = on;
        _windowCombo.Enabled = on;
        _monitorCombo.Enabled = on;
        if (_rbCaptureDevice.Visible)
        {
            _rbCaptureDevice.Enabled = on;
            _captureDeviceCombo.Enabled = on;
        }
        _presetCombo.Enabled = on;
        _bitrateCombo.Enabled = on;
        _encoderCombo.Enabled = on;
        _audioCombo.Enabled = on;
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
        StreamSession? session = _streamController.CurrentSession;
        var b = session?.Broadcaster;
        if (b is null || _streamController.IsStopping) return;
        if (b.State == "starting")
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = "Starting: waiting for the first captured frame…";
            return;
        }
        _streamController.MarkLive();
        ShareReachability reachability = _shareLinks.ResolveReachability(
            _livePort,
            session!.LocalOnly);
        if (reachability == ShareReachability.LocalOnly)
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = $"LIVE, THIS PC ONLY: click Open port in Misc to let viewers reach port {_livePort}";
        }
        else if (reachability is ShareReachability.Tailscale or ShareReachability.Lan)
        {
            _statusLabel.ForeColor = Green;
            string lanTag = reachability == ShareReachability.Lan ? " (LAN)" : "";
            _statusLabel.Text = $"LIVE{lanTag} · {session.Description} · {EncoderLabel(session.ActiveEncoder)}   viewers: {b.ViewerCount}   source: {b.SourceFps} fps (dup {b.DupPercent}%)";
        }
        else
        {
            _statusLabel.ForeColor = Color.Goldenrod;
            _statusLabel.Text = "LIVE, but no reachable address in the current scope. Start Tailscale, or check Allow LAN and click Open port in Misc.";
        }
        Text = $"Spectari - LIVE ({b.ViewerCount} watching)";
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

    private ShareLinkContext CurrentShareLinkContext()
    {
        StreamSession? live = _streamController.CurrentSession;
        return new ShareLinkContext(
            (int)_portInput.Value,
            _livePort,
            live is not null,
            live?.LocalOnly == true,
            _idleServer?.LocalOnly == true,
            live?.ViewKey,
            _pendingKey);
    }

    private void UpdateLinkBox() => _linkBox.Text =
        _shareLinks.ResolvePrimaryUrl(CurrentShareLinkContext(), "");

    private void CopyLanLink()
    {
        LanLinkResolution resolution = _shareLinks.ResolveLanLink(CurrentShareLinkContext());
        if (resolution.Warning == LanLinkWarning.NoLanAddress)
        {
            AppendLog("LAN link unavailable: no LAN address was found for this PC.");
            return;
        }

        CopyLink(resolution.Url!);
        if (resolution.Warning == LanLinkWarning.ServerLocalOnly)
            AppendLog($"Warning: the server is bound to localhost only. This LAN link cannot work until Open port succeeds for port {resolution.Port}.");
        else if (resolution.Warning == LanLinkWarning.AccessNotConfirmed)
            AppendLog($"Warning: Spectari has no record of LAN access being opened for port {resolution.Port}. This link may not load on other devices; if it does not, check Allow LAN and click Open port.");
    }

    private void OpenWatchWindow()
    {
        if (_watchForm is { IsDisposed: false })
        {
            _watchForm.Show();
            _watchForm.WindowState = FormWindowState.Normal;
            _watchForm.Activate();
            return;
        }
        int port = ShareLinkResolver.ResolveActiveTarget(CurrentShareLinkContext()).Port;
        _watchForm = new WatchForm(port);
        AppRunContext.Current?.Track(_watchForm); // count it toward app lifetime
        _watchForm.Show();
    }

    private void CopyLink(string url)
    {
        try
        {
            Clipboard.SetText(url);
            AppendLog(url.Contains("://100.", StringComparison.Ordinal)
                ? "Copied Tailscale link."
                : "Copied viewer link.");
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard failed ({ex.Message}).");
        }
    }

    /// <summary>Relaunches this exe elevated to reserve the URL and open the
    /// firewall for the current port (same steps as setup.bat), then restarts
    /// the stream so the new binding takes effect. One UAC prompt, no file hunting.</summary>
    private void FixPortAccess()
    {
        int port = ShareLinkResolver.ResolveActiveTarget(CurrentShareLinkContext()).Port;
        HostReservationReview reservation = _hostAccess.ReviewReservation(port);
        if (reservation.Status != HostReservationStatus.AvailableOrOwned)
        {
            string body = reservation.Status == HostReservationStatus.UnknownOwner
                ? $"Port {port} is already reserved, but Spectari could not read which account owns it.\n\n" +
                  "Replacing that reservation may break the app that created it. " +
                  "Reserve this port for Spectari anyway?"
                : $"Port {port} is already reserved by another account:\n\n{reservation.Owner}\n\n" +
                  "Replacing that reservation may break the app that created it. " +
                  "Reserve this port for Spectari anyway?";
            var choice = MessageBox.Show(this, body,
                "Spectari", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                AppendLog($"Kept the existing reservation for port {port}; pick a different port instead.");
                return;
            }
        }

        _hostAccess.TryConfigure(port, _allowLanCheck.Checked, Application.ExecutablePath);
    }

    private void SetHostAccessControlsEnabled(bool enabled)
    {
        _allowLanCheck.Enabled = enabled;
        _portInput.Enabled = enabled;
        _fixPortButton.Enabled = enabled;
    }

    private void RenderHostAccessSetupResult(HostAccessSetupResult result)
    {
        SetHostAccessControlsEnabled(true);
        if (result.Outcome == HostAccessSetupOutcome.Succeeded)
        {
            SaveSettings();
            AppendLog($"Port {result.Request.Port} configured.");
            if (_streamController.CurrentSession is not null
                && _streamController.LastConfig is { } lastConfig
                && !_streamController.IsStopping)
            {
                AppendLog("Restarting the stream so the new access takes effect…");
                _streamController.Switch(lastConfig, StreamController.AccessRestartTrigger);
            }
            else if (_streamController.CurrentSession is null)
            {
                RestartIdleServer();
                UpdateLinkBox();
            }
        }
        else if (result.Outcome == HostAccessSetupOutcome.ApprovalDeclined)
        {
            AppendLog("Administrator approval was declined; viewers on other machines stay blocked.");
        }
        else
        {
            AppendLog($"Port setup failed (code {result.ExitCode}). Fallback: run setup.bat {result.Request.Port} as administrator.");
        }
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
            var activeCapture = _streamController.CurrentSession?.CaptureAdapter;
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
            sb.AppendLine($"Spectari {AppVersion.Current}");
            sb.AppendLine($"Windows:  {Environment.OSVersion.VersionString}");
            sb.AppendLine($"GPUs:     {diagnostics.gpus}");
            sb.AppendLine($"ffmpeg:   {diagnostics.ffmpeg.version}");
            sb.AppendLine($"ffmpeg build: {diagnostics.ffmpeg.buildconf}");
            sb.AppendLine($"ffmpeg sha256: {diagnostics.ffmpeg.sha256}");
            sb.AppendLine($"tailnet:  {diagnostics.tailnet}");
            sb.AppendLine($"enc cache: {ReadSmallFile(AppPaths.EncoderCacheFile)}");
            // A report can compare this to the cached verdict above. During a live
            // session it describes the exact capture adapter supplied to encoder selection;
            // while idle the label makes the primary-adapter fallback explicit.
            sb.AppendLine($"enc expected: {diagnostics.expected}  ({diagnostics.gpuSource}, adapter LUID {diagnostics.gpu.luid}, driver {diagnostics.gpu.driver})");
            sb.AppendLine($"session:  {DescribeSessionState()}");
            sb.AppendLine($"settings: {ReadSmallFile(SettingsPath)}");
            sb.AppendLine("---- last 200 operator events ----");
            string[] lines = ConsoleMirror.GetOperatorLines(200);
            foreach (string line in lines)
                sb.AppendLine(line);
            string scrubbed = Util.BundleScrubber.Scrub(sb.ToString(),
                new[]
                {
                    _streamController.CurrentSession?.ViewKey,
                    _streamController.LastConfig?.ViewKey,
                    _pendingKey,
                },
                new[]
                {
                    _streamController.LastConfig?.SourceName,
                    _streamController.LastConfig?.StreamName,
                    _nameInput.Text,
                    _sourceSelection.SelectedWindow?.Title,
                    _sourceSelection.SelectedMonitor?.DeviceName,
                    _sourceSelection.SelectedCaptureDevice?.SymbolicLink,
                });
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
            string folder = AppPaths.LogsDirectory;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open the log folder: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            CancellationToken token = _updateCheckCts.Token;
            string? remoteTag = await UpdateChecker.GetLatestReleaseTagAsync(token);
            if (token.IsCancellationRequested || IsDisposed || Disposing
                || !UpdateChecker.IsRemoteVersionNewer(AppVersion.Current, remoteTag))
                return;

            string? canonical = UpdateChecker.CanonicalVersion(remoteTag);
            Version? remoteVersion = UpdateChecker.ParseVersion(remoteTag);
            if (canonical is null || remoteVersion is null
                || string.Equals(canonical, _skippedUpdateVersion, StringComparison.Ordinal))
                return;

            _availableUpdateVersion = canonical;
            int displayParts = remoteVersion.Revision != 0 ? 4 : remoteVersion.Build != 0 ? 3 : 2;
            _updateLabel.Text = $"Spectari v{remoteVersion.ToString(displayParts)} is available.";
            _updatePanel.SetBounds(0, 0, ClientSize.Width, _updatePanel.Height);
            _updatePanel.BringToFront();
            _updatePanel.Visible = true;
        }
        catch
        {
            // Update discovery must never interrupt the app or surface a failure.
        }
    }

    private static void ViewLatestRelease()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateChecker.LatestReleasePageUrl,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void DismissUpdate()
    {
        if (_availableUpdateVersion is null) return;
        _skippedUpdateVersion = _availableUpdateVersion;
        _updatePanel.Visible = false;
        SaveSettings();
    }

    /// <summary>One-line description of the running session (or "not streaming"),
    /// shared by the support bundle and the crash log.</summary>
    internal string DescribeSessionState() =>
        _streamController.CurrentSession is { } s
            ? $"{s.Description} via {s.ActiveEncoder}, state {s.Broadcaster?.State}, viewers {s.Broadcaster?.ViewerCount}, localOnly {s.LocalOnly}"
            : "not streaming";

    /// <summary>Fatal-crash path only: stop a live session before the process
    /// exits, so ffmpeg tears down and the port releases instead of being killed
    /// mid-write. Stop() is bounded (a join with a timeout) so a wedged session
    /// can't hang the exit. Guarded - a crash handler must never throw again.</summary>
    internal void StopSessionForShutdown()
    {
        try { _streamController.StopForShutdown(); } catch { }
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
    /// Diagnostics only - never throws; returns "?" when unavailable.</summary>
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
    /// for the probe-cache fingerprint. Diagnostics only - never throws; returns
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
    /// token - the diagnostic value is the path mix, not the names. Status messages
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

    // ---- settings ---------------------------------------------------------

    private void LoadSettings()
    {
        Console.WriteLine("[boot] settings load start");
        try
        {
            if (!File.Exists(SettingsPath))
            {
                Console.WriteLine("[boot] settings load complete: using defaults");
                return;
            }
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (s is null)
            {
                Console.WriteLine("[boot] settings load complete: using defaults");
                return;
            }

            SourceKind sourceKind = s.SourceKind switch
            {
                "monitor" => SourceKind.Monitor,
                "capture-device" => SourceKind.CaptureDevice,
                _ => SourceKind.Window,
            };
            if (sourceKind == SourceKind.CaptureDevice && _sourceSelection.CaptureDevices.Count == 0)
                sourceKind = SourceKind.Window;
            _sourceSelection.SelectKind(sourceKind);
            _sourceSelection.SelectMonitorDevice(s.MonitorDeviceName, s.LegacyMonitorIndex);
            _sourceSelection.SelectWindowProcess(s.WindowProcess);
            _persistedCaptureDeviceSymbolicLink = s.CaptureDeviceSymbolicLink ?? "";
            _sourceSelection.SelectCaptureDevice(_persistedCaptureDeviceSymbolicLink);
            _sourceSelection.SelectAudioKey(s.AudioSource);
            RenderMainSourceKind();
            RenderSourcePickers(
                _sourceSelection,
                _windowCombo,
                _monitorCombo,
                _captureDeviceCombo,
                _audioCombo);
            // Set the saved tier first: changing the preset index below fires
            // PopulateBitrateOptions, which reads _savedBitrateTier to reselect.
            if (s.BitrateTier is "low" or "med" or "high") _savedBitrateTier = s.BitrateTier;
            int presetIdx = Array.FindIndex(Presets, p => p.Height == s.PresetHeight && p.Fps == s.PresetFps);
            _presetCombo.SelectedIndex = presetIdx >= 0 ? presetIdx : DefaultPresetIndex;
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
            _hostAccess.RestorePersistedState(s.AllowLan, s.AllowLanPort);
            _allowLanCheck.Checked = s.AllowLan;
            _skippedUpdateVersion = UpdateChecker.CanonicalVersion(s.SkipUpdateVersion);
            Console.WriteLine("[boot] settings load complete");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[boot] settings load failed: {ex}");
        }
    }

    private void SaveSettings()
    {
        using IDisposable? operation = _uiHangWatchdog?.TrackOperation("settings save");
        try
        {
            HostAccessPersistedState access = _hostAccess.PersistedState;
            var s = new AppSettings
            {
                SourceKind = _sourceSelection.Kind switch
                {
                    SourceKind.Monitor => "monitor",
                    SourceKind.CaptureDevice => "capture-device",
                    _ => "window",
                },
                WindowProcess = _sourceSelection.SelectedWindow?.ProcessName ?? "",
                MonitorDeviceName = _sourceSelection.SelectedMonitor?.DeviceName ?? "",
                CaptureDeviceSymbolicLink =
                    _sourceSelection.SelectedCaptureDevice?.SymbolicLink ??
                    _persistedCaptureDeviceSymbolicLink,
                PresetHeight = (_presetCombo.SelectedItem as Preset ?? Presets[DefaultPresetIndex]).Height,
                PresetFps = (_presetCombo.SelectedItem as Preset ?? Presets[DefaultPresetIndex]).Fps,
                BitrateTier = (_bitrateCombo.SelectedItem as BitrateChoice)?.Tier ?? "med",
                AudioSource = _sourceSelection.AudioKey,
                Port = (int)_portInput.Value,
                StreamName = _nameInput.Text.Trim(),
                Encoder = ((EncoderChoice)_encoderCombo.SelectedItem!).Value,
                AllowLan = access.AllowLan,
                AllowLanPort = access.Port,
                SkipUpdateVersion = _skippedUpdateVersion ?? "",
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

    private static void AppendLog(string line) => ConsoleMirror.WriteOperatorLine(line);

    private void AppendLogToUi(string line)
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
}
