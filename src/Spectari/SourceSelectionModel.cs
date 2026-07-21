using Spectari.Audio;
using Spectari.Capture;

namespace Spectari;

internal enum SourceKind
{
    Window,
    Monitor,
    CaptureDevice,
}

internal sealed record SourceSelection(
    SourceKind Kind,
    IntPtr? WindowHandle,
    string? MonitorDeviceName,
    string? CaptureDeviceSymbolicLink,
    string AudioKey);

internal enum SourceSelectionApplyFailure
{
    None,
    WindowUnavailable,
    MonitorUnavailable,
    CaptureDeviceUnavailable,
    AudioUnavailable,
}

internal sealed class SourceSelectionModel
{
    internal const string NoAudioKey = "none";
    internal const string CapturedWindowAudioKey = "window";
    // A colon cannot occur in a Windows process name, so this persisted mode
    // key cannot be mistaken for one of the per-app selections.
    internal const string DesktopAudioKey = "mode:desktop";
    internal const string AudioInputDeviceKeyPrefix = "input:";

    private readonly Func<List<WindowDescription>> _enumerateWindows;
    private readonly Func<List<MonitorDescription>> _enumerateMonitors;
    private readonly Func<List<CaptureDeviceDescription>> _enumerateCaptureDevices;
    private readonly Func<List<AudioInputDeviceDescription>> _enumerateAudioInputDevices;
    private readonly uint _ownPid;
    private List<WindowDescription> _windows = [];
    private List<MonitorDescription> _monitors = [];
    private List<CaptureDeviceDescription> _captureDevices = [];
    private List<string> _captureDeviceDisplayItems = [];
    private List<AudioInputDeviceDescription> _audioInputDevices = [];
    private List<string> _audioInputDeviceDisplayItems = [];
    private IntPtr? _selectedWindowHandle;
    private string? _selectedMonitorDeviceName;
    private string? _selectedCaptureDeviceSymbolicLink;
    private string _audioKey = CapturedWindowAudioKey;

    internal SourceSelectionModel()
        : this(
            WindowEnumerator.EnumerateWindows,
            MonitorEnumerator.GetMonitors,
            CaptureDeviceEnumerator.GetDevices,
            AudioInputDeviceEnumerator.GetDevices,
            (uint)Environment.ProcessId)
    {
    }

    internal SourceSelectionModel(
        Func<List<WindowDescription>> enumerateWindows,
        Func<List<MonitorDescription>> enumerateMonitors,
        uint ownPid)
        : this(enumerateWindows, enumerateMonitors, () => [], () => [], ownPid)
    {
    }

    internal SourceSelectionModel(
        Func<List<WindowDescription>> enumerateWindows,
        Func<List<MonitorDescription>> enumerateMonitors,
        Func<List<CaptureDeviceDescription>> enumerateCaptureDevices,
        uint ownPid)
        : this(enumerateWindows, enumerateMonitors, enumerateCaptureDevices, () => [], ownPid)
    {
    }

    internal SourceSelectionModel(
        Func<List<WindowDescription>> enumerateWindows,
        Func<List<MonitorDescription>> enumerateMonitors,
        Func<List<CaptureDeviceDescription>> enumerateCaptureDevices,
        Func<List<AudioInputDeviceDescription>> enumerateAudioInputDevices,
        uint ownPid)
    {
        _enumerateWindows = enumerateWindows;
        _enumerateMonitors = enumerateMonitors;
        _enumerateCaptureDevices = enumerateCaptureDevices;
        _enumerateAudioInputDevices = enumerateAudioInputDevices;
        _ownPid = ownPid;
    }

    internal SourceKind Kind { get; private set; } = SourceKind.Window;
    internal IReadOnlyList<WindowDescription> Windows => _windows;
    internal IReadOnlyList<MonitorDescription> Monitors => _monitors;
    internal IReadOnlyList<CaptureDeviceDescription> CaptureDevices => _captureDevices;
    internal IReadOnlyList<AudioInputDeviceDescription> AudioInputDevices => _audioInputDevices;
    internal IReadOnlyList<string> WindowDisplayItems => _windows.Select(FormatWindow).ToArray();
    internal IReadOnlyList<string> MonitorDisplayItems => _monitors.Select(FormatMonitor).ToArray();
    internal IReadOnlyList<string> CaptureDeviceDisplayItems => _captureDeviceDisplayItems;
    internal IReadOnlyList<string> AudioDisplayItems => Kind switch
    {
        SourceKind.Window =>
        [
            "No audio",
            "Captured window's audio",
            "Desktop audio (all sound)",
            .. _audioInputDeviceDisplayItems,
            .. _windows.Select(FormatAudioWindow),
        ],
        SourceKind.Monitor =>
        [
            "No audio (monitor share: pick an app below)",
            "Desktop audio (all sound)",
            .. _audioInputDeviceDisplayItems,
            .. _windows.Select(FormatAudioWindow),
        ],
        SourceKind.CaptureDevice =>
        [
            "No audio (capture device share: pick an app below)",
            "Desktop audio (all sound)",
            .. _audioInputDeviceDisplayItems,
            .. _windows.Select(FormatAudioWindow),
        ],
        _ =>
        [
            "No audio (pick an app below)",
            "Desktop audio (all sound)",
            .. _audioInputDeviceDisplayItems,
            .. _windows.Select(FormatAudioWindow),
        ],
    };

    internal int SelectedWindowIndex => _selectedWindowHandle is { } handle
        ? _windows.FindIndex(window => window.Handle == handle)
        : -1;

    internal int SelectedMonitorIndex => _selectedMonitorDeviceName is { } deviceName
        ? _monitors.FindIndex(monitor => MonitorIdentityEquals(monitor.DeviceName, deviceName))
        : -1;

    internal int SelectedCaptureDeviceIndex => _selectedCaptureDeviceSymbolicLink is { } symbolicLink
        ? _captureDevices.FindIndex(device => CaptureDeviceIdentityEquals(device.SymbolicLink, symbolicLink))
        : -1;

    internal int SelectedAudioIndex
    {
        get
        {
            if (_audioKey == NoAudioKey) return 0;
            if (_audioKey == CapturedWindowAudioKey) return Kind == SourceKind.Window ? 1 : 0;
            if (_audioKey == DesktopAudioKey) return Kind == SourceKind.Window ? 2 : 1;
            int fixedOffset = Kind == SourceKind.Window ? 3 : 2;
            int inputIndex = _audioInputDevices.FindIndex(device =>
                AudioInputDeviceIdentityEquals(device.Id, AudioInputDeviceIdFromKey(_audioKey)));
            if (inputIndex >= 0) return fixedOffset + inputIndex;
            int index = _windows.FindIndex(window => AudioIdentityEquals(window.ProcessName, _audioKey));
            int processOffset = fixedOffset + _audioInputDevices.Count;
            return index >= 0 ? index + processOffset : Kind == SourceKind.Window ? 1 : 0;
        }
    }

    internal WindowDescription? SelectedWindow => SelectedWindowIndex is int index && index >= 0
        ? _windows[index]
        : null;

    internal MonitorDescription? SelectedMonitor => SelectedMonitorIndex is int index && index >= 0
        ? _monitors[index]
        : null;

    internal CaptureDeviceDescription? SelectedCaptureDevice =>
        SelectedCaptureDeviceIndex is int index && index >= 0
            ? _captureDevices[index]
            : null;

    internal string AudioKey => _audioKey;

    internal SourceSelection CurrentSelection => new(
        Kind,
        _selectedWindowHandle,
        _selectedMonitorDeviceName,
        _selectedCaptureDeviceSymbolicLink,
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
        DesktopAudioKey => 0,
        CapturedWindowAudioKey when Kind == SourceKind.Window => SelectedWindow?.Pid ?? 0,
        CapturedWindowAudioKey => 0,
        _ => _windows.FirstOrDefault(window => AudioIdentityEquals(window.ProcessName, _audioKey))?.Pid ?? 0,
    };

    internal bool IsDesktopAudioSelected => _audioKey == DesktopAudioKey;

    internal string? SelectedAudioInputDeviceId
    {
        get
        {
            string? selectedId = AudioInputDeviceIdFromKey(_audioKey);
            return selectedId is null
                ? null
                : _audioInputDevices.FirstOrDefault(device =>
                    AudioInputDeviceIdentityEquals(device.Id, selectedId))?.Id;
        }
    }

    internal bool IsSelectedAudioProcessUnavailable =>
        _audioKey is not (NoAudioKey or CapturedWindowAudioKey or DesktopAudioKey)
        && !IsAudioInputDeviceKey(_audioKey)
        && SelectedAudioPid == 0;

    internal void RefreshAll()
    {
        RefreshWindows();
        RefreshMonitors();
        RefreshCaptureDevices();
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
        RefreshAudioInputDevices();
        NormalizeAudioSelection();
    }

    internal void RefreshMonitors()
    {
        _monitors = [.. _enumerateMonitors()];
        if (SelectedMonitorIndex < 0)
            _selectedMonitorDeviceName = _monitors.Count > 0 ? _monitors[0].DeviceName : null;
    }

    internal void RefreshCaptureDevices() => RefreshCaptureDevices(_enumerateCaptureDevices());

    internal void RefreshCaptureDevices(IEnumerable<CaptureDeviceDescription> devices)
    {
        IReadOnlyList<CaptureDeviceDisplayItem> items =
            CaptureDevicePolicy.PrepareDisplayItems(devices);
        _captureDevices = items.Select(item => item.Device).ToList();
        _captureDeviceDisplayItems = items.Select(item => item.DisplayName).ToList();
        if (SelectedCaptureDeviceIndex < 0)
        {
            if (Kind == SourceKind.CaptureDevice)
                Kind = SourceKind.Monitor;
            _selectedCaptureDeviceSymbolicLink = _captureDevices.Count > 0
                ? _captureDevices[0].SymbolicLink
                : null;
        }
    }

    internal void RefreshAudioInputDevices() =>
        RefreshAudioInputDevices(_enumerateAudioInputDevices());

    internal void RefreshAudioInputDevices(IEnumerable<AudioInputDeviceDescription> devices)
    {
        IReadOnlyList<AudioInputDeviceDisplayItem> items =
            AudioInputDevicePolicy.PrepareDisplayItems(devices);
        _audioInputDevices = items.Select(item => item.Device).ToList();
        _audioInputDeviceDisplayItems = items.Select(item => item.DisplayName).ToList();
        NormalizeAudioSelection();
    }

    internal SourceSelectionModel CreateFreshSnapshot()
    {
        IReadOnlyList<CaptureDeviceDisplayItem> captureDeviceItems =
            CaptureDevicePolicy.PrepareDisplayItems(_enumerateCaptureDevices());
        IReadOnlyList<AudioInputDeviceDisplayItem> audioInputDeviceItems =
            AudioInputDevicePolicy.PrepareDisplayItems(_enumerateAudioInputDevices());
        var snapshot = new SourceSelectionModel(
            _enumerateWindows,
            _enumerateMonitors,
            _enumerateCaptureDevices,
            _enumerateAudioInputDevices,
            _ownPid)
        {
            Kind = Kind,
            _selectedWindowHandle = _selectedWindowHandle,
            _selectedMonitorDeviceName = _selectedMonitorDeviceName,
            _selectedCaptureDeviceSymbolicLink = _selectedCaptureDeviceSymbolicLink,
            _audioKey = _audioKey,
            _windows = PrepareWindows(_enumerateWindows(), _ownPid),
            _monitors = [.. _enumerateMonitors()],
            _captureDevices = captureDeviceItems.Select(item => item.Device).ToList(),
            _captureDeviceDisplayItems = captureDeviceItems.Select(item => item.DisplayName).ToList(),
            _audioInputDevices = audioInputDeviceItems.Select(item => item.Device).ToList(),
            _audioInputDeviceDisplayItems = audioInputDeviceItems.Select(item => item.DisplayName).ToList(),
        };
        if (snapshot.SelectedWindowIndex < 0) snapshot._selectedWindowHandle = null;
        if (snapshot.SelectedMonitorIndex < 0) snapshot._selectedMonitorDeviceName = null;
        if (snapshot.SelectedCaptureDeviceIndex < 0)
        {
            snapshot._selectedCaptureDeviceSymbolicLink = null;
            if (snapshot.Kind == SourceKind.CaptureDevice) snapshot.Kind = SourceKind.Monitor;
        }
        snapshot.NormalizeAudioSelection();
        return snapshot;
    }

    internal void SelectKind(SourceKind kind) => Kind = kind;

    internal bool SelectWindowIndex(int index)
    {
        IntPtr? selectedHandle = index >= 0 && index < _windows.Count
            ? _windows[index].Handle
            : null;
        bool changed = selectedHandle != _selectedWindowHandle;
        _selectedWindowHandle = selectedHandle;
        return changed;
    }

    internal bool SelectMonitorIndex(int index)
    {
        string? selectedDeviceName = index >= 0 && index < _monitors.Count
            ? _monitors[index].DeviceName
            : null;
        bool changed = selectedDeviceName is null || _selectedMonitorDeviceName is null
            ? selectedDeviceName != _selectedMonitorDeviceName
            : !MonitorIdentityEquals(selectedDeviceName, _selectedMonitorDeviceName);
        _selectedMonitorDeviceName = selectedDeviceName;
        return changed;
    }

    internal bool SelectCaptureDeviceIndex(int index)
    {
        string? selectedSymbolicLink = index >= 0 && index < _captureDevices.Count
            ? _captureDevices[index].SymbolicLink
            : null;
        bool changed = selectedSymbolicLink is null || _selectedCaptureDeviceSymbolicLink is null
            ? selectedSymbolicLink != _selectedCaptureDeviceSymbolicLink
            : !CaptureDeviceIdentityEquals(selectedSymbolicLink, _selectedCaptureDeviceSymbolicLink);
        _selectedCaptureDeviceSymbolicLink = selectedSymbolicLink;
        return changed;
    }

    internal void SelectAudioIndex(int index)
    {
        int fixedOffset = Kind == SourceKind.Window ? 3 : 2;
        if (index == 0)
        {
            _audioKey = NoAudioKey;
            return;
        }
        if (Kind == SourceKind.Window && index == 1)
        {
            _audioKey = CapturedWindowAudioKey;
            return;
        }
        if (index == (Kind == SourceKind.Window ? 2 : 1))
        {
            _audioKey = DesktopAudioKey;
            return;
        }

        int inputIndex = index - fixedOffset;
        if (inputIndex >= 0 && inputIndex < _audioInputDevices.Count)
        {
            _audioKey = AudioInputDeviceKey(_audioInputDevices[inputIndex].Id);
            return;
        }

        int processIndex = inputIndex - _audioInputDevices.Count;
        _audioKey = processIndex >= 0 && processIndex < _windows.Count
            ? _windows[processIndex].ProcessName
            : CapturedWindowAudioKey;
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

    internal void SelectCaptureDevice(string? symbolicLink)
    {
        int index = string.IsNullOrEmpty(symbolicLink)
            ? -1
            : _captureDevices.FindIndex(device =>
                CaptureDeviceIdentityEquals(device.SymbolicLink, symbolicLink));
        if (index >= 0) _selectedCaptureDeviceSymbolicLink = _captureDevices[index].SymbolicLink;
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
        CaptureDeviceDescription? captureDevice = selection.CaptureDeviceSymbolicLink is { } symbolicLink
            ? _captureDevices.FirstOrDefault(candidate =>
                CaptureDeviceIdentityEquals(candidate.SymbolicLink, symbolicLink))
            : null;

        if (selection.Kind == SourceKind.Window && window is null)
            return SourceSelectionApplyFailure.WindowUnavailable;
        if (selection.Kind == SourceKind.Monitor && monitor is null)
            return SourceSelectionApplyFailure.MonitorUnavailable;
        if (selection.Kind == SourceKind.CaptureDevice && captureDevice is null)
            return SourceSelectionApplyFailure.CaptureDeviceUnavailable;
        string? audioInputDeviceId = AudioInputDeviceIdFromKey(selection.AudioKey);
        if (audioInputDeviceId is not null &&
            !_audioInputDevices.Any(candidate =>
                AudioInputDeviceIdentityEquals(candidate.Id, audioInputDeviceId)))
            return SourceSelectionApplyFailure.AudioUnavailable;
        if (audioInputDeviceId is null &&
            selection.AudioKey is not (NoAudioKey or CapturedWindowAudioKey or DesktopAudioKey) &&
            !_windows.Any(candidate => AudioIdentityEquals(candidate.ProcessName, selection.AudioKey)))
            return SourceSelectionApplyFailure.AudioUnavailable;

        Kind = selection.Kind;
        if (window is not null) _selectedWindowHandle = window.Handle;
        if (monitor is not null) _selectedMonitorDeviceName = monitor.DeviceName;
        if (captureDevice is not null) _selectedCaptureDeviceSymbolicLink = captureDevice.SymbolicLink;
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
        if (_audioKey is NoAudioKey or CapturedWindowAudioKey or DesktopAudioKey) return;
        string? audioInputDeviceId = AudioInputDeviceIdFromKey(_audioKey);
        if (audioInputDeviceId is not null)
        {
            if (!_audioInputDevices.Any(device =>
                AudioInputDeviceIdentityEquals(device.Id, audioInputDeviceId)))
                _audioKey = CapturedWindowAudioKey;
            return;
        }
        if (!_windows.Any(window => AudioIdentityEquals(window.ProcessName, _audioKey)))
            _audioKey = CapturedWindowAudioKey;
    }

    private static string AudioInputDeviceKey(string deviceId) =>
        AudioInputDeviceKeyPrefix + deviceId;

    private static bool IsAudioInputDeviceKey(string key) =>
        key.StartsWith(AudioInputDeviceKeyPrefix, StringComparison.Ordinal);

    private static string? AudioInputDeviceIdFromKey(string key) =>
        IsAudioInputDeviceKey(key) && key.Length > AudioInputDeviceKeyPrefix.Length
            ? key[AudioInputDeviceKeyPrefix.Length..]
            : null;

    private static bool AudioIdentityEquals(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool MonitorIdentityEquals(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool CaptureDeviceIdentityEquals(string left, string right) =>
        left.Equals(right, StringComparison.Ordinal);

    private static bool AudioInputDeviceIdentityEquals(string left, string? right) =>
        right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
