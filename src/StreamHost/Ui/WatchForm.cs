using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using StreamHost.Util;

namespace StreamHost.Ui;

/// <summary>
/// In-app viewer: the grid page hosted in WebView2 (the Chromium engine that
/// ships with Windows), so everyone gets identical playback behavior without
/// depending on whichever browser a friend prefers. Stream links added here
/// persist in the app's own profile. The grid page's header carries a
/// "Find streams" row; discovery runs natively here (it needs the tailscale
/// CLI) and the results are pushed into the page, so the row lives in the
/// same bar as the add-stream controls and hides together with them. It
/// searches once when the window opens, then only on the button.
/// </summary>
public sealed class WatchForm : Form
{
    private static readonly Color Bg = Color.FromArgb(31, 33, 39);
    private static readonly Color Dim = Color.FromArgb(150, 156, 165);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly int _extraPort;
    private bool _finding;
    private bool _webReady;
    private bool _searchedOnce;

    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Dim,
        BackColor = Bg,
        Visible = false,
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public WatchForm(int extraPort = 8093)
    {
        _extraPort = extraPort;
        Text = "StreamHost - Watch";
        Size = new Size(1200, 750);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(_web);
        Controls.Add(_fallback);

        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    private async Task InitAsync()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamHost", "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "streamhost.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            // "open ↗" tiles and any target=_blank go to the default browser
            // instead of spawning bare WebView2 popup windows.
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            // The page's Find-streams button asks us to search (the probing
            // needs the tailscale CLI, which the page can't run).
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    if (doc.RootElement.TryGetProperty("find", out JsonElement _))
                        _ = FindStreamsAsync();
                }
                catch { }
            };
            // The initial search waits for the page: results land in its DOM.
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                _webReady = true;
                if (e.IsSuccess && !_searchedOnce)
                {
                    _searchedOnce = true;
                    _ = FindStreamsAsync();
                }
            };
            _web.CoreWebView2.Navigate("http://streamhost.local/grid.html");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[watch] WebView2 unavailable: {ex.Message}");
            _web.Visible = false;
            _fallback.Text = "The in-app viewer needs the WebView2 runtime (part of Windows 10/11 updates).\n" +
                             "Open stream links in a browser instead, or install the runtime from Microsoft.";
            _fallback.Visible = true;
        }
    }

    /// <summary>Probes tailnet peers (plus remembered endpoints) and pushes the
    /// results into the grid page's Find-streams row. Clicking a result there
    /// adds the stream to the existing grid — never a new window.</summary>
    private async Task FindStreamsAsync()
    {
        if (_finding || !_webReady || IsDisposed) return;
        _finding = true;
        try
        {
            await PushFinderAsync([], "searching…");
            var streams = await StreamDiscovery.FindAsync([8093, _extraPort], CancellationToken.None);
            if (IsDisposed) return;
            await PushFinderAsync(streams,
                streams.Count == 0 ? "no live streams found on your tailnet" : "");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                try { await PushFinderAsync([], $"search failed: {ex.Message}"); } catch { }
        }
        finally
        {
            _finding = false;
        }
    }

    private Task PushFinderAsync(IReadOnlyList<DiscoveredStream> streams, string status)
    {
        var payload = streams.Select(s => new { name = s.StreamName, url = s.Url, peer = s.PeerName, viewers = s.Viewers });
        return _web.CoreWebView2.ExecuteScriptAsync(
            $"window.setFoundStreams({JsonSerializer.Serialize(payload)}, {JsonSerializer.Serialize(status)})");
    }
}
