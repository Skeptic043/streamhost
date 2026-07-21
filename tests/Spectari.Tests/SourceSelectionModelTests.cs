using Spectari.Audio;
using Spectari.Capture;
using Xunit;

namespace Spectari.Tests;

public sealed class SourceSelectionModelTests
{
    [Fact]
    public void RefreshPreservesWindowAndMonitorStableIdentities()
    {
        List<WindowDescription> windows =
        [
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        List<MonitorDescription> monitors =
        [
            Monitor(10, "DISPLAY-A"),
            Monitor(20, "DISPLAY-B"),
        ];
        var model = Model(() => windows, () => monitors);
        model.RefreshAll();
        model.SelectWindowIndex(1);
        model.SelectMonitorIndex(1);

        windows =
        [
            Window(3, "Aardvark", 33),
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        monitors =
        [
            Monitor(20, "DISPLAY-B"),
            Monitor(10, "DISPLAY-A"),
        ];
        model.RefreshAll();

        Assert.Equal(new IntPtr(2), model.SelectedWindow?.Handle);
        Assert.Equal(2, model.SelectedWindowIndex);
        Assert.Equal("DISPLAY-B", model.SelectedMonitor?.DeviceName);
        Assert.Equal(0, model.SelectedMonitorIndex);
        Assert.False(model.SelectWindowIndex(2));
        Assert.False(model.SelectMonitorIndex(0));
    }

    [Fact]
    public void PickerSelectionReportsOnlyStableIdentityChanges()
    {
        var model = Model(
            () =>
            [
                Window(1, "Alpha", 11),
                Window(2, "Bravo", 22),
            ],
            () =>
            [
                Monitor(10, "DISPLAY-A"),
                Monitor(20, "DISPLAY-B"),
            ],
            () =>
            [
                Device("device-a", "Alpha"),
                Device("device-b", "Bravo"),
            ]);
        model.RefreshAll();

        Assert.False(model.SelectWindowIndex(0));
        Assert.True(model.SelectWindowIndex(1));
        Assert.False(model.SelectWindowIndex(1));
        Assert.False(model.SelectMonitorIndex(0));
        Assert.True(model.SelectMonitorIndex(1));
        Assert.False(model.SelectMonitorIndex(1));
        Assert.False(model.SelectCaptureDeviceIndex(0));
        Assert.True(model.SelectCaptureDeviceIndex(1));
        Assert.False(model.SelectCaptureDeviceIndex(1));
    }

    [Fact]
    public void RefreshPreservesCaptureDeviceSymbolicLinkAcrossReordering()
    {
        List<CaptureDeviceDescription> devices =
        [
            Device("device-a", "Alpha"),
            Device("device-b", "Bravo"),
        ];
        var model = Model(() => [], () => [], () => devices);
        model.RefreshAll();
        model.SelectCaptureDeviceIndex(1);

        devices =
        [
            Device("device-b", "Bravo"),
            Device("device-c", "Aardvark"),
            Device("device-a", "Alpha"),
        ];
        model.RefreshAll();

        Assert.Equal("device-b", model.SelectedCaptureDevice?.SymbolicLink);
        Assert.Equal(2, model.SelectedCaptureDeviceIndex);
        Assert.False(model.SelectCaptureDeviceIndex(2));
    }

    [Fact]
    public void RefreshFallsBackToMonitorWhenSelectedCaptureDeviceDisappears()
    {
        List<CaptureDeviceDescription> devices =
        [
            Device("device-a", "Alpha"),
            Device("device-b", "Bravo"),
        ];
        var model = Model(() => [], () => [Monitor(10, "DISPLAY-A")], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.CaptureDevice);
        model.SelectCaptureDeviceIndex(1);

        devices = [Device("device-a", "Alpha")];
        model.RefreshCaptureDevices();

        Assert.Equal(SourceKind.Monitor, model.Kind);
        Assert.Equal("device-a", model.SelectedCaptureDevice?.SymbolicLink);
        Assert.Equal("DISPLAY-A", model.SelectedMonitor?.DeviceName);
    }

    [Fact]
    public void RefreshKeepsMonitorSelectedWhenCaptureDevicesChange()
    {
        List<CaptureDeviceDescription> devices = [Device("device-a", "Alpha")];
        var model = Model(() => [], () => [Monitor(10, "DISPLAY-A")], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.Monitor);

        devices = [];
        model.RefreshCaptureDevices();

        Assert.Equal(SourceKind.Monitor, model.Kind);
        Assert.Null(model.SelectedCaptureDevice);
    }

    [Fact]
    public void RefreshFallsBackToMonitorWhenLastCaptureDeviceDisappears()
    {
        List<CaptureDeviceDescription> devices = [Device("device-a", "Alpha")];
        var model = Model(() => [], () => [Monitor(10, "DISPLAY-A")], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.CaptureDevice);

        devices = [];
        model.RefreshCaptureDevices();

        Assert.Equal(SourceKind.Monitor, model.Kind);
        Assert.Null(model.SelectedCaptureDevice);
        Assert.Equal("DISPLAY-A", model.SelectedMonitor?.DeviceName);
    }

    [Fact]
    public void FreshSnapshotFallsBackWhenSelectedCaptureDeviceDisappeared()
    {
        List<CaptureDeviceDescription> devices = [Device("device-a", "Alpha")];
        var model = Model(() => [], () => [Monitor(10, "DISPLAY-A")], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.CaptureDevice);

        devices = [];
        SourceSelectionModel snapshot = model.CreateFreshSnapshot();

        Assert.Equal(SourceKind.Monitor, snapshot.Kind);
        Assert.Null(snapshot.SelectedCaptureDevice);
    }

    [Fact]
    public void FormatsPickerAndAudioDisplayStrings()
    {
        var window = Window(1, "game", 11, "Game title");
        var monitor = new MonitorDescription(new IntPtr(10), "DISPLAY-A", 2560, 1440, true);
        var model = Model(() => [window], () => [monitor]);
        model.RefreshAll();

        Assert.Equal("game - Game title", Assert.Single(model.WindowDisplayItems));
        Assert.Equal("DISPLAY-A  2560x1440  (primary)", Assert.Single(model.MonitorDisplayItems));
        Assert.Equal(
            [
                "No audio",
                "Captured window's audio",
                "Desktop audio (all sound)",
                "game - Game title",
            ],
            model.AudioDisplayItems);

        model.SelectKind(SourceKind.Monitor);

        Assert.Equal(
            [
                "No audio (monitor share: pick an app below)",
                "Desktop audio (all sound)",
                "game - Game title",
            ],
            model.AudioDisplayItems);

        model.SelectKind(SourceKind.CaptureDevice);

        Assert.Equal(
            [
                "No audio (capture device share: pick an app below)",
                "Desktop audio (all sound)",
                "game - Game title",
            ],
            model.AudioDisplayItems);
        Assert.Equal(0u, model.SelectedAudioPid);
    }

    [Fact]
    public void CaptureAudioInputsAppearBeforeProcessEntriesAndMapByStableId()
    {
        var model = Model(
            () => [Window(1, "game", 11, "Game title")],
            () => [],
            () => [],
            () =>
            [
                AudioDevice("card-b", "Bravo Card"),
                AudioDevice("card-a", "Alpha Card"),
            ]);
        model.RefreshAll();

        Assert.Equal(
            [
                "No audio",
                "Captured window's audio",
                "Desktop audio (all sound)",
                "Capture device audio - Alpha Card",
                "Capture device audio - Bravo Card",
                "game - Game title",
            ],
            model.AudioDisplayItems);

        model.SelectAudioIndex(4);

        Assert.Equal("input:card-b", model.AudioKey);
        Assert.Equal("card-b", model.SelectedAudioInputDeviceId);
        Assert.Equal(0u, model.SelectedAudioPid);
        Assert.Equal(4, model.SelectedAudioIndex);

        model.SelectKind(SourceKind.CaptureDevice);

        Assert.Equal(3, model.SelectedAudioIndex);
    }

    [Fact]
    public void PersistedCaptureAudioSelectionSurvivesRestartAndDeviceReordering()
    {
        List<AudioInputDeviceDescription> devices =
        [
            AudioDevice("card-a", "Alpha Card"),
            AudioDevice("card-b", "Bravo Card"),
        ];
        var model = Model(() => [], () => [], () => [], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.CaptureDevice);
        model.SelectAudioIndex(3);
        string persistedKey = model.AudioKey;

        devices =
        [
            AudioDevice("card-b", "Aardvark Card"),
            AudioDevice("card-a", "Zulu Card"),
        ];
        var restored = Model(() => [], () => [], () => [], () => devices);
        restored.RefreshAll();
        restored.SelectKind(SourceKind.CaptureDevice);
        restored.SelectAudioKey(persistedKey);

        Assert.Equal("input:card-b", persistedKey);
        Assert.Equal("card-b", restored.SelectedAudioInputDeviceId);
        Assert.Equal(2, restored.SelectedAudioIndex);
    }

    [Fact]
    public void MissingCaptureAudioInputFallsBackWithoutAProcessUnavailableWarning()
    {
        List<AudioInputDeviceDescription> devices = [AudioDevice("card-a", "Capture Card")];
        var model = Model(() => [], () => [], () => [], () => devices);
        model.RefreshAll();
        model.SelectKind(SourceKind.CaptureDevice);
        model.SelectAudioIndex(2);

        devices = [];
        model.RefreshAudioInputDevices();

        Assert.Equal(SourceSelectionModel.CapturedWindowAudioKey, model.AudioKey);
        Assert.Null(model.SelectedAudioInputDeviceId);
        Assert.False(model.IsSelectedAudioProcessUnavailable);
        Assert.Equal(0, model.SelectedAudioIndex);
    }

    [Theory]
    [InlineData((int)SourceKind.Window, 0, SourceSelectionModel.NoAudioKey)]
    [InlineData((int)SourceKind.Window, 1, SourceSelectionModel.CapturedWindowAudioKey)]
    [InlineData((int)SourceKind.Window, 2, SourceSelectionModel.DesktopAudioKey)]
    [InlineData((int)SourceKind.Window, 3, "game")]
    [InlineData((int)SourceKind.Monitor, 0, SourceSelectionModel.NoAudioKey)]
    [InlineData((int)SourceKind.Monitor, 1, SourceSelectionModel.DesktopAudioKey)]
    [InlineData((int)SourceKind.Monitor, 2, "game")]
    [InlineData((int)SourceKind.CaptureDevice, 0, SourceSelectionModel.NoAudioKey)]
    [InlineData((int)SourceKind.CaptureDevice, 1, SourceSelectionModel.DesktopAudioKey)]
    [InlineData((int)SourceKind.CaptureDevice, 2, "game")]
    public void AudioSelectionIndexesMapToDisplayedItems(
        int kindValue,
        int selectedIndex,
        string expectedKey)
    {
        var model = Model(() => [Window(1, "game", 11)], () => []);
        model.RefreshAll();
        model.SelectKind((SourceKind)kindValue);

        model.SelectAudioIndex(selectedIndex);

        Assert.Equal(expectedKey, model.AudioKey);
        Assert.Equal(selectedIndex, model.SelectedAudioIndex);
    }

    [Theory]
    [InlineData(SourceSelectionModel.CapturedWindowAudioKey, 1, 0)]
    [InlineData(SourceSelectionModel.NoAudioKey, 0, 0)]
    [InlineData(SourceSelectionModel.DesktopAudioKey, 2, 1)]
    [InlineData("game", 3, 2)]
    public void PersistedAudioSelectionSurvivesShareKindRoundTrips(
        string persistedKey,
        int windowIndex,
        int nonWindowIndex)
    {
        var model = Model(() => [Window(1, "game", 11)], () => []);
        model.RefreshAll();
        model.SelectAudioKey(persistedKey);

        Assert.Equal(windowIndex, model.SelectedAudioIndex);

        model.SelectKind(SourceKind.Monitor);

        Assert.Equal(persistedKey, model.AudioKey);
        Assert.Equal(nonWindowIndex, model.SelectedAudioIndex);

        model.SelectKind(SourceKind.CaptureDevice);

        Assert.Equal(persistedKey, model.AudioKey);
        Assert.Equal(nonWindowIndex, model.SelectedAudioIndex);

        model.SelectKind(SourceKind.Window);

        Assert.Equal(persistedKey, model.AudioKey);
        Assert.Equal(windowIndex, model.SelectedAudioIndex);
    }

    [Fact]
    public void DesktopAudioKeyPersistsWithoutAWindowAndDoesNotCollideWithProcessKeys()
    {
        var model = Model(() => [Window(1, "mode", 11)], () => []);
        model.RefreshAll();

        model.SelectAudioIndex(2);
        string persistedKey = model.AudioKey;
        model.RefreshWindows();
        var restored = Model(() => [], () => []);
        restored.RefreshAll();
        restored.SelectAudioKey(persistedKey);

        Assert.Equal(SourceSelectionModel.DesktopAudioKey, persistedKey);
        Assert.Equal("mode:desktop", persistedKey);
        Assert.Equal(2, model.SelectedAudioIndex);
        Assert.Equal(2, restored.SelectedAudioIndex);
        Assert.True(model.IsDesktopAudioSelected);
        Assert.True(restored.IsDesktopAudioSelected);
        Assert.False(model.IsSelectedAudioProcessUnavailable);
        Assert.Equal(0u, model.SelectedAudioPid);
    }

    [Fact]
    public void FiltersOwnAndSurfacelessWindowsAndSortsTheRest()
    {
        var model = Model(
            () =>
            [
                Window(1, "Zulu", 99),
                Window(2, "NoWidth", 20, width: 1),
                Window(3, "NoHeight", 30, height: 1),
                Window(4, "Bravo", 40),
                Window(5, "alpha", 50),
            ],
            () => [],
            ownPid: 99);

        model.RefreshWindows();

        Assert.Collection(
            model.Windows,
            window => Assert.Equal("alpha", window.ProcessName),
            window => Assert.Equal("Bravo", window.ProcessName));
    }

    [Fact]
    public void RefreshRepicksSameProcessWhenSelectedWindowHandleVanishes()
    {
        List<WindowDescription> windows =
        [
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        var model = Model(() => windows, () => []);
        model.RefreshWindows();
        model.SelectWindowIndex(1);

        windows =
        [
            Window(1, "Alpha", 11),
            Window(3, "Bravo", 33),
        ];
        model.RefreshWindows();

        Assert.Equal(new IntPtr(3), model.SelectedWindow?.Handle);
    }

    [Fact]
    public void RefreshFallsBackWhenSelectedSourcesAndAudioDisappear()
    {
        List<WindowDescription> windows =
        [
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        List<MonitorDescription> monitors =
        [
            Monitor(10, "DISPLAY-A"),
            Monitor(20, "DISPLAY-B"),
        ];
        var model = Model(() => windows, () => monitors);
        model.RefreshAll();
        model.SelectWindowIndex(1);
        model.SelectMonitorIndex(1);
        model.SelectAudioIndex(4);

        windows = [Window(1, "Alpha", 11)];
        monitors = [Monitor(10, "DISPLAY-A")];
        model.RefreshAll();

        Assert.Equal(new IntPtr(1), model.SelectedWindow?.Handle);
        Assert.Equal("DISPLAY-A", model.SelectedMonitor?.DeviceName);
        Assert.Equal(SourceSelectionModel.CapturedWindowAudioKey, model.AudioKey);
        Assert.Equal(1, model.SelectedAudioIndex);
    }

    [Fact]
    public void SelectionSnapshotAppliesByStableIdentityAfterReordering()
    {
        List<WindowDescription> windows =
        [
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        List<MonitorDescription> monitors =
        [
            Monitor(10, "DISPLAY-A"),
            Monitor(20, "DISPLAY-B"),
        ];
        var model = Model(() => windows, () => monitors);
        model.RefreshAll();
        SourceSelectionModel dialog = model.CreateFreshSnapshot();
        dialog.SelectKind(SourceKind.Monitor);
        dialog.SelectWindowIndex(1);
        dialog.SelectMonitorIndex(1);
        dialog.SelectAudioIndex(3);

        windows =
        [
            Window(3, "Aardvark", 33),
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        monitors =
        [
            Monitor(20, "DISPLAY-B"),
            Monitor(10, "DISPLAY-A"),
        ];
        model.RefreshAll();

        SourceSelectionApplyFailure result = model.TryApplySelection(dialog.CurrentSelection);

        Assert.Equal(SourceSelectionApplyFailure.None, result);
        Assert.Equal(SourceKind.Monitor, model.Kind);
        Assert.Equal(new IntPtr(2), model.SelectedWindow?.Handle);
        Assert.Equal("DISPLAY-B", model.SelectedMonitor?.DeviceName);
        Assert.Equal("Bravo", model.AudioKey);
    }

    [Fact]
    public void DisappearedActiveSourceRejectsWithoutPartialSelectionWrite()
    {
        List<WindowDescription> windows =
        [
            Window(1, "Alpha", 11),
            Window(2, "Bravo", 22),
        ];
        var model = Model(() => windows, () => [Monitor(10, "DISPLAY-A")]);
        model.RefreshAll();
        model.SelectAudioIndex(0);
        SourceSelectionModel dialog = model.CreateFreshSnapshot();
        dialog.SelectWindowIndex(1);
        dialog.SelectAudioIndex(4);

        windows = [Window(1, "Alpha", 11)];
        model.RefreshWindows();
        SourceSelection before = model.CurrentSelection;

        SourceSelectionApplyFailure result = model.TryApplySelection(dialog.CurrentSelection);

        Assert.Equal(SourceSelectionApplyFailure.WindowUnavailable, result);
        Assert.Equal(before, model.CurrentSelection);
    }

    [Fact]
    public void DisappearedCaptureDeviceRejectsWithoutPartialSelectionWrite()
    {
        List<CaptureDeviceDescription> devices =
        [
            Device("device-a", "Alpha"),
            Device("device-b", "Bravo"),
        ];
        var model = Model(() => [], () => [], () => devices);
        model.RefreshAll();
        SourceSelectionModel dialog = model.CreateFreshSnapshot();
        dialog.SelectKind(SourceKind.CaptureDevice);
        dialog.SelectCaptureDeviceIndex(1);

        devices = [Device("device-a", "Alpha")];
        model.RefreshCaptureDevices();
        Assert.Equal("device-a", model.SelectedCaptureDevice?.SymbolicLink);
        SourceSelection before = model.CurrentSelection;

        SourceSelectionApplyFailure result = model.TryApplySelection(dialog.CurrentSelection);

        Assert.Equal(SourceSelectionApplyFailure.CaptureDeviceUnavailable, result);
        Assert.Equal(before, model.CurrentSelection);
    }

    private static SourceSelectionModel Model(
        Func<List<WindowDescription>> windows,
        Func<List<MonitorDescription>> monitors,
        uint ownPid = 999) => new(windows, monitors, ownPid);

    private static SourceSelectionModel Model(
        Func<List<WindowDescription>> windows,
        Func<List<MonitorDescription>> monitors,
        Func<List<CaptureDeviceDescription>> captureDevices,
        uint ownPid = 999) => new(windows, monitors, captureDevices, ownPid);

    private static SourceSelectionModel Model(
        Func<List<WindowDescription>> windows,
        Func<List<MonitorDescription>> monitors,
        Func<List<CaptureDeviceDescription>> captureDevices,
        Func<List<AudioInputDeviceDescription>> audioInputDevices,
        uint ownPid = 999) =>
        new(windows, monitors, captureDevices, audioInputDevices, ownPid);

    private static WindowDescription Window(
        int handle,
        string process,
        uint pid,
        string title = "Title",
        int width = 1920,
        int height = 1080) =>
        new(new IntPtr(handle), title, process, pid, width, height);

    private static MonitorDescription Monitor(int handle, string deviceName) =>
        new(new IntPtr(handle), deviceName, 1920, 1080, false);

    private static CaptureDeviceDescription Device(string symbolicLink, string friendlyName) =>
        new(symbolicLink, friendlyName);

    private static AudioInputDeviceDescription AudioDevice(string id, string friendlyName) =>
        new(id, friendlyName, AudioEndpointFormFactor.Unknown);
}
