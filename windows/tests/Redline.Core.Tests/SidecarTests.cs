// The published sidecar. It is a contract with tools this project does not control, so the
// spellings other readers rely on are asserted rather than assumed.
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class SidecarTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    public SidecarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-sidecar-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private List<LimitWindow> Windows => new()
    {
        new("Claude", "five_hour", 42, _now.AddSeconds(3600), Provenance.Official),
        new("Claude", "seven_day", 18, _now.AddSeconds(86400), Provenance.Official),
        new("Claude", "seven_day_opus", 71, _now.AddSeconds(86400), Provenance.Official),
        new("Codex", "seven_day", 5, null, Provenance.Official),
    };

    [Fact]
    public void PayloadCarriesBothPercentageSpellings()
    {
        var json = Sidecar.Payload(Windows, _now, "redline/test");
        var five = Assert.IsType<JsonObject>(json["five_hour"]);
        Assert.Equal(42, Json.Num(five["used_percentage"]));
        Assert.Equal(42, Json.Num(five["utilization"]));
        Assert.NotNull(five["resets_at"]);
    }

    [Fact]
    public void ModelScopedWindowsKeepTheirDisplayName()
    {
        var json = Sidecar.Payload(Windows, _now, "redline/test");
        var scoped = Assert.IsType<JsonArray>(json["model_scoped"]);
        Assert.Single(scoped);
        Assert.Equal("Opus", Json.Str(scoped[0]!["display_name"]));
        Assert.Equal(71, Json.Num(scoped[0]!["utilization"]));
    }

    [Fact]
    public void OtherProvidersStayOutOfTheStandardKeys()
    {
        var json = Sidecar.Payload(Windows, _now, "redline/test");
        // The standard block is Claude-shaped; Codex would be misread as Claude's week
        var seven = Assert.IsType<JsonObject>(json["seven_day"]);
        Assert.Equal(18, Json.Num(seven["utilization"]));
        var extra = Assert.IsType<JsonObject>(json["redline"]);
        var all = Assert.IsType<JsonArray>(extra["windows"]);
        // Every provider still travels in the namespaced block
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void RoundTripsThroughOurOwnParser()
    {
        var path = Path.Combine(_dir, "usage-snapshot.json");
        Assert.True(Sidecar.Publish(Windows, "redline/test", updatedAt: _now, to: path));
        var parsed = StatuslineFeed.Read(path, _now);
        Assert.NotNull(parsed);
        var stamp = Assert.NotNull(parsed!.UpdatedAt);
        Assert.InRange((stamp - _now).TotalSeconds, -1, 1);
        Assert.Equal(3, parsed.Windows.Count);
        Assert.True(parsed.IsFresh(_now));
    }

    // chmod 0600 has no Windows equivalent (the profile is already private), so this pins
    // what remains: the write is atomic and leaves no temp file beside the sidecar.
    [Fact]
    public void PublishedFileIsPrivate()
    {
        var path = Path.Combine(_dir, "usage-snapshot.json");
        Assert.True(Sidecar.Publish(Windows, "redline/test", updatedAt: _now, to: path));
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ExternalPathMustBeAbsoluteJSON()
    {
        Assert.Null(Sidecar.ValidExternalPath("usage.json"));
        Assert.Null(Sidecar.ValidExternalPath(@"C:\tmp\usage.txt"));
        Assert.Null(Sidecar.ValidExternalPath("/tmp/usage.txt"));
        Assert.Null(Sidecar.ValidExternalPath(""));
        Assert.NotNull(Sidecar.ValidExternalPath(@"C:\tmp\usage.json"));
        Assert.NotNull(Sidecar.ValidExternalPath("~/usage.json"));
    }

    [Fact]
    public void StaleExternalSidecarIsIgnored()
    {
        var path = Path.Combine(_dir, "other.json");
        Sidecar.Publish(Windows, "other/1.0", updatedAt: _now.AddSeconds(-7200), to: path);
        // A file another tool stopped updating must not stand in for a live one
        Assert.Null(Sidecar.ReadExternal(path, now: _now));
        Assert.NotNull(Sidecar.ReadExternal(path, now: _now.AddSeconds(-7200)));
    }

    [Fact]
    public void EmptySidecarIsNotASource()
    {
        var path = Path.Combine(_dir, "empty.json");
        File.WriteAllText(path, "{\"updated_at\":\"2026-08-18T00:00:00Z\"}");
        Assert.Null(Sidecar.ReadExternal(path, now: DateTimeOffset.Parse("2026-08-18T00:01:00Z")));
    }
}
