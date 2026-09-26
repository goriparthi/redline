// The command line tool, driven in process against a fixture home: scripts/e2e.sh ported.
// Exit codes are the contract, so every run asserts one before it looks at the text.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class CommandLineToolTests : IDisposable
{
    private readonly string _home;
    private readonly string _claudeDir;
    private readonly string _codexDir;
    private readonly string _dataDir;
    private readonly string _configDir;
    // Midday, so the fixtures three hours back stay inside the same UTC day
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    public CommandLineToolTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "redline-cli-" + Guid.NewGuid());
        _claudeDir = Path.Combine(_home, ".claude", "projects", "demo");
        _codexDir = Path.Combine(_home, ".codex", "sessions", "2026", "08", "18");
        _dataDir = Path.Combine(_home, ".local", "share", "redline");
        _configDir = Path.Combine(_home, ".config", "redline");
        foreach (var d in new[] { _claudeDir, _codexDir, _dataDir, _configDir }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { }
    }

    private RedlineCLI.Result Run(int expected, params string[] args)
    {
        var result = RedlineCLI.Run(args, now: Now, home: _home, timeZone: TimeZoneInfo.Utc);
        Assert.True(result.Code == expected,
            $"{string.Join(' ', args)} exited {result.Code}, expected {expected}\n{result.Text}");
        return result;
    }

    private JsonObject RunJson(int expected, params string[] args) =>
        JsonNode.Parse(Run(expected, args).Text)!.AsObject();

    private static string Iso(int minutesAgo) =>
        Now.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);

    private static string ClaudeLine(string id, string ts, int input = 1000, int output = 100) =>
        new JsonObject
        {
            ["timestamp"] = ts, ["requestId"] = "req_" + id,
            ["message"] = new JsonObject
            {
                ["id"] = id, ["model"] = "claude-sonnet-5",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = input, ["output_tokens"] = output, ["cache_read_input_tokens"] = 0,
                },
            },
        }.ToJsonString() + "\n";

    private static string CodexLine(string ts, int used) =>
        new JsonObject
        {
            ["timestamp"] = ts,
            ["payload"] = new JsonObject
            {
                ["type"] = "token_count", ["model"] = "gpt-5",
                ["rate_limits"] = new JsonObject
                {
                    ["primary"] = new JsonObject
                    {
                        ["used_percent"] = used, ["window_minutes"] = 300, ["resets_in_seconds"] = 7200,
                    },
                },
                ["info"] = new JsonObject
                {
                    ["last_token_usage"] = new JsonObject
                    {
                        ["input_tokens"] = 500, ["cached_input_tokens"] = 100, ["output_tokens"] = 50,
                        ["reasoning_output_tokens"] = 10,
                    },
                },
            },
        }.ToJsonString() + "\n";

    private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
    private static void Append(string path, string text) => File.AppendAllText(path, text, new UTF8Encoding(false));

    private string Session => Path.Combine(_claudeDir, "session.jsonl");

    private void FirstIngestFixtures()
    {
        Write(Session, ClaudeLine("a", Iso(180)) + ClaudeLine("b", Iso(120), 2000, 200));
        Write(Path.Combine(_codexDir, "rollout.jsonl"), CodexLine(Iso(60), 42));
    }

    private void Config(string json) => Write(Path.Combine(_configDir, "config.json"), json);

    private void Snapshot(double utilization, string limits = "", string updatedAt = "2026-08-18T11:55:00Z")
    {
        limits = limits.Length > 0 ? limits :
            $$"""[{"provider":"Claude","key":"five_hour","utilization":{{utilization.ToString(CultureInfo.InvariantCulture)}},"resetsAt":"2099-01-01T00:00:00Z"}]""";
        Write(Path.Combine(_dataDir, "snapshot.json"), $$"""
            {
              "updatedAt": "{{updatedAt}}",
              "limits": {{limits}},
              "today": {"io": 1000, "cost": 0.5, "cacheRead": 0, "cacheWrite": 0, "hasUnpriced": false},
              "week": {"io": 5000, "cost": 2.5, "cacheRead": 0, "cacheWrite": 0, "hasUnpriced": true}
            }
            """);
    }

    // An empty machine: every command says nothing is known rather than inventing a zero

    [Fact]
    public void AnEmptyMachineReportsNothingRatherThanZero()
    {
        Assert.Contains("redlinectl <command>", Run(0, "help").Text);
        Assert.Contains("redlinectl <command>", Run(0, "-h").Text);
        // As in Swift, a leading "--" word means status, so --help never reaches the help case
        Run(30, "--help");
        Run(30, "status");
        Assert.Equal("{\"error\":\"no data\"}", Run(30, "status", "--json").Text);
        Run(20, "history");
        Assert.Equal("{\"records\":[]}", Run(20, "history", "--json").Text);
        Run(20, "cadence");
        Assert.Equal("{\"records\":0}", Run(20, "cadence", "--json").Text);
        Assert.Contains("No transcripts", Run(20, "findings").Text);
    }

    [Fact]
    public void AnUnknownCommandExitsNoDataWithTheUsage()
    {
        var r = Run(30, "frobnicate");
        Assert.StartsWith("unknown command: frobnicate", r.Text);
        Assert.Contains("exit codes", r.Text);
    }

    [Fact]
    public void AnOptionFirstMeansStatus()
    {
        Snapshot(40);
        var o = RunJson(0, "--json");
        Assert.Equal("five_hour", Json.Str(o["windows"]![0]!["key"]));
    }

    // Ingest and what the store answers

    [Fact]
    public void IngestIsIncremental()
    {
        FirstIngestFixtures();
        var first = RunJson(0, "ingest", "--json");
        Assert.Equal(3, Json.Int(first["added"]));
        Assert.Equal(3, Json.Int(first["records"]));
        Assert.Equal(2, Json.Int(first["by_provider"]!["Claude"]));
        Assert.Equal(1, Json.Int(first["by_provider"]!["Codex"]));

        // The same transcripts a second time say nothing new
        var second = RunJson(0, "ingest", "--json");
        Assert.Equal(0, Json.Int(second["added"]));
        Assert.Equal(3, Json.Int(second["records"]));

        Assert.Matches(@"^0 new records · Claude 0 · Codex 0 · Ollama 0 · 3 held$", Run(0, "ingest").Text);
    }

    [Fact]
    public void HistoryAnswersFromTheStore()
    {
        FirstIngestFixtures();
        Run(0, "ingest");
        Assert.Contains("days are UTC", Run(0, "history").Text);
        // Claude 1100 + 2200, plus Codex 400 fresh input and 60 output on the same UTC day
        var json = RunJson(0, "history", "--json");
        Assert.Equal(3760, Json.Int(json["tokens"]));
        Assert.Equal(1, Json.Int(json["days"]));
        Assert.Equal("UTC", Json.Str(json["day_basis"]));

        var csv = Run(0, "history", "--csv").Text;
        Assert.StartsWith("day,provider,model,input,output,cache_read,cache_write,cost_usd,priced\n", csv);
        Assert.Contains("Claude,claude-sonnet-5", csv);
        Assert.Contains("2026-08-18,", csv);
    }

    [Fact]
    public void CadenceReadsTheStore()
    {
        FirstIngestFixtures();
        Run(0, "ingest");
        var text = Run(0, "cadence").Text;
        Assert.Contains("current run", text);
        Assert.Contains("days running  1", text);
        Assert.Contains("busiest hour  10:00 local", text);
        var json = RunJson(0, "cadence", "--json");
        Assert.Equal(1, Json.Int(json["active_days"]));
        Assert.Equal(3, Json.Int(json["records"]));
        Assert.Equal(24, json["tokens_by_hour_local"]!.AsArray().Count);
    }

    [Fact]
    public void AppendedAndHalfWrittenLinesAreReadOnlyWhenWhole()
    {
        FirstIngestFixtures();
        Run(0, "ingest");
        Append(Session, ClaudeLine("c", Iso(10), 500, 50));
        var r = RunJson(0, "ingest", "--json");
        Assert.Equal(1, Json.Int(r["added"]));
        Assert.Equal(4, Json.Int(r["records"]));

        // A line being written right now ends mid record. It must not be parsed until it is whole.
        Append(Session, $$"""{"timestamp":"{{Iso(1)}}","requestId":"req_partial","message":{"id":"partial","model":"claude-sonnet-5","usage":{"input_tok""");
        Assert.Equal(0, Json.Int(RunJson(0, "ingest", "--json")["added"]));
        Append(Session, """ens":900,"output_tokens":90,"cache_read_input_tokens":0}}}""" + "\n");
        r = RunJson(0, "ingest", "--json");
        Assert.Equal(1, Json.Int(r["added"]));
        Assert.Equal(5, Json.Int(r["records"]));
    }

    [Fact]
    public void HistoryOutlivesTheTranscripts()
    {
        FirstIngestFixtures();
        Run(0, "ingest");
        var before = Json.Int(RunJson(0, "history", "--json")["tokens"]);
        Directory.Delete(Path.Combine(_home, ".claude", "projects"), true);
        Assert.Equal(0, Json.Int(RunJson(0, "ingest", "--json")["added"]));
        var after = Json.Int(RunJson(0, "history", "--json")["tokens"]);
        Assert.Equal(before, after);
        Assert.NotEqual(0, after);
    }

    // Settings are obeyed

    [Fact]
    public void IngestRefusesWhenHistoryIsOff()
    {
        FirstIngestFixtures();
        Config("""{ "recordHistory": false, "providers": ["Claude", "Codex", "Ollama"] }""");
        Assert.Contains("Keep Local History is off", Run(30, "ingest").Text);
        Assert.Equal("{\"error\":\"history is off\"}", Run(30, "ingest", "--json").Text);
    }

    [Fact]
    public void AProviderLeftOutIsNotRead()
    {
        Config("""{ "providers": ["Ollama"] }""");
        Write(Path.Combine(_claudeDir, "second.jsonl"), ClaudeLine("z", Iso(5), 7000, 700));
        var r = RunJson(0, "ingest", "--json");
        Assert.Equal(0, Json.Int(r["added"]));
        Assert.Null(r["by_provider"]!["Claude"]);
    }

    [Fact]
    public void ShimRecordsAreIngestedAsOllama()
    {
        Write(Path.Combine(_dataDir, "ollama.jsonl"),
            """{"ts": "2026-08-18T11:00:00.123456+00:00", "model": "qwen3-coder:30b", "prompt_eval_count": 40, "eval_count": 12, "total_duration_ms": 900, "load_duration_ms": 20, "done_reason": "stop"}""" + "\n");
        var r = RunJson(0, "ingest", "--json");
        Assert.Equal(1, Json.Int(r["by_provider"]!["Ollama"]));
    }

    // Status and the published files

    [Fact]
    public void UsageRecordsAloneAreNotAReading()
    {
        FirstIngestFixtures();
        Run(0, "ingest");
        Assert.Contains("No reading available", Run(30, "status").Text);
    }

    [Fact]
    public void StatusExitCodeFollowsTheWorstWindow()
    {
        Snapshot(91);
        var text = Run(10, "status").Text;
        Assert.Contains("91%", text);
        Assert.Contains("Session (5h)", text);
        Assert.Contains("today   1.0K tokens  $0.50 (estimate)", text);
        Assert.Contains("7 days  5.0K tokens  $2.50+ (estimate)", text);
        Assert.Contains("snapshot 5m old", text);

        Snapshot(40);
        Run(0, "status");
        // Three hours into a five hour window, so the pace has a clock to read against
        Snapshot(0, limits: """[{"provider":"Claude","key":"five_hour","utilization":100,"resetsAt":"2026-08-18T14:00:00Z"}]""");
        var hit = Run(11, "status").Text;
        Assert.Contains("resets in 2h", hit);
        Assert.Contains("limit reached", hit);
        Snapshot(0, limits: "[]");
        Assert.Contains("No limit windows are being reported.", Run(20, "status").Text);
    }

    [Fact]
    public void TheRedThresholdComesFromConfig()
    {
        Config("""{ "limitRedPct": 95 }""");
        Snapshot(91);
        Run(0, "status");
    }

    [Fact]
    public void StatusJsonLabelsEveryFigure()
    {
        Snapshot(91);
        var o = RunJson(10, "status", "--json");
        Assert.Equal("2026-08-18T12:00:00Z", Json.Str(o["generated_at"]));
        Assert.Equal("2026-08-18T11:55:00Z", Json.Str(o["updated_at"]));
        var w = o["windows"]![0]!.AsObject();
        Assert.Equal("Claude", Json.Str(w["provider"]));
        Assert.Equal(91, Json.Num(w["utilization"]));
        Assert.Equal("unknown", Json.Str(w["provenance"]));
        Assert.Equal("2099-01-01T00:00:00Z", Json.Str(w["resets_at"]));
        Assert.Equal(1000, Json.Int(o["today"]!["tokens"]));
        Assert.Equal("official", Json.Str(o["today"]!["tokens_basis"]));
        Assert.Equal("local_estimate", Json.Str(o["today"]!["cost_basis"]));
        Assert.Equal(true, Json.Bool(o["week"]!["cost_partial"]));
        // Keys come out sorted, as JSONSerialization's .sortedKeys writes them
        var keys = o.Select(kv => kv.Key).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
    }

    [Fact]
    public void AFresherStatuslineFeedReplacesClaudesWindows()
    {
        Snapshot(0, limits: """
            [{"provider":"Claude","key":"five_hour","utilization":10,"resetsAt":"2099-01-01T00:00:00Z"},
             {"provider":"Codex","key":"five_hour","utilization":30,"resetsAt":"2099-01-01T00:00:00Z"}]
            """);
        Write(Path.Combine(_dataDir, "claude-usage.json"), """
            {"updated_at":"2026-08-18T11:59:00Z","five_hour":{"used_percentage":88,"resets_at":4070908800},"seven_day":null,"model_scoped":null}
            """);
        var o = RunJson(10, "status", "--json");
        var rows = o["windows"]!.AsArray().Select(n => (Json.Str(n!["provider"]), Json.Num(n!["utilization"]))).ToList();
        Assert.Contains(("Claude", (double?)88), rows);
        Assert.Contains(("Codex", (double?)30), rows);
        Assert.DoesNotContain(("Claude", (double?)10), rows);
        Assert.Equal("2026-08-18T11:59:00Z", Json.Str(o["claude_limits_as_of"]));
        // Grouped by provider: Claude's rows first
        Assert.Equal("Claude", Json.Str(o["windows"]![0]!["provider"]));
    }

    [Fact]
    public void ExpiredAndUninformativeWindowsAreDropped()
    {
        Snapshot(0, limits: """
            [{"provider":"Claude","key":"five_hour","utilization":95,"resetsAt":"2026-08-18T11:00:00Z"},
             {"provider":"Claude","key":"mystery","utilization":0},
             {"provider":"Claude","key":"seven_day","utilization":20,"resetsAt":"2099-01-01T00:00:00Z"}]
            """);
        var o = RunJson(0, "status", "--json");
        Assert.Equal(new[] { "seven_day" }, o["windows"]!.AsArray().Select(n => Json.Str(n!["key"])));
    }

    // The diagnostics log

    [Fact]
    public void LogReadsTheHomesDiagnostics()
    {
        Run(20, "log");
        var log = new DiagnosticsLog(DiagnosticsLog.DefaultPath(_home), "test");
        log.Log(DiagLevel.warn, "feed.stale", "the feed is old", new() { ["age"] = "900" }, Now.AddMinutes(-3));
        log.Log(DiagLevel.error, "snapshot.write_failed", "could not write", null, Now.AddMinutes(-2));
        log.Log(DiagLevel.warn, "feed.stale", "the feed is old", null, Now.AddMinutes(-1));
        log.Log(DiagLevel.info, "poll.ok", "fine", null, Now);

        var text = Run(0, "log").Text.Split('\n');
        Assert.Equal(3, text.Length);
        Assert.Equal("2026-08-18T11:57:00Z  WARN  feed.stale  the feed is old  age=900", text[0]);
        Assert.Equal(4, Run(0, "log", "--level", "debug").Text.Split('\n').Length);
        Assert.Single(Run(0, "log", "--tail", "1").Text.Split('\n'));
        Assert.Single(Run(0, "log", "--level", "error").Text.Split('\n'));

        var tally = Run(0, "log", "--tally").Text.Split('\n');
        Assert.Equal("           feed.stale  2  last 2026-08-18T11:59:00Z", tally[0]);
        var json = RunJson(0, "log", "--tally", "--json");
        Assert.Equal(2, Json.Int(json["codes"]![0]!["count"]));

        var events = JsonNode.Parse(Run(0, "log", "--json").Text)!.AsArray();
        Assert.Equal("warn", Json.Str(events[0]!["level"]));
        Assert.Equal("feed.stale", Json.Str(events[0]!["code"]));
    }

    // Helpers

    [Fact]
    public void CsvFieldsAreQuotedOnlyWhenTheyNeedIt()
    {
        Assert.Equal("plain", RedlineCLI.CsvField("plain"));
        Assert.Equal("\"a,b\"", RedlineCLI.CsvField("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", RedlineCLI.CsvField("say \"hi\""));
    }

    [Fact]
    public void PaddingAlwaysLeavesAGap()
    {
        Assert.Equal("ab   ", RedlineCLI.Pad("ab", 5));
        Assert.Equal("abcdef ", RedlineCLI.Pad("abcdef", 5));
        Assert.Equal("12%", RedlineCLI.Percent(12.5));
        Assert.Equal("14%", RedlineCLI.Percent(13.5));
    }

    [Fact]
    public void ExecutePrintsTheTextAndReturnsTheCode()
    {
        var output = new StringWriter();
        var code = RedlineCLI.Execute(new[] { "help" }, output, now: Now, home: _home);
        Assert.Equal(0, code);
        Assert.EndsWith("30 no data\n", output.ToString());
    }
}
