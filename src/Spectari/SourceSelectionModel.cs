using Spectari.Capture;

namespace Spectari;

internal enum SourceKind
{
    Window,
    Monitor,
}

internal sealed record SourceSelection(
    SourceKind Kind,
    IntPtr? WindowHandle,
    string? MonitorDeviceName,
    string AudioKey);

internal enum SourceSelectionApplyFailure
{
    None,
    WindowUnavailable,
    MonitorUnavailable,
    AudioUnavailable,
}

internal sealed class SourceSelectionModel
{
    internal const string NoAudioKey = "none";
    internal const string CapturedWindowAudioKey = "window";

    private readonly Func<List<WindowDescription>> _enumerateWindows;
    private readonly Func<List<MonitorDescription>> _enumerateMonitors;
    private readonly uint _ownPid;
    private List<WindowDescription> _windows = [];
    private List<MonitorDescription> _monitors = [];
    private IntPtr? _selectedWindowHandle;
    private string? _selectedMonitorDeviceName;
    private string _audioKey = CapturedWindowAudioKey;

    internal SourceSelectionModel()
        : this(WindowEnumerator.EnumerateWindows, MonitorEnumerator.GetMonitors, (uint)Environment.ProcessId)
    {
    }

    internal SourceSelectionModel(
        Func<List<WindowDescription>> enumerateWindows,
        Func<List<MonitorDescription>> enumerateMonitors,
        uint ownPid)
    {
        _enumerateWindows = enumerateWindows;
        _enumerateMonitors = enumerateMonitors;
        _ownPid = ownPid;
    }

    internal SourceKind Kind { get; private set; } = SourceKind.Window;
    internal IReadOnlyList<WindowDescription> Windows => _windows;
    internal IReadOnlyList<MonitorDescription> Monitors => _monitors;
    internal IReadOnlyList<string> WindowDisplayItems => _windows.Select(FormatWindow).ToArray();
    internal IReadOnlyList<string> MonitorDisplayItems => _monitors.Select(FormatMonitor).ToArray();
    internal IReadOnlyList<string> AudioDisplayItems =>
    [
        "No audio",
        Kind == SourceKind.Window
            ? "Captured window's audio"
            : "No audio (monitor share: pick an app below)",
        .. _windows.Select(FormatAudioWindow),
    ];

    internal int SelectedWindowIndex => _selectedWindowHandle is { } handle
        ? _windows.FindIndex(window => window.Handle == handle)
        : -1;

    internal int SelectedMonitorIndex => _selectedMonitorDeviceName is { } deviceName
        ? _monitors.FindIndex(monitor => MonitorIdentityEquals(monitor.DeviceName, deviceName))
        : -1;

    internal int SelectedAudioIndex
    {
        get
        {
            if (_audioKey == NoAudioKey) return 0;
            if (_audioKey == CapturedWindowAudioKey) return 1;
            int index = _windows.FindIndex(window => AudioIdentityEquals(window.ProcessName, _audioKey));
            return index >= 0 ? index + 2 : 1;
        }
    }

    internal WindowDescription? SelectedWindow => SelectedWindowIndex is int index && index >= 0
        ? _windows[index]
        : null;

    internal MonitorDescription? SelectedMonitor => SelectedMonitorIndex is int index && index >= 0
        ? _monitors[index]
        : null;

    internal string AudioKey => _audioKey;

    internal SourceSelection CurrentSelection => new(
        Kind,
        _selectedWindowHandle,
        _selectedMonitorDeviceName,
        _audioKey);

    internal (int W, int H) SelectedSourceSize => Kind switch
    {
        SourceKind.Window when SelectedWindow is { } window => (window.Width, window.Height),
        SourceKind.Monitor when SelectedMonitor is { } monitor => (monitor.Width, monitor.Height),
        _ => (0, 0),
    };

    internal uint SelectedAudioPid => _audioKey switch
    {
        NoAudioKey => 0,
        CapturedWindowAudioKey when Kind == SourceKind.Window => SelectedWindow?.Pid ?? 0,
        CapturedWindowAudioKey => 0,
        _ => _windows.FirstOrDefault(window => AudioIdentityEquals(window.ProcessName, _audioKey))?.Pid ?? 0,
    };

    internal void RefreshAll()
    {
        RefreshWindows();
        RefreshMonitors();
    }

    internal void RefreshWindows()
    {
        string? previousProcess = SelectedWindow?.ProcessName;
        _windows = PrepareWindows(_enumerateWindows(), _ownPid);
        if (SelectedWindowIndex < 0)
        {
            // Deliberate fallback order for a vanished handle: the same process
            // first, so a relaunched app stays picked, then the first window.
            int processIndex = previousProcess is null ? -1
                : _windows.FindIndex(window => window.ProcessName.Equals(
                    previousProcess, StringComparison.OrdinalIgnoreCase));
            _selectedWindowHandle = processIndex >= 0 ? _windows[processIndex].Handle
                : _windows.Count > 0 ? _windows[0].Handle : null;
        }
        NormalizeAudioSelection();
    }

    internal void RefreshMonitors()
    {
        _monitors = [.. _enumerateMonitors()];
        if (SelectedMonitorIndex < 0)
            _selectedMonitorDeviceName = _monitors.Count > 0 ? _monitors[0].DeviceName : null;
    }

    internal SourceSelectionModel CreateFreshSnapshot()
    {
        var snapshot = new SourceSelectionModel(_enumerateWindows, _enumerateMonitors, _ownPid)
        {
            Kind = Kind,
            _selectedWindowHandle = _selectedWindowHandle,
            _selectedMonitorDeviceName = _selectedMonitorDeviceName,
            _audioKey = _audioKey,
            _windows = PrepareWindows(_enumerateWindows(), _ownPid),
            _monitors = [.. _enumerateMonitors()],
        };
        if (snapshot.SelectedWindowIndex < 0) snapshot._selectedWindowHandle = null;
        if (snapshot.SelectedMonitorIndex < 0) snapshot._selectedMonitorDeviceName = null;
        snapshot.NormalizeAudioSelection();
        return snapshot;
    }

    internal void SelectKind(SourceKind kind) => Kind = kind;

    internal void SelectWindowIndex(int index) =>
        _selectedWindowHandle = index >= 0 && index < _windows.Count ? _windows[index].Handle : null;

    internal void SelectMonitorIndex(int index) =>
        _selectedMonitorDeviceName = index >= 0 && index < _monitors.Count ? _monitors[index].DeviceName : null;

    internal void SelectAudioIndex(int index)
    {
        _audioKey = index switch
        {
            0 => NoAudioKey,
            1 => CapturedWindowAudioKey,
            > 1 when index - 2 < _windows.Count => _windows[index - 2].ProcessName,
            _ => CapturedWindowAudioKey,
        };
    }

    internal void SelectWindowProcess(string? processName)
    {
        int index = string.IsNullOrEmpty(processName)
            ? -1
            : _windows.FindIndex(window => AudioIdentityEquals(window.ProcessName, processName));
        if (index >= 0) _selectedWindowHandle = _windows[index].Handle;
    }

    internal void SelectMonitorDevice(string? deviceName, int? legacyIndex = null)
    {
        int index = string.IsNullOrEmpty(deviceName)
            ? -1
            : _monitors.FindIndex(monitor => MonitorIdentityEquals(monitor.DeviceName, deviceName));
        if (index < 0 && legacyIndex is int candidateIndex && candidateIndex >= 0 && candidateIndex < _monitors.Count)
            index = candidateIndex;
        if (index >= 0) _selectedMonitorDeviceName = _monitors[index].DeviceName;
    }

    internal void SelectAudioKey(string? key)
    {
        _audioKey = string.IsNullOrEmpty(key) ? CapturedWindowAudioKey : key;
        NormalizeAudioSelection();
    }

    internal SourceSelectionApplyFailure TryApplySelection(SourceSelection selection)
    {
        WindowDescription? window = selection.WindowHandle is { } windowHandle
            ? _windows.FirstOrDefault(candidate => candidate.Handle == windowHandle)
            : null;
        MonitorDescription? monitor = selection.MonitorDeviceName is { } monitorDeviceName
            ? _monitors.FirstOrDefault(candidate => MonitorIdentityEquals(candidate.DeviceName, monitorDeviceName))
            : null;

        if (selection.Kind == SourceKind.Window && window is null)
            return SourceSelectionApplyFailure.WindowUnavailable;
        if (selection.Kind == SourceKind.Monitor && monitor is null)
            return SourceSelectionApplyFailure.MonitorUnavailable;
        if (selection.AudioKey is not (NoAudioKey or CapturedWindowAudioKey) &&
            !_windows.Any(candidate => AudioIdentityEquals(candidate.ProcessName, selection.AudioKey)))
            return SourceSelectionApplyFailure.AudioUnavailable;

        Kind = selection.Kind;
        if (window is not null) _selectedWindowHandle = window.Handle;
        if (monitor is not null) _selectedMonitorDeviceName = monitor.DeviceName;
        _audioKey = selection.AudioKey;
        return SourceSelectionApplyFailure.None;
    }

    internal static List<WindowDescription> PrepareWindows(
        IEnumerable<WindowDescription> windows,
        uint ownPid) => FilterSurfaceLess(windows)
        .Where(window => window.Pid != ownPid)
        .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal static List<WindowDescription> FilterSurfaceLess(IEnumerable<WindowDescription> windows) =>
        windows.Where(HasCapturableSurface).ToList();

    internal static bool HasCapturableSurface(WindowDescription window) =>
        window.Width >= 2 && window.Height >= 2;

    internal static string FormatWindow(WindowDescription window) =>
        $"{window.ProcessName} - {Truncate(window.Title, 58)}";

    internal static string FormatMonitor(MonitorDescription monitor) =>
        $"{monitor.DeviceName}  {monitor.Width}x{monitor.Height}{(monitor.IsPrimary ? "  (primary)" : "")}";

    internal static string FormatAudioWindow(WindowDescription window) =>
        $"{window.ProcessName} - {Truncate(window.Title, 40)}";

    private void NormalizeAudioSelection()
    {
        if (_audioKey is NoAudioKey or CapturedWindowAudioKey) return;
        if (!_windows.Any(window => AudioIdentityEquals(window.ProcessName, _audioKey)))
            _audioKey = CapturedWindowAudioKey;
    }

    private static bool AudioIdentityEquals(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool MonitorIdentityEquals(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
