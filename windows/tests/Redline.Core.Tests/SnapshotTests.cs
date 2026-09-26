using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-snap-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "snapshot.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Snapshot Sample(DateTimeOffset? updatedAt = null)
    {
        var agg = new Agg { Io = 6044, Cost = 1.25, HasUnpriced = true };
        var limits = new[]
        {
            new LimitWindow("Claude", "five_hour", 12, DateTimeOffset.FromUnixTimeSeconds(4_000_000_000)),
            new LimitWindow("Codex", "five_hour", 41, null),
            new LimitWindow("Claude", "seven_day", 8, null),
        };
        return new Snapshot(updatedAt ?? DateTimeOffset.UtcNow, limits, agg, agg);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var snap = Sample();
        Assert.True(SnapshotStore.Write(snap, _file));
        var back = SnapshotStore.Read(_file);
        // Dates must survive the ISO8601 round trip
        Assert.Equal(snap, back);
    }

    /// Claude's windows age on their own clock: the feed writes only while Claude Code runs,
    /// so their stamp must survive the trip and drive staleness independently of updatedAt.
    [Fact]
    public void ClaudeLimitsAsOfRoundTripsAndAges()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_755_400_000);
        var agg = new Agg { Io = 1 };
        var snap = new Snapshot(now, new[] { new LimitWindow("Claude", "seven_day", 30, null) },
                                agg, agg, claudeLimitsAsOf: now.AddSeconds(-1200));
        Assert.True(SnapshotStore.Write(snap, _file));
        var back = SnapshotStore.Read(_file);
        Assert.NotNull(back);
        Assert.Equal(snap.ClaudeLimitsAsOf, back!.ClaudeLimitsAsOf);
        // The snapshot itself is current
        Assert.False(back.IsStale(now));
        // While its Claude windows are twenty minutes old
        Assert.True(back.ClaudeLimitsAreStale(now));
        Assert.False(back.ClaudeLimitsAreStale(now, tolerance: 1800));
    }

    /// Snapshots written before the field existed decode with no stamp, and no stamp must
    /// read as "nothing to age" rather than stale.
    [Fact]
    public void OlderSnapshotsWithoutTheStampStillDecode()
    {
        var snap = Sample();
        Assert.True(SnapshotStore.Write(snap, _file));
        var json = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        json.Remove("claudeLimitsAsOf");
        File.WriteAllText(_file, json.ToJsonString());
        var back = SnapshotStore.Read(_file);
        Assert.NotNull(back);
        Assert.Null(back!.ClaudeLimitsAsOf);
        Assert.False(back.ClaudeLimitsAreStale());
    }

    [Fact]
    public void TimestampsAreTruncatedToWholeSeconds()
    {
        // ISO8601 carries no sub-second precision, so the constructor normalizes
        var odd = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_987);
        Assert.Equal(1_700_000_000, Sample(odd).UpdatedAt.ToUnixTimeSeconds());
        Assert.Equal(0, Sample(odd).UpdatedAt.Millisecond);
    }

    [Fact]
    public void WorstPicksHighestUtilizationAcrossProviders()
    {
        Assert.Equal("Codex", Sample().Worst("five_hour")?.Provider);
        Assert.Equal(41, Sample().Worst("five_hour")?.Utilization);
        Assert.Null(Sample().Worst("nope"));
    }

    [Fact]
    public void CarriesUnpricedFlagSoTheWidgetCanMarkPartialCost()
    {
        Assert.True(Sample().Today.HasUnpriced);
    }

    [Fact]
    public void StalenessIsDetectable()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = Sample(now.AddSeconds(-60));
        var old = Sample(now.AddSeconds(-3600));
        Assert.False(fresh.IsStale(now));
        // A widget must be able to say the reading is not current
        Assert.True(old.IsStale(now));
    }

    [Fact]
    public void ReadingAbsentFileReturnsNil()
    {
        Assert.Null(SnapshotStore.Read(Path.Combine(_dir, "absent.json")));
    }

    [Fact]
    public void FallsBackToUserPathWithoutAnAppGroup()
    {
        var path = SnapshotStore.Url(appGroup: null).Replace('\\', '/');
        Assert.EndsWith(".local/share/redline/snapshot.json", path);
    }

    /// Windows addition: the keys a Swift-written file uses, so either side reads the other's.
    [Fact]
    public void KeysMatchSwiftCodable()
    {
        var json = Sample(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)).ToJson();
        Assert.Equal("2023-11-14T22:13:20Z", Json.Str(json["updatedAt"]));
        Assert.Equal(new[] { "updatedAt", "limits", "today", "week", "todayByProvider", "weekByProvider" },
                     json.Select(kv => kv.Key));
        var first = json["limits"]![0]!.AsObject();
        Assert.Equal(new[] { "provider", "key", "utilization", "resetsAt" }, first.Select(kv => kv.Key));
        Assert.Equal(new[] { "io", "cost", "hasUnpriced" }, json["today"]!.AsObject().Select(kv => kv.Key));
        // Nil optionals are omitted, as JSONEncoder does
        Assert.Null(json["limits"]![1]!["resetsAt"]);
    }

    /// Swift's ISO8601 decoder rejects fractional seconds and missing required keys alike.
    [Fact]
    public void RequiredKeysAreRequired()
    {
        File.WriteAllText(_file, """{"updatedAt":"2026-08-12T10:00:00Z","limits":[]}""");
        Assert.Null(SnapshotStore.Read(_file));
    }
}

public sealed class SnapshotLocationTests
{
    [Fact]
    public void WidgetContainerPathIsInsideTheWidgetsOwnSandbox()
    {
        var p = SnapshotStore.WidgetContainerUrl.Replace('\\', '/');
        // Must address the widget's container, the one place it can always read
        Assert.Contains($"Library/Containers/{SnapshotStore.WidgetBundleID}/Data", p);
        Assert.EndsWith("redline/snapshot.json", p);
    }

    [Fact]
    public void OwnContainerIsTriedFirstWhenReading()
    {
        var first = SnapshotStore.ReadCandidates.FirstOrDefault();
        Assert.NotNull(first);
        // A sandboxed widget can only rely on its own container
        Assert.Equal(SnapshotStore.LocalAppSupportUrl, first);
    }

    [Fact]
    public void UserPathIsAlwaysAWriteTarget()
    {
        Assert.Contains(SnapshotStore.UserUrl, SnapshotStore.WriteTargets);
    }
}

public sealed class SnapshotProviderViewTests
{
    private static Snapshot Make()
    {
        var today = new Agg { Io = 1000 };
        today.Providers["Claude"] = new ProviderUsage { Io = 900, Cost = 9 };
        today.Providers["Codex"] = new ProviderUsage { Io = 100, Cost = 0 };
        var week = new Agg { Io = 5000 };
        week.Providers["Claude"] = new ProviderUsage { Io = 5000, Cost = 50 };
        var limits = new[]
        {
            new LimitWindow("Claude", "five_hour", 10, null),
            new LimitWindow("Claude", "seven_day", 20, null),
            new LimitWindow("Codex", "seven_day", 80, null),
        };
        return new Snapshot(DateTimeOffset.UtcNow, limits, today, week,
            ollama: new Snapshot.OllamaSection(true, "0.5.0",
                new[] { new Snapshot.OllamaSection.RunningModel("qwen3:30b", 100, 0.5) },
                3, 30_000_000_000));
    }

    [Fact]
    public void WorstScopedToOneProvider()
    {
        var s = Make();
        // Unscoped takes the max
        Assert.Equal(80, s.Worst("seven_day")?.Utilization);
        // Scoped must ignore other providers
        Assert.Equal(20, s.Worst("seven_day", provider: "Claude")?.Utilization);
    }

    [Fact]
    public void WindowsForProvider()
    {
        Assert.Single(Make().Windows("Codex"));
        Assert.Empty(Make().Windows("Ollama"));
    }

    [Fact]
    public void PerProviderTotals()
    {
        var s = Make();
        Assert.Equal(900, s.TodayFor("Claude").Io);
        // Null means every provider
        Assert.Equal(1000, s.TodayFor(null).Io);
        // An absent provider reports zero, not a crash
        Assert.Equal(0, s.WeekFor("Codex").Io);
    }

    [Fact]
    public void OllamaSectionSurvivesTheRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redline-{Guid.NewGuid()}.json");
        try
        {
            Assert.True(SnapshotStore.Write(Make(), path));
            var back = SnapshotStore.Read(path);
            Assert.Equal("0.5.0", back?.Ollama?.Version);
            Assert.Equal(0.5, back?.Ollama?.Running.First().VramShare);
            Assert.Equal(3, back?.Ollama?.DownloadedCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OlderSnapshotWithoutNewFieldsStillDecodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redline-{Guid.NewGuid()}.json");
        try
        {
            // A file written before per-provider totals and Ollama existed
            File.WriteAllText(path, """
                {"updatedAt":"2026-08-12T10:00:00Z","limits":[],
                 "today":{"io":1,"cost":0,"hasUnpriced":false},
                 "week":{"io":2,"cost":0,"hasUnpriced":false}}
                """);
            var back = SnapshotStore.Read(path);
            // A widget must not go blank because the format grew
            Assert.NotNull(back);
            Assert.Null(back!.Ollama);
            Assert.Equal(0, back.TodayFor("Claude").Io);
        }
        finally { File.Delete(path); }
    }
}

public sealed class SnapshotWindowIdentityTests
{
    [Fact]
    public void WindowIdIncludesProvider()
    {
        var claude = new Snapshot.Window("Claude", "seven_day", 5, null);
        var codex = new Snapshot.Window("Codex", "seven_day", 17, null);
        // A shared key rendered one provider twice and dropped the other
        Assert.NotEqual(claude.Id, codex.Id);
    }

    [Fact]
    public void WindowIdsAreUniqueAcrossATypicalSnapshot()
    {
        var windows = new[]
        {
            new Snapshot.Window("Claude", "five_hour", 8, null),
            new Snapshot.Window("Claude", "seven_day", 5, null),
            new Snapshot.Window("Codex", "seven_day", 17, null),
        };
        Assert.Equal(windows.Length, windows.Select(w => w.Id).Distinct().Count());
    }

    [Fact]
    public void WindowDisplayNameIsReadableAndProviderAware()
    {
        Assert.Equal("Session · 5h", new Snapshot.Window("Claude", "five_hour", 0, null).DisplayName);
        Assert.Equal("Week", new Snapshot.Window("Codex", "seven_day", 0, null).DisplayName);
        Assert.Equal("Week · all models", new Snapshot.Window("Claude", "seven_day", 0, null).DisplayName);
    }
}
