using System.Globalization;
using System.Text.Json;

namespace Spectari.Util;

/// <summary>Fail-silent lookup and pure numeric comparison for full releases.</summary>
internal static class UpdateChecker
{
    private static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/Skeptic043/spectari/releases/latest");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Client = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    internal const string LatestReleasePageUrl =
        "https://github.com/Skeptic043/spectari/releases/latest";

    /// <summary>Makes one anonymous request and returns only tag_name.</summary>
    internal static async Task<string?> GetLatestReleaseTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
            request.Headers.UserAgent.ParseAdd("Spectari-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return json.RootElement.TryGetProperty("tag_name", out JsonElement tag)
                && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses two to four numeric components, normalizing missing components to
    /// zero. A leading v and local build metadata are ignored.
    /// </summary>
    internal static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string numeric = value.Trim();
        if (numeric.StartsWith('v')) numeric = numeric[1..];

        int metadata = numeric.IndexOf('+');
        if (metadata >= 0)
        {
            if (metadata == 0 || metadata == numeric.Length - 1
                || numeric.IndexOf('+', metadata + 1) >= 0)
                return null;
            numeric = numeric[..metadata];
        }

        string[] parts = numeric.Split('.');
        if (parts.Length is < 2 or > 4) return null;

        int[] components = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture,
                    out components[i]))
                return null;
        }

        return new Version(components[0], components[1], components[2], components[3]);
    }

    /// <summary>True only when both inputs parse and remote is numerically newer.</summary>
    internal static bool IsRemoteVersionNewer(string? current, string? remote)
    {
        Version? currentVersion = ParseVersion(current);
        Version? remoteVersion = ParseVersion(remote);
        return currentVersion is not null && remoteVersion is not null
            && remoteVersion.CompareTo(currentVersion) > 0;
    }

    /// <summary>Canonical four-component value for persisted dismissal state.</summary>
    internal static string? CanonicalVersion(string? value) => ParseVersion(value)?.ToString(4);
}
