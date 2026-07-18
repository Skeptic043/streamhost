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
        Assert.Equal("No audio", model.AudioDisplayItems[0]);
        Assert.Equal("Captured window's audio", model.AudioDisplayItems[1]);
        Assert.Equal("game - Game title", model.AudioDisplayItems[2]);

        model.SelectKind(SourceKind.Monitor);

        Assert.Equal("No audio (monitor share: pick an app below)", model.AudioDisplayItems[1]);
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
        model.SelectAudioIndex(3);

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
        dialog.SelectAudioIndex(3);

        windows = [Window(1, "Alpha", 11)];
        model.RefreshWindows();
        SourceSelection before = model.CurrentSelection;

        SourceSelectionApplyFailure result = model.TryApplySelection(dialog.CurrentSelection);

        Assert.Equal(SourceSelectionApplyFailure.WindowUnavailable, result);
        Assert.Equal(before, model.CurrentSelection);
    }

    private static SourceSelectionModel Model(
        Func<List<WindowDescription>> windows,
        Func<List<MonitorDescription>> monitors,
        uint ownPid = 999) => new(windows, monitors, ownPid);

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
}
