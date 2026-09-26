// Incremental reading of append only transcripts. A line read twice doubles a day, a line
// skipped loses one, and a record parsed from half a line looks like data.
using System.Text;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class TranscriptTailTests : IDisposable
{
    private readonly string _dir;

    public TranscriptTailTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-tail-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string text, string name = "t.jsonl")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static void Append(string text, string path) =>
        File.AppendAllText(path, text, new UTF8Encoding(false));

    [Fact]
    public void ReadsEveryCompleteLineWithItsOffset()
    {
        var path = Write("one\ntwo\nthree\n");
        var seen = new List<(string, long)>();
        var next = TranscriptTail.Read(path, 0, (l, o) => seen.Add((l, o)));
        Assert.Equal(new[] { "one", "two", "three" }, seen.Select(s => s.Item1));
        Assert.Equal(new long[] { 0, 4, 8 }, seen.Select(s => s.Item2));
        Assert.Equal(14, next);
    }

    [Fact]
    public void ResumesWhereItStopped()
    {
        var path = Write("one\ntwo\n");
        var first = TranscriptTail.Read(path, 0, (_, _) => { });
        Append("three\n", path);
        var seen = new List<string>();
        var next = TranscriptTail.Read(path, first, (l, _) => seen.Add(l));
        Assert.Equal(new[] { "three" }, seen); // already read lines must not be read again
        Assert.Equal(14, next);
    }

    [Fact]
    public void APartialTrailingLineIsLeftForNextTime()
    {
        // A transcript being written right now ends mid record. Half a JSON object parsed
        // once is a bug that hides until the day it matters.
        var path = Write("one\ntwo\nthr");
        var seen = new List<string>();
        var next = TranscriptTail.Read(path, 0, (l, _) => seen.Add(l));
        Assert.Equal(new[] { "one", "two" }, seen);
        Assert.Equal(8, next); // the offset must stop at the last complete line

        Append("ee\n", path);
        var rest = new List<string>();
        TranscriptTail.Read(path, next, (l, _) => rest.Add(l));
        Assert.Equal(new[] { "three" }, rest); // the line arrives whole on the next pass
    }

    [Fact]
    public void ALineLongerThanAChunkSurvives()
    {
        var longLine = new string('x', TranscriptTail.ChunkSize + 1024);
        var path = Write($"short\n{longLine}\nafter\n");
        var seen = new List<string>();
        TranscriptTail.Read(path, 0, (l, _) => seen.Add(l));
        Assert.Equal(3, seen.Count);
        Assert.Equal(longLine.Length, seen[1].Length);
        Assert.Equal("after", seen[2]);
    }

    [Fact]
    public void ATruncatedFileIsReadFromTheStart()
    {
        // Truncation and rewriting both mean the offsets no longer point where we think.
        var mark = new IngestMark("/tmp/x", "Claude", 900, 880, DateTimeOffset.UtcNow);
        Assert.Equal(0, TranscriptTail.StartOffset(mark, 400));
        Assert.Equal(880, TranscriptTail.StartOffset(mark, 1200));
        Assert.Equal(0, TranscriptTail.StartOffset(null, 1200));
    }

    [Fact]
    public void BlankLinesAreSkippedWithoutLosingPosition()
    {
        var path = Write("one\n\ntwo\n");
        var seen = new List<(string, long)>();
        var next = TranscriptTail.Read(path, 0, (l, o) => seen.Add((l, o)));
        Assert.Equal(new[] { "one", "two" }, seen.Select(s => s.Item1));
        Assert.Equal(new long[] { 0, 5 }, seen.Select(s => s.Item2));
        Assert.Equal(9, next);
    }
}

public sealed class ClaudeIngestTests : IDisposable
{
    private readonly string _home;
    private readonly string _projects;
    private readonly Warehouse _warehouse;
    private readonly UsageStore _store;

    public ClaudeIngestTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "redline-ingest-" + Guid.NewGuid());
        _projects = Path.Combine(_home, "projects", "demo");
        Directory.CreateDirectory(_projects);
        _warehouse = new Warehouse(Path.Combine(_home, "history"));
        _store = new UsageStore(Path.Combine(_home, "projects"));
    }

    public void Dispose()
    {
        _warehouse.Dispose();
        try { Directory.Delete(_home, true); } catch { }
    }

    /// One Claude Code assistant line, in the shape the parser actually reads.
    internal static string Line(string id, string ts, int input = 100, int output = 10,
                                string model = "claude-sonnet-5") =>
        $"{{\"timestamp\":\"{ts}\",\"requestId\":\"req_{id}\",\"message\":{{\"id\":\"{id}\"," +
        $"\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output}," +
        "\"cache_read_input_tokens\":0}}}";

    private string WriteTranscript(IEnumerable<string> lines, string name = "a.jsonl")
    {
        var path = Path.Combine(_projects, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static void Append(IEnumerable<string> lines, string path) =>
        File.AppendAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    [Fact]
    public void FirstPassReadsEverythingAndSecondPassReadsNothing()
    {
        WriteTranscript(new[] { Line("a", "2026-08-18T10:00:00.000Z"),
                                Line("b", "2026-08-18T10:05:00.000Z") });
        Assert.Equal(2, _store.Ingest(_warehouse));
        Assert.Equal(0, _store.Ingest(_warehouse)); // an unchanged transcript costs a stat and nothing else
        Assert.Equal(2, _warehouse.EntryCount);
    }

    [Fact]
    public void OnlyNewLinesAreIngested()
    {
        var path = WriteTranscript(new[] { Line("a", "2026-08-18T10:00:00.000Z") });
        Assert.Equal(1, _store.Ingest(_warehouse));
        Append(new[] { Line("b", "2026-08-18T11:00:00.000Z") }, path);
        Assert.Equal(1, _store.Ingest(_warehouse));
        Assert.Equal(2, _warehouse.EntryCount);
    }

    [Fact]
    public void AResumedSessionCopiedIntoASecondFileIsCountedOnce()
    {
        // Resumed sessions copy identical message ids between transcripts. The id is the
        // dedup key, so the copy is recognised even though its byte position differs.
        var line = Line("shared", "2026-08-18T10:00:00.000Z");
        WriteTranscript(new[] { line }, "one.jsonl");
        WriteTranscript(new[] { line }, "two.jsonl");
        Assert.Equal(1, _store.Ingest(_warehouse));
        Assert.Equal(1, _warehouse.EntryCount);
    }

    [Fact]
    public void UsageSurvivesTheTranscriptBeingDeleted()
    {
        // The whole reason the store exists: Claude Code prunes its own projects directory.
        var path = WriteTranscript(new[] { Line("a", "2026-08-18T10:00:00.000Z", input: 4321) });
        _store.Ingest(_warehouse);
        _warehouse.Merge(_warehouse.Entries(), new Config());
        File.Delete(path);
        Assert.Equal(0, _store.Ingest(_warehouse));
        Assert.Equal(4321, _warehouse.Entries().FirstOrDefault()?.Input);
        Assert.Null(_warehouse.IngestMark(path)); // the mark for a pruned transcript is dropped
    }

    [Fact]
    public void ATranscriptRewrittenSmallerIsReadAgainFromTheStart()
    {
        var path = WriteTranscript(new[] { Line("a", "2026-08-18T10:00:00.000Z"),
                                           Line("b", "2026-08-18T10:05:00.000Z") });
        _store.Ingest(_warehouse);
        // Rewritten with different content, shorter than what was already read
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Line("c", "2026-08-18T12:00:00.000Z") + "\n"));
        Assert.Equal(1, _store.Ingest(_warehouse));
        Assert.Equal(3, _warehouse.EntryCount); // the earlier records are still held
    }

    [Fact]
    public void SyntheticModelsAndZeroUsageAreIgnored()
    {
        WriteTranscript(new[]
        {
            Line("a", "2026-08-18T10:00:00.000Z", model: "<synthetic>"),
            Line("b", "2026-08-18T10:01:00.000Z", input: 0, output: 0),
            Line("c", "2026-08-18T10:02:00.000Z"),
        });
        Assert.Equal(1, _store.Ingest(_warehouse));
    }

    [Fact]
    public void TailedAndWholeFileParsesAgree()
    {
        var lines = Enumerable.Range(0, 20)
            .Select(i => Line($"m{i}", $"2026-08-18T10:{i:00}:00.000Z")).ToList();
        WriteTranscript(lines);
        _store.Ingest(_warehouse);
        var scanned = _store.Scan(3650, Database.FromSeconds(1_787_000_000));
        Assert.Equal(scanned.Count, _warehouse.EntryCount);
        Assert.Equal(scanned.Sum(e => e.Input), _warehouse.Entries().Sum(e => e.Input));
    }
}
