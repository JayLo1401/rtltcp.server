using System.Text.Json;

namespace RtlTcp.Core.Update;

/// <summary>One downloadable release file.</summary>
public sealed record ReleasePackage(string Version, string Url, string Sha256);

/// <summary>Reads the update manifests (latest/version.json) published by the release workflow.</summary>
public static class ReleaseManifest
{
    /// <summary>"1.2.3", "1.2.3-dev", "v1.2.3" → 1.2.3; null if not a version.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var end = s.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) s = s[..end];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    public static bool IsNewer(string? candidate, string? current) =>
        ParseVersion(candidate) is { } c && (ParseVersion(current) is not { } cur || c > cur);

    /// <summary>windows/release/latest/version.json: {"version","url","sha256"}.</summary>
    public static ReleasePackage? ParseWindows(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return Package(Str(r, "version"), Str(r, "url"), Str(r, "sha256"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>linux/release/latest/version.json: {"version", "debs": {"amd64": {"url","sha256"}, …}}.</summary>
    public static ReleasePackage? ParseLinux(string json, string debArch)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("debs", out var debs) || !debs.TryGetProperty(debArch, out var deb)) return null;
            return Package(Str(r, "version"), Str(deb, "url"), Str(deb, "sha256"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ReleasePackage? Package(string? version, string? url, string? sha) =>
        ParseVersion(version) is null || string.IsNullOrEmpty(url) || sha is not { Length: 64 }
            || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")
            ? null
            : new ReleasePackage(version!, url, sha.ToLowerInvariant());
}

/// <summary>When the automatic update runs: daily at a fixed local time, within a one-hour window.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static bool TryParseTime(string? s, out TimeOnly time) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], out time);

    /// <summary>True while <paramref name="now"/> is inside today's window and today's run has not happened yet.</summary>
    public static bool IsDue(DateTime now, TimeOnly time, DateOnly? lastRunDay)
    {
        var today = DateOnly.FromDateTime(now);
        if (lastRunDay == today) return false;
        var start = today.ToDateTime(time);
        return now >= start && now < start + Window;
    }

    /// <summary>In the last minutes of the window: update even if a client is still connected.</summary>
    public static bool IsWindowEnding(DateTime now, TimeOnly time)
    {
        var start = DateOnly.FromDateTime(now).ToDateTime(time);
        return now >= start + Window - TimeSpan.FromMinutes(5);
    }
}
