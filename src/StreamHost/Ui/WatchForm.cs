using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace StreamHost.Ui;

/// <summary>
/// In-app viewer: the grid page hosted in WebView2 (the Chromium engine that
/// ships with Windows), so everyone gets identical playback behavior without
/// depending on whichever browser a friend prefers. Stream links added here
/// persist in the app's own profile.
/// </summary>
public sealed class WatchForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(150, 156, 165),
        BackColor = Color.FromArgb(31, 33, 39),
        Visible = false,
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public WatchForm()
    {
        Text = "StreamHost — Watch";
        Size = new Size(1200, 750);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(31, 33, 39);
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
}
