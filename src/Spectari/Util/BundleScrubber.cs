using System.Text.RegularExpressions;

namespace Spectari.Util;

/// <summary>
/// Scrubs the "Copy log" support bundle before it hits the clipboard and, from
/// there, a PUBLIC GitHub issue. Moderate level plus path minimization: viewer
/// keys and Tailscale (CGNAT) IPs are masked, Windows paths and the account
/// username collapse to env tokens, and remote peer hostnames are genericized
/// by the caller before the text arrives here. Deliberately KEEPS the shared
/// window title, local machine/stream name, OS version, GPU names, ffmpeg
/// version, and settings values - those are the diagnostic payload, not PII.
/// </summary>
public static class BundleScrubber
{
    // ?k=<token> / &k=<token> in copied links and log lines -> k=[key]. The key
    // alphabet matches SessionConfig.NewViewKey (base64url: A-Za-z0-9_-).
    private static readonly Regex KeyParam =
        new(@"[?&]k=[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    // The shared window title can be a bank tab or a private document name, so it
    // is masked while the [proc] suffix (useful debug context) stays. Non-greedy
    // up to the closing "' [" so a title with an apostrophe (e.g. Bob's Bank) is
    // still fully redacted.
    private static readonly Regex WindowTitle =
        new(@"window '.*?' \[", RegexOptions.Compiled);

    /// <summary>Masks ?k=/&k= viewer-key tokens in one line of text, keeping the
    /// base64url alphabet consistent with <see cref="Scrub"/>. Used by the GUI's
    /// copied-link log lines so a hyphen-containing key can't leak a suffix into
    /// the on-disk log that Scrub then can't recover as one contiguous token.</summary>
    public static string RedactKeyParam(string text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : KeyParam.Replace(text, m => m.Value[0] + "k=[key]");

    // Tailscale CGNAT range 100.64.0.0/10. A broad 100.x.x.x is deliberate: the
    // owner would rather over-mask a stray non-tailscale 100.x than leak a real
    // tailnet address that a pasted bundle carries around.
    private static readonly Regex TailscaleIp =
        new(@"100\.\d{1,3}\.\d{1,3}\.\d{1,3}", RegexOptions.Compiled);

    // ...\Users\<name>\AppData\Roaming|Local -> env token (username disappears).
    // Case-insensitive; the AppData rules must run BEFORE the generic USERPROFILE
    // rule or the latter would truncate the path before AppData is recognized.
    private static readonly Regex AppDataRoaming =
        new(@"[A-Za-z]:\\Users\\[^\\]+\\AppData\\Roaming", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AppDataLocal =
        new(@"[A-Za-z]:\\Users\\[^\\]+\\AppData\\Local", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UserProfile =
        new(@"[A-Za-z]:\\Users\\[^\\]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Scrubs the whole assembled bundle. Never throws and never leaks:
    /// on any failure it returns a short note instead of the raw text, so a bug
    /// in here can't paste an unscrubbed key or path into a public issue.
    /// <paramref name="extraSecrets"/> are exact live secrets (viewer keys) that
    /// are replaced by their literal value; empty/null entries are ignored.</summary>
    public static string Scrub(string text, IEnumerable<string?>? extraSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            string s = text;

            // 1) Exact live secrets first - catches a raw key that appears without
            // the ?k= wrapper. Longest first so a key that is a prefix of another
            // can't leave a tail behind; empties skipped so we never replace "".
            if (extraSecrets is not null)
                foreach (string secret in extraSecrets
                             .Where(x => !string.IsNullOrEmpty(x))
                             .Select(x => x!)
                             .Distinct()
                             .OrderByDescending(x => x.Length))
                    s = s.Replace(secret, "[key]", StringComparison.Ordinal);

            // ...then any remaining ?k=/&k= tokens (e.g. rotated keys in the log).
            s = KeyParam.Replace(s, m => m.Value[0] + "k=[key]");

            // 2) Windows paths / username -> env tokens.
            s = AppDataRoaming.Replace(s, "%AppData%");
            s = AppDataLocal.Replace(s, "%LocalAppData%");
            s = s.Replace(@"C:\Program Files (x86)", "%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase);
            s = s.Replace(@"C:\Program Files", "%ProgramFiles%", StringComparison.OrdinalIgnoreCase);
            s = UserProfile.Replace(s, "%USERPROFILE%");

            // The urlacl owner line "MACHINE\username" (PortSetup builds
            // {UserDomainName}\{UserName}; Open port logs the owner) is the one
            // place the username leaks outside a path. Anchor on the backslash so
            // only that field is touched: the machine name before it stays (kept
            // by design at Moderate), and nothing like "--max-viewers" can match.
            // The domain/machine name is deliberately NOT redacted.
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                s = Regex.Replace(s, @"\\" + Regex.Escape(user) + @"\b", @"\[user]", RegexOptions.IgnoreCase);

            // 3) Tailscale IPs in text and URLs (masks RedactKey's copied-link host).
            s = TailscaleIp.Replace(s, "100.x.x.x");

            // 4) Shared window title -> [title], keeping the [proc] suffix. A title
            // can be a bank tab or private document name; the process name stays as
            // debug context. The machine/stream name is deliberately NOT touched.
            s = WindowTitle.Replace(s, "window '[title]' [");

            return s;
        }
        catch
        {
            return "(bundle scrub failed - use Open logs for raw detail)";
        }
    }
}
