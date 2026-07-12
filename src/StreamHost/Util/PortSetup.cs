using System.Diagnostics;

namespace StreamHost.Util;

/// <summary>
/// Elevated port configuration, same two steps as packaging/setup.bat: reserve
/// the http.sys URL for the streaming user and open the firewall. The firewall
/// rule is scoped to Tailscale only by default; the caller opts in to the
/// private LAN ranges via <paramref name="allowLan"/>. Runs silently (no
/// console) because it is invoked via UAC from the "Fix access" button; the
/// caller reads the exit code.
/// Exit codes: 0 ok, 2 urlacl failed, 3 firewall rule failed.
/// </summary>
public static class PortSetup
{
    public const string TailscaleRange = "100.64.0.0/10";
    public const string LanRanges = "192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";

    public static int Run(int port, string? user, bool allowLan)
    {
        // The UAC prompt may elevate as a different account than the one that
        // will run StreamHost, so the unelevated app passes its own identity.
        user ??= $"{Environment.UserDomainName}\\{Environment.UserName}";

        // Capture whatever account currently holds this URL so a half-failed
        // setup can put it back instead of leaving nothing reserved. Reading the
        // reservation needs no admin; the mutating netsh calls below do. Restore
        // re-grants the prior USER account only — it does not preserve a full
        // custom SDDL — which is acceptable here and far better than silently
        // destroying a foreign reservation.
        string? priorUser = ReadReservationUser(port);

        Exec($"http delete urlacl url=http://+:{port}/");
        if (Exec($"http add urlacl url=http://+:{port}/ user=\"{user}\"") != 0)
        {
            if (priorUser is not null)
                Exec($"http add urlacl url=http://+:{port}/ user=\"{priorUser}\"");
            return 2;
        }

        string remoteip = allowLan ? $"{TailscaleRange},{LanRanges}" : TailscaleRange;
        Exec($"advfirewall firewall delete rule name=\"StreamHost {port}\"");
        if (Exec($"advfirewall firewall add rule name=\"StreamHost {port}\" dir=in action=allow protocol=TCP localport={port} remoteip={remoteip}") != 0)
        {
            // Roll the urlacl back to how we found it: drop ours, restore theirs.
            Exec($"http delete urlacl url=http://+:{port}/");
            if (priorUser is not null)
                Exec($"http add urlacl url=http://+:{port}/ user=\"{priorUser}\"");
            return 3;
        }

        return 0;
    }

    /// <summary>The account currently granted this URL, or null if none is
    /// reserved. Parsed from the "User:" line of `netsh http show urlacl`, which
    /// is a read-only call and needs no elevation.</summary>
    private static string? ReadReservationUser(int port)
    {
        try
        {
            var r = ProcessRunner.Run("netsh", $"http show urlacl url=http://+:{port}/", 5000);
            foreach (var line in r.StdOut.Split('\n'))
            {
                int idx = line.IndexOf("User:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string owner = line[(idx + 5)..].Trim();
                return owner.Length == 0 ? null : owner;
            }
        }
        catch { }
        return null;
    }

    private static int Exec(string netshArgs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", netshArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return -1;
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
