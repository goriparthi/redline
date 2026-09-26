// Exercises the on-disk scanners against synthetic transcripts in a temp dir.
using System.Globalization;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

internal static class StoreFixtures
{
    public static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redline-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Remove(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// What ISO8601DateFormatter().string(from:) writes: whole seconds, UTC, trailing Z.
    public static string Iso(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public sealed class CodexStoreTests : IDisposable
{
    private readonly string _dir = StoreFixtures.MakeTempDir();

    public void Dispose() => StoreFixtures.Remove(_dir);

    private void Write(string[] lines, string name = "rollout.jsonl") =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    // resetsAt defaults far into the future so windows survive Unexpired();
    // tests that care about rollover pass a past value explicitly.
    private static string TokenCount(string ts, double pct, int lastInput, int lastCached,
                                     int lastOutput, int reasoning = 0, double resetsAt = 4_000_000_000)
    {
        var payload = new JsonObject
        {
            ["type"] = "token_count",
            ["rate_limits"] = new JsonObject
            {
                ["primary"] = new JsonObject { ["used_percent"] = pct, ["window_minutes"] = 300, ["resets_at"] = resetsAt },
            },
            ["info"] = new JsonObject
            {
                ["last_token_usage"] = new JsonObject
                {
                    ["input_tokens"] = lastInput,
                    ["cached_input_tokens"] = lastCached,
                    ["output_tokens"] = lastOutput,
                    ["reasoning_output_tokens"] = reasoning,
                },
            },
        };
        return new JsonObject { ["type"] = "event_msg", ["timestamp"] = ts, ["payload"] = payload }.ToJsonString();
    }

    [Fact]
    public void ParsesTokensAndSplitsCachedOutOfInput()
    {
        var now = DateTimeOffset.UtcNow;
        Write(new[] { TokenCount(StoreFixtures.Iso(now), 3, 1000, 900, 50, reasoning: 5) });
        var snap = new CodexStore(_dir).Scan(7, now);
        Assert.Single(snap.Entries);
        var e = snap.Entries[0];
        Assert.Equal("Codex", e.Provider);
        Assert.Equal(100, e.Input); // cached tokens must not be billed as fresh input
        Assert.Equal(900, e.CacheRead);
        Assert.Equal(55, e.Output); // reasoning tokens count as output
    }

    [Fact]
    public void SumsPerTurnDeltasRatherThanCumulativeTotals()
    {
        var now = DateTimeOffset.UtcNow;
        Write(new[]
        {
            TokenCount(StoreFixtures.Iso(now.AddSeconds(-60)), 1, 100, 0, 10),
            TokenCount(StoreFixtures.Iso(now), 2, 200, 0, 20),
        });
        var snap = new CodexStore(_dir).Scan(7, now);
        var io = snap.Entries.Sum(e => e.Input + e.Output);
        Assert.Equal(330, io); // each event is a delta, so they add rather than replace
    }

    [Fact]
    public void LimitsComeFromNewestEvent()
    {
        var now = DateTimeOffset.UtcNow;
        Write(new[]
        {
            TokenCount(StoreFixtures.Iso(now.AddSeconds(-600)), 11, 1, 0, 1),
            TokenCount(StoreFixtures.Iso(now), 77, 1, 0, 1),
        });
        var snap = new CodexStore(_dir).Scan(7, now);
        Assert.Equal(77.0, snap.Limits.FirstOrDefault()?.Utilization);
    }

    [Fact]
    public void DropsLimitWindowsThatAlreadyReset()
    {
        // A 5-hour window that reset long ago must not be shown as current, which is exactly
        // what reading days-old Codex sessions off disk would otherwise do.
        var now = DateTimeOffset.UtcNow;
        Write(new[] { TokenCount(StoreFixtures.Iso(now), 88, 10, 0, 1,
                                 resetsAt: now.ToUnixTimeMilliseconds() / 1000.0 - 3600) });
        var snap = new CodexStore(_dir).Scan(7, now);
        Assert.Empty(snap.Limits); // an expired window is meaningless, not just stale
        Assert.Single(snap.Entries); // token totals stay valid regardless
    }

    [Fact]
    public void IgnoresEventsOutsideLookback()
    {
        var now = DateTimeOffset.UtcNow;
        Write(new[] { TokenCount(StoreFixtures.Iso(now.AddDays(-40)), 5, 10, 0, 10) });
        Assert.Empty(new CodexStore(_dir).Scan(7, now).Entries);
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        var snap = new CodexStore(Path.Combine(_dir, "nope")).Scan(7);
        Assert.Empty(snap.Entries);
        Assert.Empty(snap.Limits);
    }

    [Fact]
    public void SkipsMalformedLines()
    {
        var ts = StoreFixtures.Iso(DateTimeOffset.UtcNow);
        Write(new[] { "{not json", "", TokenCount(ts, 1, 10, 0, 1) });
        Assert.Single(new CodexStore(_dir).Scan(7).Entries);
    }
}

public sealed class OllamaStoreTests : IDisposable
{
    private readonly string _dir = StoreFixtures.MakeTempDir();
    private string Log => Path.Combine(_dir, "ollama.jsonl");

    public void Dispose() => StoreFixtures.Remove(_dir);

    [Fact]
    public void ParsesEvalCounts()
    {
        var ts = StoreFixtures.Iso(DateTimeOffset.UtcNow);
        File.WriteAllText(Log, $"{{\"ts\":\"{ts}\",\"model\":\"qwen3-coder:30b\",\"prompt_eval_count\":1200,\"eval_count\":340}}");
        var output = new OllamaStore(Log).Scan(7);
        Assert.Single(output);
        Assert.Equal("Ollama", output[0].Provider);
        Assert.Equal(1200, output[0].Input);
        Assert.Equal(340, output[0].Output);
        Assert.Equal("qwen3-coder:30b", output[0].Model);
    }

    [Fact]
    public void LocalUsageStaysUnpricedSoItIsNotCountedAsSpend()
    {
        var ts = StoreFixtures.Iso(DateTimeOffset.UtcNow);
        File.WriteAllText(Log, $"{{\"ts\":\"{ts}\",\"model\":\"qwen3-coder:30b\",\"prompt_eval_count\":10,\"eval_count\":5}}");
        var entries = new OllamaStore(Log).Scan(7);
        var a = Usage.Aggregate(entries, DateTimeOffset.UtcNow.AddSeconds(-3600), new Config());
        Assert.Equal(15, a.Io);
        Assert.Equal(0.0, a.Cost); // local inference has no dollar cost
        Assert.True(a.HasUnpriced);
    }

    [Fact]
    public void MissingLogIsNotAnError()
    {
        var store = new OllamaStore(Path.Combine(_dir, "absent.jsonl"));
        Assert.False(store.IsConfigured);
        Assert.Empty(store.Scan(7));
    }

    [Fact]
    public void ZeroTokenCallsAreSkipped()
    {
        var ts = StoreFixtures.Iso(DateTimeOffset.UtcNow);
        File.WriteAllText(Log, $"{{\"ts\":\"{ts}\",\"model\":\"m\",\"prompt_eval_count\":0,\"eval_count\":0}}");
        Assert.Empty(new OllamaStore(Log).Scan(7));
    }
}

public sealed class ClaudeStoreTests : IDisposable
{
    private readonly string _dir = StoreFixtures.MakeTempDir();

    public void Dispose() => StoreFixtures.Remove(_dir);

    private static string Line(string id, string requestId, string ts, string model,
                               int input, int output, int cacheRead = 0) =>
        new JsonObject
        {
            ["timestamp"] = ts,
            ["requestId"] = requestId,
            ["message"] = new JsonObject
            {
                ["id"] = id,
                ["model"] = model,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = input,
                    ["output_tokens"] = output,
                    ["cache_read_input_tokens"] = cacheRead,
                },
            },
        }.ToJsonString();

    [Fact]
    public void DedupesRepeatedMessageIdsAcrossFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var l = Line("msg_1", "req_1", StoreFixtures.Iso(now), "claude-sonnet-5", 100, 10);
        // A resumed session copies identical ids into a second transcript
        foreach (var name in new[] { "a.jsonl", "b.jsonl" }) File.WriteAllText(Path.Combine(_dir, name), l);
        var output = new UsageStore(_dir).Scan(7, now);
        Assert.Single(output); // the same message must not be counted twice
    }

    [Fact]
    public void WideningTheRangeRereadsCachedFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var body = string.Join("\n",
            Line("m1", "r1", StoreFixtures.Iso(now.AddDays(-3)), "claude-sonnet-5", 10, 1),
            Line("m2", "r2", StoreFixtures.Iso(now.AddDays(-20)), "claude-sonnet-5", 20, 2));
        File.WriteAllText(Path.Combine(_dir, "a.jsonl"), body);
        // One store across both scans, which is what the dashboard does on a range change
        var store = new UsageStore(_dir);
        Assert.Single(store.Scan(7, now));
        // widening the range must re-read files cached for a narrower one
        Assert.Equal(2, store.Scan(30, now).Count);
        // narrowing again must not leak the wider window's entries
        Assert.Single(store.Scan(7, now));
    }

    [Fact]
    public void SkipsSyntheticModel()
    {
        var ts = StoreFixtures.Iso(DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_dir, "s.jsonl"), Line("m", "r", ts, "<synthetic>", 5, 5));
        Assert.Empty(new UsageStore(_dir).Scan(7));
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        Assert.Empty(new UsageStore(Path.Combine(_dir, "nope")).Scan(7));
    }
}
