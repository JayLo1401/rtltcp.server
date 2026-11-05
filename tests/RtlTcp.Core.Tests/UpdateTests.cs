using RtlTcp.Core.Update;

namespace RtlTcp.Core.Tests;

public class UpdateTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0-dev", false)]
    [InlineData("1.0.1", "0.0.0-dev", true)]
    [InlineData("kaputt", "1.0.0", false)]
    public void Version_comparison(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseManifest.IsNewer(candidate, current));

    [Fact]
    public void Windows_manifest()
    {
        var p = ReleaseManifest.ParseWindows($$"""{"version":"1.2.0","url":"https://x/setup.exe","sha256":"{{Sha.ToUpperInvariant()}}"}""");
        Assert.Equal(new ReleasePackage("1.2.0", "https://x/setup.exe", Sha), p);
    }

    [Fact]
    public void Linux_manifest_per_architecture()
    {
        var json = $$"""
            {
              "version": "1.2.0",
              "debs": {
                "amd64": {"url": "https://x/a.deb", "sha256": "{{Sha}}"},
                "armhf": {"url": "https://x/h.deb", "sha256": "{{Sha}}"}
              }
            }
            """;
        Assert.Equal("https://x/h.deb", ReleaseManifest.ParseLinux(json, "armhf")!.Url);
        Assert.Null(ReleaseManifest.ParseLinux(json, "arm64"));
    }

    [Theory]
    [InlineData("""{"version":"1.2.0","url":"https://x","sha256":"zu-kurz"}""")]
    [InlineData("""{"version":"x","url":"https://x","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"version":"1.2.0","url":"file:///etc/passwd","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""kein json""")]
    public void Invalid_manifests_are_rejected(string json) => Assert.Null(ReleaseManifest.ParseWindows(json));

    [Theory]
    [InlineData("2026-11-06 03:59", false)]
    [InlineData("2026-11-06 04:00", true)]
    [InlineData("2026-11-06 04:59", true)]
    [InlineData("2026-11-06 05:00", false)]
    public void Schedule_window(string now, bool due) =>
        Assert.Equal(due, UpdateSchedule.IsDue(DateTime.Parse(now), new TimeOnly(4, 0), null));

    [Fact]
    public void Schedule_runs_once_per_day() =>
        Assert.False(UpdateSchedule.IsDue(new DateTime(2026, 11, 6, 4, 30, 0), new TimeOnly(4, 0), new DateOnly(2026, 11, 6)));

    [Theory]
    [InlineData("04:00", true)]
    [InlineData("4:30", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("4 Uhr", false)]
    public void Time_format(string s, bool ok) => Assert.Equal(ok, UpdateSchedule.TryParseTime(s, out _));
}
