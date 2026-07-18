using System.Text.Json;

namespace Spectari.Util;

public sealed record DiscoveredStream(string PeerName, string Url, string StreamName, int Viewers, bool IsLocal);

/// <summary>
/// Finds live Spectari streams on the tailnet: asks the Tailscale CLI for
/// peers ("tailscale status --json"), probes each one's /api/stats, and keeps a
/// small remembered-endpoints file as a fallback for peers the CLI misses
/// (e.g. custom ports seen before). Hosts hand their current view key to
/// tailnet callers, so the returned URLs are directly watchable.
/// </summary>
public static class StreamDiscovery
{
    private static readonly string RememberedPath = AppPaths.PeersFile;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Probe every known peer on the given ports; returns live streams only.</summary>
    public static async Task<List<DiscoveredStream>> FindAsync(IEnumerable<int> ports, CancellationToken ct)
    {
        var endpoints = new Dictionary<string, (string PeerName, bool IsLocal)>(StringComparer.OrdinalIgnoreCase);
        int[] portList = ports.Distinct().ToArray();

        // The CLI call can block for seconds; keep it off the caller's (UI) thread.
        foreach (var (ip, name, isLocal) in await Task.Run(GetTailnetPeers, ct))
            foreach (int port in portList)
                endpoints.TryAdd($"{ip}:{port}", (name, isLocal));

        // Loopback too: your own stream should show up even when the Tailscale
        // backend is stopped (no tailnet IPs to enumerate then).
        foreach (int port in portList)
            endpoints.TryAdd($"127.0.0.1:{port}", (Environment.MachineName, true));

        var localAddresses = LocalAddresses();
        foreach (string remembered in LoadRemembered())
        {
            string host = remembered.Split(':')[0];
            endpoints.TryAdd(remembered, (host, localAddresses.Contains(host)));
        }

        var probes = endpoints.Select(async kv =>
        {
            try
            {
                using var res = await Http.GetAsync($"http://{kv.Key}/api/stats", ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.GetProperty("state").GetString() != "live") return null;
                string? key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
                string url = $"http://{kv.Key}/" + (key is null ? "" : $"?k={key}");
                return new DiscoveredStream(
                    kv.Value.PeerName,
                    url,
                    root.TryGetProperty("name", out var n) ? n.GetString() ?? kv.Value.PeerName : kv.Value.PeerName,
                    root.TryGetProperty("viewers", out var v) ? v.GetInt32() : 0,
                    kv.Value.IsLocal);
            }
            catch { return null; }
        }).ToArray();

        // The host's own stream can answer on both its tailnet IP and loopback;
        // collapse duplicates by view key, keeping the shareable (non-loopback)
        // address. Keyless streams have nothing safe to collapse on.
        var found = (await Task.WhenAll(probes)).Where(s => s is not null).Select(s => s!)
            .GroupBy(s => KeyOf(s.Url) ?? s.Url)
            .Select(g =>
            {
                var selected = g.OrderBy(s => new Uri(s.Url).Host == "127.0.0.1" ? 1 : 0).First();
                return selected with { IsLocal = g.Any(s => s.IsLocal) };
            })
            .ToList();
        SaveRemembered(found.Select(s => new Uri(s.Url).Authority).Where(a => !a.StartsWith("127.")));
        return found.OrderBy(s => s.StreamName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? KeyOf(string url)
    {
        try
        {
            string q = new Uri(url).Query;
            return q.StartsWith("?k=") ? q[3..] : null;
        }
        catch { return null; }
    }

    private static HashSet<string> LocalAddresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    addresses.Add(address.Address.ToString());
        }
        catch { }
        return addresses;
    }

    /// <summary>IPv4 + hostname of every online tailnet peer, this machine included
    /// (your own stream showing up in the finder is correct). Empty when the
    /// Tailscale CLI is missing or errors - remembered endpoints still get probed.</summary>
    private static List<(string Ip, string Name, bool IsLocal)> GetTailnetPeers()
    {
        string? json = RunTailscale("status --json");
        if (json is null) return [];
        var peers = new List<(string, string, bool)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            void AddNode(JsonElement node, bool isLocal)
            {
                if (node.TryGetProperty("Online", out var online) && !online.GetBoolean()) return;
                if (!node.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) return;
                string name = node.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? "?" : "?";
                foreach (var ip in ips.EnumerateArray())
                {
                    string? s = ip.GetString();
                    if (s is not null && !s.Contains(':')) { peers.Add((s, name, isLocal)); break; }
                }
            }
            if (root.TryGetProperty("Self", out var self)) AddNode(self, true);
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
                foreach (var p in peerMap.EnumerateObject()) AddNode(p.Value, false);
        }
        catch { }
        return peers;
    }

    /// <summary>One line: each online peer and whether traffic to it goes direct
    /// or through a DERP relay - the first thing to check when a viewer stutters.
    /// Endpoint addresses are deliberately omitted (support bundles get pasted
    /// around). "idle" means no recent traffic, so the path is undecided.</summary>
    public static string DescribeTailnetPaths()
    {
        string? json = RunTailscale("status --json");
        if (json is null) return "tailscale CLI not found or not running";
        var parts = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            // A stopped/logged-out backend still lists peers; reporting them as
            // "idle" would read like a healthy-but-quiet link. Say what's wrong.
            if (doc.RootElement.TryGetProperty("BackendState", out var bs)
                && bs.GetString() is string state && state != "Running")
                return $"tailscale not running (state: {state})";
            if (doc.RootElement.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in peerMap.EnumerateObject())
                {
                    var node = p.Value;
                    if (node.TryGetProperty("Online", out var online) && !online.GetBoolean()) continue;
                    string name = node.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? "?" : "?";
                    bool active = node.TryGetProperty("Active", out var a) && a.GetBoolean();
                    string curAddr = node.TryGetProperty("CurAddr", out var ca) ? ca.GetString() ?? "" : "";
                    string relay = node.TryGetProperty("Relay", out var r) ? r.GetString() ?? "" : "";
                    string path = !active ? "idle"
                        : curAddr.Length > 0 ? "direct"
                        : relay.Length > 0 ? $"relay ({relay})"
                        : "unknown";
                    parts.Add($"{name}: {path}");
                }
            }
        }
        catch { return "tailscale status unreadable"; }
        return parts.Count == 0 ? "no online peers" : string.Join(", ", parts);
    }

    private static string? RunTailscale(string args)
    {
        string[] candidates =
        [
            "tailscale",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        ];
        foreach (string exe in candidates)
        {
            try
            {
                // Drains both pipes and can't block past 5s - a tailscale that
                // hangs without closing stdout no longer wedges Find Streams.
                var r = ProcessRunner.Run(exe, args, 5000);
                if (!r.TimedOut && r.ExitCode == 0 && r.StdOut.Length > 0) return r.StdOut;
            }
            catch { }
        }
        return null;
    }

    private static List<string> LoadRemembered()
    {
        try
        {
            if (File.Exists(RememberedPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RememberedPath)) ?? [];
        }
        catch { }
        return [];
    }

    private static void SaveRemembered(IEnumerable<string> liveEndpoints)
    {
        try
        {
            var existing = LoadRemembered();
            var merged = existing.Union(liveEndpoints, StringComparer.OrdinalIgnoreCase)
                .TakeLast(32).ToList();
            if (existing.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(RememberedPath)!);
            // Temp-then-swap so a crash mid-write can't corrupt peers.json.
            string tmp = RememberedPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(merged));
            File.Move(tmp, RememberedPath, overwrite: true);
        }
        catch { }
    }
}
