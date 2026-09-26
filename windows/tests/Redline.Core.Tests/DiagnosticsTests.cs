// The diagnostics file is only worth having if it survives what breaks log files: a
// truncated tail, a rotation, a secret that should never have been written.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"diag-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "diagnostics.ndjson");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DiagnosticsLog Log(DiagLevel level = DiagLevel.debug) => new(_path, "1.2.3", level);

    [Fact]
    public void AnEventRoundTripsThroughTheFile()
    {
        var l = Log();
        l.Error("feed.parse_failed", "sidecar is not valid JSON", new() { ["bytes"] = "12" });
        var events = l.Read();
        Assert.Single(events);
        Assert.Equal("feed.parse_failed", events[0].Code);
        Assert.Equal(DiagLevel.error, events[0].Level);
        Assert.Equal("12", events[0].Context["bytes"]);
        Assert.Equal("1.2.3", events[0].Version);
    }

    [Fact]
    public void OneEventPerLine()
    {
        var l = Log();
        l.Info("a.b", "first");
        l.Info("c.d", "second");
        var text = File.ReadAllText(_path);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TimestampsAreUTC()
    {
        var l = Log();
        l.Log(DiagLevel.info, "a.b", "stamped", now: DateTimeOffset.FromUnixTimeSeconds(0));
        Assert.Equal("1970-01-01T00:00:00Z", l.Read().FirstOrDefault()?.At);
    }

    [Fact]
    public void LevelBelowTheMinimumIsDropped()
    {
        var l = Log(DiagLevel.warn);
        l.Debug("a.b", "noise");
        l.Info("a.b", "noise");
        l.Warn("a.b", "kept");
        Assert.Equal(new[] { "a.b" }, l.Read().Select(e => e.Code));
        Assert.Equal(DiagLevel.warn, l.Read().FirstOrDefault()?.Level);
    }

    [Fact]
    public void ReadFiltersByLevel()
    {
        var l = Log();
        l.Info("quiet.one", "x");
        l.Error("loud.one", "y");
        Assert.Equal(new[] { "loud.one" }, l.Read(minimumLevel: DiagLevel.error).Select(e => e.Code));
    }

    /// A crash mid-write leaves half a line. The events before it must still be readable,
    /// because that is exactly when someone goes looking.
    [Fact]
    public void ATruncatedTailDoesNotHideEarlierEvents()
    {
        var l = Log();
        l.Error("first.event", "kept");
        l.Error("second.event", "kept");
        var text = File.ReadAllText(_path);
        text += "{\"at\":\"2026-01-01T00:00:00Z\",\"lev";
        File.WriteAllText(_path, text);
        Assert.Equal(new[] { "first.event", "second.event" }, l.Read().Select(e => e.Code));
    }

    [Fact]
    public void AttemptRecordsAThrownErrorAndReturnsNull()
    {
        var l = Log();
        // int? rather than int: Attempt<T> returns default(T), which is only null for a nullable
        var result = l.Attempt<int?>("thing.failed", new() { ["path"] = "/tmp/x" },
            () => throw new InvalidOperationException("boom"));
        Assert.Null(result);
        var e = l.Read().FirstOrDefault();
        Assert.Equal("thing.failed", e?.Code);
        Assert.Equal(DiagLevel.error, e?.Level);
        Assert.Equal("/tmp/x", e?.Context["path"]);
        Assert.True(e?.Context.ContainsKey("error"));
    }

    [Fact]
    public void AttemptPassesTheValueThroughOnSuccess()
    {
        var l = Log();
        Assert.Equal(42, l.Attempt("nope", null, () => 42));
        Assert.Empty(l.Read());
    }

    [Fact]
    public void TallyGroupsByCodeMostFrequentFirst()
    {
        var l = Log();
        l.Error("often", "x");
        l.Error("often", "x");
        l.Error("often", "x");
        l.Warn("rare", "y");
        var rows = l.Tally();
        Assert.Equal(new[] { "often", "rare" }, rows.Select(r => r.Code));
        Assert.Equal(new[] { 3, 1 }, rows.Select(r => r.Count));
    }

    [Fact]
    public void RotationKeepsEarlierEventsReadable()
    {
        var l = Log();
        // Past the 1 MB threshold, with each event well under it
        var filler = new string('x', 4_000);
        for (var i = 0; i < 300; i++) l.Error($"bulk.{i}", filler);
        Assert.True(File.Exists(_path + ".1"), "expected a rotated generation");
        var codes = l.Read().Select(e => e.Code).ToHashSet();
        Assert.Contains("bulk.299", codes); // newest event should survive rotation
        Assert.True(codes.Count > 1);
    }

    [Fact]
    public void ConcurrentWritesDoNotCorruptLines()
    {
        var l = Log();
        Parallel.For(0, 100, i => l.Error($"race.{i % 5}", "hit"));
        Assert.Equal(100, l.Read().Count); // every line should parse
    }

    // MARK: - Redaction

    [Fact]
    public void HomePathIsAbbreviated()
    {
        var home = RedlineHome.AccountHome;
        Assert.Equal("~/.config/redline", Redaction.Scrub($"{home}/.config/redline"));
    }

    [Fact]
    public void ATokenIsNotWrittenToTheFile()
    {
        var l = Log();
        l.Error("oauth.refresh_failed", "bearer sk-ant-oat01-abc123def456ghi789jkl012");
        var text = l.Read().FirstOrDefault()?.Message ?? "";
        Assert.DoesNotContain("abc123def456ghi789jkl012", text);
        Assert.Contains("<redacted>", text);
    }

    [Fact]
    public void RedactionKeepsTheUsefulPart()
    {
        var output = Redaction.Scrub("token refresh failed with sk-ant-oat01-abc123def456ghi789");
        Assert.Contains("refresh", output);
        Assert.Contains("failed", output);
    }

    [Fact]
    public void OrdinaryMessagesAreUntouched()
    {
        Assert.Equal("sidecar is not valid JSON", Redaction.Scrub("sidecar is not valid JSON"));
    }
}
