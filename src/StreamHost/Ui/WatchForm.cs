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
/// persist in the app's own profile. A "Find streams" strip below the grid
/// probes the tailnet for live StreamHost peers and adds them with one click;
/// it searches once when the window opens, then only on the button.
/// </summary>
public sealed class WatchForm : Form
{
    private static readonly Color Bg = Color.FromArgb(31, 33, 39);
    private static readonly Color Card = Color.FromArgb(42, 45, 53);
    private static readonly Color Border = Color.FromArgb(60, 65, 74);
    private static readonly Color Fg = Color.FromArgb(212, 216, 222);
    private static readonly Color Dim = Color.FromArgb(150, 156, 165);
    // Same color as the grid page's header bar so the two read as one chrome.
    private static readonly Color BarBg = Color.FromArgb(22, 24, 28);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _finder = new()
    {
        Dock = DockStyle.Bottom,
        Height = 38,
        Padding = new Padding(6, 6, 6, 0),
        BackColor = BarBg,
        WrapContents = false,
    };
    private readonly Button _findButton = new() { Text = "↻ Find streams", Width = 110, Height = 26 };
    private readonly Label _finderStatus = new() { AutoSize = true, ForeColor = Dim, Margin = new Padding(8, 10, 0, 0) };
    private readonly int _extraPort;
    private bool _finding;
    private bool _webReady;

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
        Text = "StreamHost — Watch";
        Size = new Size(1200, 750);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        StyleButton(_findButton);
        _findButton.Click += (_, _) => _ = FindStreamsAsync();
        _finder.Controls.Add(_findButton);
        _finder.Controls.Add(_finderStatus);

        Controls.Add(_web);
        Controls.Add(_fallback);
        Controls.Add(_finder);

        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    private static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Card;
        b.ForeColor = Fg;
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
            // The grid page reports its add-bar visibility; the finder strip
            // follows it so "Hide bar" clears all the chrome at once.
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    if (doc.RootElement.TryGetProperty("barHidden", out var h))
                        _finder.Visible = !h.GetBoolean();
                }
                catch { }
            };
            _web.CoreWebView2.Navigate("http://streamhost.local/grid.html");
            _webReady = true;
            _ = FindStreamsAsync();
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

    /// <summary>Probes tailnet peers (plus remembered endpoints) and rebuilds the
    /// one-click buttons. Clicking a result adds the stream to the existing grid —
    /// never a new window.</summary>
    private async Task FindStreamsAsync()
    {
        if (_finding || IsDisposed) return;
        _finding = true;
        _finderStatus.Text = "searching…";
        try
        {
            var streams = await StreamDiscovery.FindAsync([8093, _extraPort], CancellationToken.None);
            if (IsDisposed) return;

            for (int i = _finder.Controls.Count - 1; i >= 0; i--)
                if (_finder.Controls[i] is Button b && !ReferenceEquals(b, _findButton))
                    _finder.Controls.RemoveAt(i);

            foreach (var s in streams)
            {
                var b = new Button
                {
                    Text = $"+ {s.StreamName}",
                    AutoSize = true,
                    Height = 26,
                    Margin = new Padding(6, 0, 0, 0),
                    Tag = s.Url,
                };
                StyleButton(b);
                new ToolTip().SetToolTip(b, $"{s.PeerName} · {s.Viewers} watching · click to add to the grid");
                b.Click += (_, _) => AddToGrid((string)b.Tag!);
                _finder.Controls.Add(b);
            }
            _finderStatus.Text = streams.Count == 0 ? "no live streams found on your tailnet" : "";
        }
        catch (Exception ex)
        {
            _finderStatus.Text = $"search failed: {ex.Message}";
        }
        finally
        {
            _finding = false;
        }
    }

    private void AddToGrid(string url)
    {
        if (!_webReady) return;
        try
        {
            _ = _web.CoreWebView2.ExecuteScriptAsync($"window.addStream({JsonSerializer.Serialize(url)})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[watch] add stream failed: {ex.Message}");
        }
    }
}
