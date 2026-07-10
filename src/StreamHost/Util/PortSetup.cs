using System.Diagnostics;

namespace StreamHost.Util;

/// <summary>
/// Elevated port configuration, same two steps as packaging/setup.bat: reserve
/// the http.sys URL for the streaming user and open the firewall for private
/// address ranges only. Runs silently (no console) because it is invoked via
/// UAC from the "Fix access" button; the caller reads the exit code.
/// Exit codes: 0 ok, 2 urlacl failed, 3 firewall rule failed.
/// </summary>
public static class PortSetup
{
    public const string FirewallRemoteRanges = "100.64.0.0/10,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12";

    public static int Run(int port, string? user)
    {
        // The UAC prompt may elevate as a different account than the one that
        // will run StreamHost, so the unelevated app passes its own identity.
        user ??= $"{Environment.UserDomainName}\\{Environment.UserName}";

        Exec($"http delete urlacl url=http://+:{port}/");
        if (Exec($"http add urlacl url=http://+:{port}/ user=\"{user}\"") != 0)
            return 2;

        Exec($"advfirewall firewall delete rule name=\"StreamHost {port}\"");
        if (Exec($"advfirewall firewall add rule name=\"StreamHost {port}\" dir=in action=allow protocol=TCP localport={port} remoteip={FirewallRemoteRanges}") != 0)
            return 3;

        return 0;
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
