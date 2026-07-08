using System.Net;

namespace StreamHost.Server;

/// <summary>
/// Minimal HttpListener server: serves the viewer page from wwwroot and upgrades
/// /ws to a WebSocket handled by the Broadcaster. No ASP.NET dependency.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Broadcaster _broadcaster;
    private readonly string _wwwroot;
    public string BoundPrefix { get; }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    public WebServer(int port, Broadcaster broadcaster)
    {
        _broadcaster = broadcaster;
        _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // A failed Start() poisons the HttpListener, so each attempt gets a fresh one.
        (_listener, BoundPrefix) = TryBind($"http://+:{port}/") ?? TryBind($"http://localhost:{port}/")
            ?? throw new InvalidOperationException($"Could not bind port {port}");
        if (BoundPrefix.Contains("localhost"))
        {
            Console.WriteLine($"[http] WARNING: bound to localhost only. Run setup.bat {port} as administrator, then restart the stream.");
        }
    }

    private static (HttpListener, string)? TryBind(string prefix)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(prefix);
            listener.Start();
            return (listener, prefix);
        }
        catch (HttpListenerException)
        {
            try { listener.Close(); } catch { }
            return null;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/ws")
            {
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }
                var wsContext = await context.AcceptWebSocketAsync(null);
                await _broadcaster.HandleClientAsync(wsContext.WebSocket, ct);
                return;
            }

            if (path == "/api/stats")
            {
                // CORS so the grid page (served by a friend's host) can probe us
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                string name = _broadcaster.StreamName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                await WriteResponseAsync(context, 200, "application/json",
                    $"{{\"name\":\"{name}\",\"state\":\"{_broadcaster.State}\",\"viewers\":{_broadcaster.ViewerCount},\"fragments\":{Interlocked.Read(ref _broadcaster.FragmentsSent)}," +
                    $"\"sourceFps\":{_broadcaster.SourceFps},\"dupPct\":{_broadcaster.DupPercent},\"audio\":{(_broadcaster.HasAudio ? "true" : "false")}}}");
                return;
            }

            if (path == "/") path = "/index.html";
            if (path == "/grid") path = "/grid.html";
            string file = Path.GetFullPath(Path.Combine(_wwwroot, path.TrimStart('/')));
            if (!file.StartsWith(_wwwroot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                await WriteResponseAsync(context, 404, "text/plain", "not found");
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            byte[] bytes = await File.ReadAllBytesAsync(file, ct);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
