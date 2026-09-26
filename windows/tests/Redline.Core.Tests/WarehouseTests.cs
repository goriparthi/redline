// The history store. Its whole reason to exist is that transcripts get pruned, so what
// survives a shrinking scan is the thing worth testing hardest.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class WarehouseTests : IDisposable
{
    private readonly string _dir;
    private readonly Warehouse _warehouse;
    private readonly Config _config = new();

    public WarehouseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-warehouse-" + Guid.NewGuid());
        _warehouse = new Warehouse(_dir);
    }

    public void Dispose()
    {
        _warehouse.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DateTimeOffset At(double seconds) => Database.FromSeconds(seconds);

    private static Entry MakeEntry(DateTimeOffset ts, string model = "claude-sonnet-5",
                                   int input = 1000, int output = 100) =>
        new("Claude", Guid.NewGuid().ToString(), ts, model, input, output, 0, 0, 0);

    [Fact]
    public void RollupGroupsByUTCDay()
    {
        // 23:30 UTC and 00:30 UTC the next day are different days regardless of where the
        // machine is, which is the point of fixing the basis.
        var a = At(1_760_000_000);
        var b = a.AddSeconds(86400);
        var records = Warehouse.Rollup(new[] { MakeEntry(a), MakeEntry(b) }, _config);
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].Day, records[1].Day);
        Assert.Equal(1000, records[0].Input);
    }

    [Fact]
    public void APrunedTranscriptCannotEraseTheDay()
    {
        var day = At(1_760_000_000);
        _warehouse.Merge(new[] { MakeEntry(day, input: 5000) }, _config);
        // The transcript that record came from is gone, so a later pass brings nothing for
        // that day at all. The day still has to read the same afterwards.
        _warehouse.Merge(new[] { MakeEntry(day.AddSeconds(86400), input: 10) }, _config);
        var stored = _warehouse.Records(since: day, until: day);
        Assert.Single(stored);
        Assert.Equal(5000, stored[0].Input); // a day already recorded must not shrink
    }

    [Fact]
    public void TwoRecordsOnOneDayAddUpRatherThanCompete()
    {
        // Distinct records of the same day are both facts about it; entries are deduped, so they sum
        var day = At(1_760_000_000);
        _warehouse.Merge(new[] { MakeEntry(day, input: 100) }, _config);
        _warehouse.Merge(new[] { MakeEntry(day, input: 900) }, _config);
        Assert.Equal(1000, _warehouse.Load().FirstOrDefault()?.Input);
    }

    [Fact]
    public void TheSameRecordTwiceIsCountedOnce()
    {
        var day = At(1_760_000_000);
        var e = MakeEntry(day, input: 500);
        _warehouse.Merge(new[] { e }, _config);
        _warehouse.Merge(new[] { e }, _config);
        Assert.Equal(500, _warehouse.Load().FirstOrDefault()?.Input);
        Assert.Equal(1, _warehouse.EntryCount);
    }

    [Fact]
    public void RecordsWithNoIdDedupeOnTheirOrigin()
    {
        // Codex and Ollama records carry no id. Re-reading the same byte of the same file
        // must not double the day.
        var day = At(1_760_000_000);
        var e = new Entry("Codex", null, day, "gpt-5", 100, 10, 0, 0, 0, "/tmp/session.jsonl#4096");
        Assert.Equal(1, _warehouse.Ingest(new[] { e }));
        Assert.Equal(0, _warehouse.Ingest(new[] { e })); // same file position, same record
        Assert.Equal(1, _warehouse.EntryCount);
    }

    [Fact]
    public void DailyRowOutlivesTheEntriesItWasBuiltFrom()
    {
        // The seam the two tables exist for: entries age out on retention, the day does not.
        var day = At(1_760_000_000);
        _warehouse.Merge(new[] { MakeEntry(day, input: 4000) }, _config);
        var removed = _warehouse.PruneEntries(
            now: day.AddSeconds((double)(Warehouse.EntryRetentionDays + 2) * 86400));
        Assert.Equal(1, removed);
        Assert.Equal(0, _warehouse.EntryCount);
        // the rollup is the long memory and must survive its own entries
        Assert.Equal(4000, _warehouse.Records(since: day, until: day).FirstOrDefault()?.Input);
    }

    [Fact]
    public void EntriesComeBackInsideTheirWindow()
    {
        var b = At(1_760_000_000);
        _warehouse.Ingest(new[] { MakeEntry(b, input: 1), MakeEntry(b.AddSeconds(7200), input: 2) });
        Assert.Equal(2, _warehouse.Entries().Count);
        Assert.Single(_warehouse.Entries(since: b.AddSeconds(3600)));
        Assert.Empty(_warehouse.Entries(provider: "Codex"));
    }

    [Fact]
    public void IngestMarksTrackWhereReadingStopped()
    {
        var mark = new IngestMark("/tmp/a.jsonl", "Claude", 900, 880, At(1_760_000_000));
        _warehouse.SetIngestMark(mark);
        Assert.Equal(880, _warehouse.IngestMark("/tmp/a.jsonl")?.ByteOffset);
        _warehouse.ForgetIngestMarks(new HashSet<string>(), "Claude");
        // a transcript that is gone should not leave a mark behind
        Assert.Null(_warehouse.IngestMark("/tmp/a.jsonl"));
    }

    [Fact]
    public void LastKnownLimitsSurviveAQuietPoll()
    {
        var now = At(1_760_000_000);
        var w = new LimitWindow("Codex", "five_hour", 33, now.AddSeconds(3600), Provenance.Official);
        _warehouse.RecordLimits(new[] { w }, at: now);
        var stored = _warehouse.LatestLimits("Codex");
        Assert.NotNull(stored);
        Assert.Single(stored!.Value.Windows);
        Assert.Equal(33, stored.Value.Windows[0].Utilization);
        Assert.Equal(now, stored.Value.At);
    }

    [Fact]
    public void UnpricedModelIsCountedButNotCosted()
    {
        var records = Warehouse.Rollup(
            new[] { MakeEntry(DateTimeOffset.UtcNow, model: "some-unlisted-model") }, _config);
        Assert.Equal(1100, records.FirstOrDefault()?.Io);
        Assert.Equal(0, records.FirstOrDefault()?.Cost);
        Assert.False(records.FirstOrDefault()?.Priced ?? true);
        Assert.Equal(Provenance.Unknown, records.FirstOrDefault()?.CostBasis);
    }

    [Fact]
    public void LimitSamplesSkipUnchangedReadings()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddSeconds(3600);
        var w = new LimitWindow("Claude", "five_hour", 40, reset, Provenance.Official);
        Assert.Equal(1, _warehouse.RecordLimits(new[] { w }, at: now));
        // an identical reading a minute later says nothing new
        Assert.Equal(0, _warehouse.RecordLimits(new[] { w }, at: now.AddSeconds(60)));
        var moved = new LimitWindow("Claude", "five_hour", 41, reset, Provenance.Official);
        Assert.Equal(1, _warehouse.RecordLimits(new[] { moved }, at: now.AddSeconds(120)));
        Assert.Equal(2, _warehouse.LimitSamples().Count);
    }

    [Fact]
    public void LimitSamplesRefuseToGoBackwards()
    {
        var now = DateTimeOffset.UtcNow;
        var w = new LimitWindow("Codex", "seven_day", 10, now.AddSeconds(86400), Provenance.Official);
        _warehouse.RecordLimits(new[] { w }, at: now);
        var older = new LimitWindow("Codex", "seven_day", 9, now.AddSeconds(86400), Provenance.Official);
        Assert.Equal(0, _warehouse.RecordLimits(new[] { older }, at: now.AddSeconds(-3600)));
    }

    [Fact]
    public void SamplesFromDifferentWindowInstancesAreNotTheSame()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LimitSample(now, "Claude", "five_hour", 90, now.AddSeconds(60), Provenance.Official);
        var b = new LimitSample(now.AddSeconds(120), "Claude", "five_hour", 2,
                                now.AddSeconds(18000), Provenance.Official);
        Assert.False(a.SameWindowInstance(b));
    }

    [Fact]
    public void ByDayFoldsProvidersTogether()
    {
        var day = At(1_760_000_000);
        var codex = new Entry("Codex", "x", day, "gpt-5", 5, 5, 0, 0, 0);
        var records = Warehouse.Rollup(new[] { MakeEntry(day), codex }, _config);
        var byDay = Warehouse.ByDay(records);
        Assert.Single(byDay);
        Assert.Equal(1110, byDay[0].Io);
        Assert.False(byDay[0].Priced); // one unpriced model makes the day's total partial
    }
}
