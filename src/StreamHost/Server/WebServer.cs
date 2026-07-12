using System.Net;
using System.Text.Json;

namespace StreamHost.Server;

/// <summary>
/// Minimal HttpListener server: serves the viewer page from wwwroot and upgrades
/// /ws to a WebSocket handled by the Broadcaster. No ASP.NET dependency.
/// The viewer page and the WebSocket require the per-session key (?k=) when one
/// is set; /api/stats and static assets stay open so the grid can probe.
/// A null broadcaster runs the server in idle mode: pages are served ungated
/// (they reveal nothing), /api/stats reports state "idle", and /ws refuses —
/// this is the holding page shown while the app is open but not streaming, so
/// tabs opened early connect themselves once a stream starts.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Broadcaster? _broadcaster;
    private readonly string _wwwroot;
    private readonly string? _viewKey;
    private readonly string _idleName;
    public string BoundPrefix { get; }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    public WebServer(int port, Broadcaster? broadcaster, string? viewKey = null, string? idleName = null)
    {
        _broadcaster = broadcaster;
        _viewKey = viewKey;
        _idleName = string.IsNullOrWhiteSpace(idleName) ? Environment.MachineName : idleName.Trim();
        _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // A failed Start() poisons the HttpListener, so each attempt gets a fresh one.
        // The wildcard needs a urlacl (or admin); localhost always works for this
        // user — so falling through to localhost means "no remote access yet",
        // while failing BOTH means the port itself is taken by another process.
        (_listener, BoundPrefix) = TryBind($"http://+:{port}/") ?? TryBind($"http://localhost:{port}/")
            ?? throw new InvalidOperationException(DescribeBindFailure(port));
        if (BoundPrefix.Contains("localhost"))
        {
            Console.WriteLine(_broadcaster is null
                ? $"[http] holding page is localhost-only for now — port {port} opens up via Fix access or setup.bat."
                : $"[http] WARNING: bound to localhost only. Run setup.bat {port} as administrator, then restart the stream.");
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

    /// <summary>Turns a total bind failure into an actionable message: name the
    /// likely culprit (another StreamHost answers /api/stats) so the user closes
    /// the right thing instead of force-quitting blindly.</summary>
    private static string DescribeBindFailure(int port)
    {
        string culprit = ProbedAnotherStreamHost(port)
            ? "another StreamHost is already running on this port (check the taskbar or Task Manager and close it, or pick a different port)"
            : "another program is using this port — close it or pick a different port";
        return $"Port {port} is already in use: {culprit}.";
    }

    /// <summary>Best-effort: does something on this port answer like our own
    /// /api/stats? A short timeout keeps the error path snappy.</summary>
    private static bool ProbedAnotherStreamHost(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };
            string body = http.GetStringAsync($"http://localhost:{port}/api/stats").GetAwaiter().GetResult();
            return body.Contains("\"state\"");
        }
        catch { return false; }
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
        // Hoisted so the catch can name the failing route. AbsolutePath excludes
        // the query string, so the viewer key never lands in the error log.
        string path = "/";
        try
        {
            path = context.Request.Url?.AbsolutePath ?? "/";
            bool keyOk = _viewKey is null || context.Request.QueryString["k"] == _viewKey;

            if (path == "/ws")
            {
                if (_broadcaster is null) // idle: nothing to stream yet
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }
                if (!keyOk)
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }
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
                var stats = _broadcaster is null
                    ? new Dictionary<string, object?>
                    {
                        ["name"] = _idleName,
                        ["state"] = "idle",
                        ["viewers"] = 0,
                    }
                    : new Dictionary<string, object?>
                    {
                        ["name"] = _broadcaster.StreamName,
                        ["state"] = _broadcaster.State,
                        ["viewers"] = _broadcaster.ViewerCount,
                        ["fragments"] = Interlocked.Read(ref _broadcaster.FragmentsSent),
                        ["sourceFps"] = _broadcaster.SourceFps,
                        ["dupPct"] = _broadcaster.DupPercent,
                        ["audio"] = _broadcaster.HasAudio,
                    };
                // Hand the current key to trusted callers only: Tailscale peers
                // (the tailnet requires device approval) and this machine. That is
                // what lets the stream finder and saved grid tiles keep working
                // after a restart rotates the key, without exposing it to the LAN.
                if (_viewKey is not null && IsTrustedCaller(context.Request.RemoteEndPoint?.Address))
                    stats["key"] = _viewKey;
                await WriteResponseAsync(context, 200, "application/json", JsonSerializer.Serialize(stats));
                return;
            }

            if (path == "/")
            {
                if (!keyOk)
                {
                    await WriteResponseAsync(context, 403, "text/plain; charset=utf-8",
                        "This stream needs its viewer key. Ask the streamer for the current link (it ends in ?k=...).");
                    return;
                }
                path = "/index.html";
            }
            if (path == "/grid") path = "/grid.html";
            string file = Path.GetFullPath(Path.Combine(_wwwroot, path.TrimStart('/')));
            // Trailing separator so a sibling like "wwwroot-x" can't pass the prefix check.
            string root = _wwwroot.EndsWith(Path.DirectorySeparatorChar) ? _wwwroot : _wwwroot + Path.DirectorySeparatorChar;
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
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
            // Log before responding: a bare 500 with no detail erased exactly the
            // info a public bug report needs. ConsoleMirror tees this to the log.
            Console.Error.WriteLine($"[http] {path} handler error: {ex}");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    /// <summary>Tailscale CGNAT range (100.64.0.0/10) or loopback.</summary>
    private static bool IsTrustedCaller(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
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
