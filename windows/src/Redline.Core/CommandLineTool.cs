// The bundled command line tool: `redlinectl status --json` answers what RedLine already knows.
// Exit codes are the contract: 0 ok, 10 near, 11 at a limit, 20 nothing to report, 30 no data.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Redline.Core;

public static class RedlineCLI
{
    public sealed record Result(string Text, int Code);

    public static class Code
    {
        public const int Ok = 0;
        public const int Near = 10;
        public const int Hit = 11;
        public const int Indeterminate = 20;
        public const int NoData = 30;
    }

    /// The words that mean "run the tool and exit". Kept here so the entry point and the
    /// help text cannot disagree about what exists.
    public static readonly string[] Commands =
        { "status", "findings", "history", "cadence", "ingest", "log", "help" };

    public const string Usage = """
        redlinectl <command> [options]

          status              current limits, tokens and cost
          findings            setup findings from your transcripts
          history             recorded daily history from the local warehouse
          cadence             how the work is spread out: runs, hours, days in a row
          ingest              read new transcript records into the local store now
          log                 recorded warnings and errors, newest last
          statusline          Claude Code statusLine command: files the rate limits for RedLine
          ollama-shim ARGS    the ollama stand-in the tracking shim forwards to
          help                this text

        options
          --json              machine-readable output
          --csv               comma-separated output (history only)
          --days N            window to report on (findings, history, log)
          --level L           debug | info | warn | error, lowest to report (log)
          --tally             group the log by code, most frequent first
          --tail N            only the last N entries (log)

        exit codes
          0 ok · 10 near a limit · 11 at a limit · 20 nothing to report · 30 no data
        """;

    /// Null `home` reads the real profile (or REDLINE_HOME); a path reads a fixture home.
    public static Result Run(IReadOnlyList<string> arguments, string version = "dev",
                             DateTimeOffset? now = null, string? home = null,
                             TimeZoneInfo? timeZone = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var args = arguments.ToList();
        var command = "status";
        if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            command = args[0];
            args.RemoveAt(0);
        }
        var json = args.Contains("--json");
        var csv = args.Contains("--csv");
        var days = IntOption(args, "--days");
        var paths = new Paths(home);

        return command switch
        {
            "status" => Status(json, t, paths),
            "findings" => Findings(json, days ?? 7, t, paths),
            "history" => History(json, csv, days ?? 30, t, paths),
            "cadence" => Cadence(json, days ?? 14, t, paths, timeZone),
            "ingest" => Ingest(json, t, paths),
            "log" => Logs(json, args, days, t, paths, version),
            "help" or "--help" or "-h" => new Result(Usage, Code.Ok),
            _ => new Result($"unknown command: {command}\n\n" + Usage, Code.NoData),
        };
    }

    /// Runs a command and prints its text the way Swift's `print` does. Returns the exit code.
    public static int Execute(IReadOnlyList<string> arguments, TextWriter stdout, string version = "dev",
                              DateTimeOffset? now = null, string? home = null,
                              TimeZoneInfo? timeZone = null)
    {
        var result = Run(arguments, version, now, home, timeZone);
        stdout.Write(result.Text + "\n");
        stdout.Flush();
        return result.Code;
    }

    /// Every location the tool reads, resolved once against an injected or the real home.
    internal sealed class Paths
    {
        public readonly string? Home;
        public Paths(string? home) { Home = home; }

        private string Under(string components) =>
            Home is null ? RedlineHome.PathFor(components) : RedlineHome.Join(Home, components);

        public string Config => Under(".config/redline/config.json");
        public string Projects => Under(".claude/projects");
        public string CodexSessions => Under(".codex/sessions");
        public string OllamaLog => Under(".local/share/redline/ollama.jsonl");
        public string History => Under(".local/share/redline/history");
        public string Feed => StatuslineFeed.DefaultPath(Home);
        public string Diagnostics => DiagnosticsLog.DefaultPath(Home);

        public Snapshot? ReadSnapshot()
        {
            if (Home is null) return SnapshotStore.ReadAny();
            // The same order SnapshotStore uses for an overridden home: own app data, then shared
            foreach (var p in new[] { Under($"AppData/Roaming/redline/{SnapshotStore.FileName}"),
                                      Under($".local/share/redline/{SnapshotStore.FileName}") })
                if (SnapshotStore.Read(p) is { } s) return s;
            return null;
        }
    }

    // log

    /// The diagnostics file, as text or JSON. This is the command an eval loop runs: it
    /// answers "what has actually been going wrong" without opening the app.
    internal static Result Logs(bool json, List<string> args, int? days, DateTimeOffset now,
                                Paths paths, string version)
    {
        var level = StringOption(args, "--level") is { } raw &&
                    Enum.TryParse<DiagLevel>(raw.ToLowerInvariant(), false, out var parsed) &&
                    Enum.IsDefined(parsed) && raw.ToLowerInvariant() == parsed.ToString()
            ? parsed : DiagLevel.warn;
        DateTimeOffset? since = days is { } d ? now.AddSeconds(-(double)d * 86_400) : null;
        var log = paths.Home is null ? Diag.Log : new DiagnosticsLog(paths.Diagnostics, version);

        if (args.Contains("--tally"))
        {
            var rows = log.Tally(level, since);
            if (rows.Count == 0)
                return new Result($"No entries at {level} or above.", Code.Indeterminate);
            if (json)
            {
                var codes = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["code"] = r.Code, ["count"] = r.Count, ["latest"] = r.Latest,
                }).ToArray());
                return new Result(Encode(new JsonObject { ["codes"] = codes }), Code.Ok);
            }
            var width = rows.Max(r => r.Code.Length);
            var text = string.Join("\n", rows.Select(r =>
                new string(' ', width - r.Code.Length) + r.Code + $"  {r.Count}  last {r.Latest}"));
            return new Result(text, Code.Ok);
        }

        var events = log.Read(level, since);
        if (IntOption(args, "--tail") is { } tail && tail >= 0 && events.Count > tail)
            events = events.Skip(events.Count - tail).ToList();
        if (events.Count == 0)
            return new Result($"No entries at {level} or above.", Code.Indeterminate);
        if (json)
        {
            var node = JsonSerializer.SerializeToNode(events) ?? new JsonArray();
            return new Result(EncodeNode(node), Code.Ok);
        }
        var lines = events.Select(e =>
        {
            var ctx = e.Context.Count == 0 ? "" : "  " + string.Join(" ",
                e.Context.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            return $"{e.At}  {e.Level.ToString().ToUpperInvariant()}  {e.Code}  {e.Message}{ctx}";
        });
        return new Result(string.Join("\n", lines), Code.Ok);
    }

    internal static string? StringOption(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    internal static int? IntOption(List<string> args, string name) =>
        StringOption(args, name) is { } s &&
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    // status

    /// Reads what the app published, topped up from the statusline feed when that is fresher.
    /// Nothing is fetched: with no app running the honest answer is a stale reading and its age.
    internal static Result Status(bool json, DateTimeOffset now, Paths paths)
    {
        var config = Config.Load(paths.Config);
        var snapshot = paths.ReadSnapshot();
        var feed = StatuslineFeed.Read(paths.Feed, now);

        var windows = (snapshot?.Limits ?? Array.Empty<Snapshot.Window>())
            .Select(w => new LimitWindow(w.Provider, w.Key, w.Utilization, w.ResetsAt, Provenance.Unknown))
            .ToList();
        var limitsAsOf = snapshot?.ClaudeLimitsAsOf;
        if (feed is { IsEmpty: false, UpdatedAt: { } at } && (limitsAsOf is null || at > limitsAsOf))
        {
            windows = windows.Where(w => !string.Equals(w.Provider, StatuslineFeed.Provider,
                                                        StringComparison.OrdinalIgnoreCase))
                             .Concat(feed.Windows).ToList();
            limitsAsOf = at;
        }
        // Grouped by provider, then in window order inside each, so the rows read the way the
        // dropdown does. LINQ's OrderBy is stable, so the window order from Sorted survives.
        windows = LimitParser.Sorted(LimitParser.Unexpired(windows, now).Where(w => !w.IsUninformative))
            .OrderBy(w => w.Provider, StringComparer.Ordinal).ToList();

        if (snapshot is null && feed is null)
            return new Result(json ? "{\"error\":\"no data\"}"
                                   : "No reading available. Start RedLine, or set up the usage feed.",
                              Code.NoData);

        List<LimitSample> samples;
        using (var warehouse = new Warehouse(paths.History))
            samples = warehouse.LimitSamples(since: now.AddSeconds(-86400));
        var paces = PaceEstimator.Paces(windows, samples, now);
        var code = ExitCode(windows, config);

        return new Result(json ? StatusJSON(windows, paces, snapshot, limitsAsOf, now)
                               : StatusText(windows, paces, snapshot, limitsAsOf, now), code);
    }

    internal static int ExitCode(IReadOnlyList<LimitWindow> windows, Config config)
    {
        if (windows.Count == 0) return Code.Indeterminate;
        var worst = windows.Max(w => w.Utilization);
        if (worst >= 100) return Code.Hit;
        if (worst >= config.LimitRedPct) return Code.Near;
        return Code.Ok;
    }

    internal static string StatusText(IReadOnlyList<LimitWindow> windows, IReadOnlyList<Pace> paces,
                                      Snapshot? snapshot, DateTimeOffset? limitsAsOf, DateTimeOffset now)
    {
        var lines = new List<string>();
        if (windows.Count == 0) lines.Add("No limit windows are being reported.");
        foreach (var w in windows)
        {
            var pace = paces.FirstOrDefault(p => p.Provider == w.Provider && p.Key == w.Key);
            var row = Pad(w.Provider, 7) + Pad(w.DisplayName, 20);
            row += Pad(Percent(w.Utilization), 6);
            row += w.ResetsAt is { } r && r > now
                ? Pad("resets in " + Pace.Short((r - now).TotalSeconds), 22)
                : Pad("", 22);
            if (pace?.Summary(now) is { } summary) row += summary;
            lines.Add(row.Trim(' ', '\t'));
        }
        if (snapshot is not null)
        {
            var today = snapshot.Today;
            var week = snapshot.Week;
            lines.Add("");
            lines.Add($"today   {Redline.Core.Usage.FmtTokens(today.Io)} tokens  " +
                      $"{Redline.Core.Usage.FmtCost(today.Cost)}{(today.HasUnpriced ? "+" : "")} (estimate)");
            lines.Add($"7 days  {Redline.Core.Usage.FmtTokens(week.Io)} tokens  " +
                      $"{Redline.Core.Usage.FmtCost(week.Cost)}{(week.HasUnpriced ? "+" : "")} (estimate)");
            var age = (now - snapshot.UpdatedAt).TotalSeconds;
            lines.Add("");
            lines.Add($"snapshot {Pace.Short(age)} old" +
                      (limitsAsOf is { } l ? $", limits {Pace.Short((now - l).TotalSeconds)} old" : ""));
        }
        return string.Join("\n", lines);
    }

    internal static string StatusJSON(IReadOnlyList<LimitWindow> windows, IReadOnlyList<Pace> paces,
                                      Snapshot? snapshot, DateTimeOffset? limitsAsOf, DateTimeOffset now)
    {
        var root = new JsonObject { ["generated_at"] = Iso(now) };
        root["windows"] = new JsonArray(windows.Select(w =>
        {
            var o = new JsonObject
            {
                ["provider"] = w.Provider,
                ["key"] = w.Key,
                ["display_name"] = w.DisplayName,
                ["utilization"] = w.Utilization,
                ["provenance"] = w.Source.RawValue(),
            };
            if (w.ResetsAt is { } r) o["resets_at"] = Iso(r);
            if (paces.FirstOrDefault(p => p.Provider == w.Provider && p.Key == w.Key) is { } pace)
            {
                var block = new JsonObject
                {
                    ["rate_per_hour"] = pace.RatePerHour,
                    ["basis"] = pace.BasisNote,
                    ["hits_limit_before_reset"] = pace.HitsLimitBeforeReset,
                };
                if (pace.ExhaustsAt is { } e) block["exhausts_at"] = Iso(e);
                if (pace.PaceDelta is { } d) block["pace_delta"] = d;
                o["pace"] = block;
            }
            return (JsonNode)o;
        }).ToArray());
        if (snapshot is not null)
        {
            root["updated_at"] = Iso(snapshot.UpdatedAt);
            root["today"] = Totals(snapshot.Today);
            root["week"] = Totals(snapshot.Week);
        }
        if (limitsAsOf is { } l) root["claude_limits_as_of"] = Iso(l);
        return Encode(root);
    }

    internal static JsonObject Totals(Snapshot.Totals t) => new()
    {
        ["tokens"] = t.Io,
        ["cost_usd"] = t.Cost,
        ["tokens_basis"] = Provenance.Official.RawValue(),
        ["cost_basis"] = Provenance.LocalEstimate.RawValue(),
        // True when a model had no pricing entry: arithmetic over what could be priced, not all
        ["cost_partial"] = t.HasUnpriced,
    };

    // findings

    internal static Result Findings(bool json, int days, DateTimeOffset now, Paths paths)
    {
        var config = Config.Load(paths.Config);
        var sessions = new TranscriptScanner(paths.Projects).Scan(days, now);
        if (sessions.Count == 0)
            return new Result(json ? "{\"findings\":[]}" : "No transcripts in this window.", Code.Indeterminate);
        var input = ClaudeSetup.FindingsInput(sessions, days, now, paths.Home);
        var report = Redline.Core.Findings.Report(input, config);
        if (json)
        {
            var root = new JsonObject
            {
                ["generated_at"] = Iso(report.GeneratedAt),
                ["window_days"] = report.WindowDays,
                ["sessions_scanned"] = report.SessionsScanned,
            };
            root["findings"] = new JsonArray(report.Findings.Select(f =>
            {
                var o = new JsonObject
                {
                    ["id"] = f.Id,
                    ["kind"] = f.Kind.RawValue(),
                    ["basis"] = f.Basis.RawValue(),
                    ["title"] = f.Title,
                    ["detail"] = f.Detail,
                    ["evidence"] = new JsonArray(f.Evidence.Select(e =>
                    {
                        var row = new JsonObject { ["label"] = e.Label };
                        if (e.Value is { } v) row["value"] = v;
                        return (JsonNode)row;
                    }).ToArray()),
                };
                if (f.EstimatedTokens is { } tk) o["estimated_tokens"] = tk;
                if (f.EstimatedUSD is { } u) o["estimated_usd"] = u;
                if (f.Fix is { } fix) o["fix"] = fix;
                return (JsonNode)o;
            }).ToArray());
            return new Result(Encode(root), Code.Ok);
        }
        var lines = new List<string>
        {
            $"{report.Summary} · {report.SessionsScanned} sessions · {report.WindowDays} days",
        };
        foreach (var f in report.Findings)
        {
            lines.Add("");
            var head = $"[{f.Kind.Label()}] {f.Title}";
            if (f.EstimatedUSD is { } usd) head += $"  ~{Redline.Core.Usage.FmtCost(usd)}";
            lines.Add(head);
            lines.Add($"  {f.Detail}");
            foreach (var row in f.Evidence.Take(8))
                lines.Add($"    · {row.Label}" + (row.Value is { } v ? $" · {v}" : ""));
            if (f.Fix is { } fix) lines.Add($"  fix: {fix}");
            lines.Add($"  basis: {f.Basis.RawValue()}");
        }
        return new Result(string.Join("\n", lines), Code.Ok);
    }

    // history

    internal static Result History(bool json, bool csv, int days, DateTimeOffset now, Paths paths)
    {
        using var warehouse = new Warehouse(paths.History);
        var records = warehouse.Records(since: now.AddSeconds(-(double)days * 86400));
        if (records.Count == 0)
            return new Result(json ? "{\"records\":[]}" : "No history recorded yet. RedLine writes it as it polls.",
                              Code.Indeterminate);
        var c = CultureInfo.InvariantCulture;
        if (csv)
        {
            var rows = new List<string> { "day,provider,model,input,output,cache_read,cache_write,cost_usd,priced" };
            rows.AddRange(records.Select(r =>
                $"{r.Day},{r.Provider},{CsvField(r.Model)},{r.Input},{r.Output}," +
                $"{r.CacheRead},{r.CacheWrite}," + r.Cost.ToString("F6", c) + "," + (r.Priced ? "true" : "false")));
            return new Result(string.Join("\n", rows), Code.Ok);
        }
        if (json)
        {
            var root = new JsonObject
            {
                ["day_basis"] = "UTC",
                ["records"] = new JsonArray(records.Select(r => (JsonNode)new JsonObject
                {
                    ["day"] = r.Day, ["provider"] = r.Provider, ["model"] = r.Model,
                    ["input"] = r.Input, ["output"] = r.Output, ["cache_read"] = r.CacheRead,
                    ["cache_write"] = r.CacheWrite, ["cost_usd"] = r.Cost,
                    ["priced"] = r.Priced, ["cost_basis"] = r.CostBasis.RawValue(),
                }).ToArray()),
            };
            var byDay = Warehouse.ByDay(records);
            root["days"] = byDay.Count;
            root["tokens"] = byDay.Sum(d => d.Io);
            root["cost_usd"] = byDay.Sum(d => d.Cost);
            return new Result(Encode(root), Code.Ok);
        }
        var lines = new List<string> { "day         tokens      cost" };
        foreach (var row in Warehouse.ByDay(records))
            lines.Add($"{row.Day}  " + Pad(Redline.Core.Usage.FmtTokens(row.Io), 11) +
                      Redline.Core.Usage.FmtCost(row.Cost) + (row.Priced ? "" : "+"));
        var tokens = records.Sum(r => (long)r.Io);
        var cost = records.Sum(r => r.Cost);
        lines.Add("");
        lines.Add($"{records.Count} records · {Redline.Core.Usage.FmtTokens(tokens)} tokens · " +
                  $"{Redline.Core.Usage.FmtCost(cost)} estimated · days are UTC");
        return new Result(string.Join("\n", lines), Code.Ok);
    }

    // ingest

    /// Reads whatever the transcripts gained since the last pass: the app's own code path on
    /// demand, for a backfill, a machine with no app running, and the end to end tests.
    internal static Result Ingest(bool json, DateTimeOffset now, Paths paths)
    {
        var config = Config.Load(paths.Config);
        if (!config.RecordHistory)
            return new Result(json ? "{\"error\":\"history is off\"}"
                                   : "Keep Local History is off, so there is no store to read into.",
                              Code.NoData);
        using var warehouse = new Warehouse(paths.History);
        var counts = new Dictionary<string, int>();
        if (config.Wants(UsageStore.Provider))
            counts[UsageStore.Provider] = new UsageStore(paths.Projects).Ingest(warehouse, now);
        if (config.Wants(CodexStore.Provider))
        {
            var before = warehouse.EntryCount;
            _ = new CodexStore(paths.CodexSessions).Ingest(warehouse, now);
            counts[CodexStore.Provider] = warehouse.EntryCount - before;
        }
        if (config.Wants(OllamaStore.Provider))
            counts[OllamaStore.Provider] = new OllamaStore(paths.OllamaLog).Ingest(warehouse, now);
        warehouse.RollupPending(config);
        var added = counts.Values.Sum();

        if (json)
        {
            var by = new JsonObject();
            foreach (var kv in counts) by[kv.Key] = kv.Value;
            return new Result(Encode(new JsonObject
            {
                ["added"] = added, ["by_provider"] = by, ["records"] = warehouse.EntryCount,
            }), Code.Ok);
        }
        var detail = string.Join(" · ", counts.Keys.OrderBy(k => k, StringComparer.Ordinal)
                                                   .Select(k => $"{k} {counts[k]}"));
        return new Result($"{added} new records · {detail} · {warehouse.EntryCount} held", Code.Ok);
    }

    // cadence

    /// What the timestamps say about the shape of the work. Reads the store and nothing else.
    internal static Result Cadence(bool json, int days, DateTimeOffset now, Paths paths, TimeZoneInfo? timeZone)
    {
        List<Entry> entries;
        using (var warehouse = new Warehouse(paths.History))
            entries = warehouse.Entries(since: now.AddSeconds(-(double)days * 86400));
        if (entries.Count == 0)
            return new Result(json ? "{\"records\":0}" : $"No activity recorded in the last {days} days.",
                              Code.Indeterminate);
        var stretches = Redline.Core.Cadence.Stretches(entries);
        var current = Redline.Core.Cadence.Current(entries, now: now);
        var streak = Redline.Core.Cadence.Streak(entries, now, timeZone);
        var hours = Redline.Core.Cadence.ByHourOfDay(entries, timeZone);
        var longest = stretches.Count == 0 ? 0 : stretches.Max(s => s.Length);
        // The first hour holding the maximum, as Swift's max(by:) keeps the earlier of a tie
        var busiest = Array.IndexOf(hours, hours.Max());

        if (json)
        {
            var root = new JsonObject
            {
                ["records"] = entries.Count,
                ["days"] = days,
                ["active_days"] = Redline.Core.Cadence.ActiveDays(entries, timeZone).Count,
                ["streak_days"] = streak,
                ["longest_stretch_seconds"] = (int)longest,
                ["runs"] = stretches.Count,
                ["busiest_hour_local"] = busiest,
                ["tokens_by_hour_local"] = new JsonArray(hours.Select(h => (JsonNode)h).ToArray()),
                ["basis"] = "counted from local usage records",
            };
            if (current is not null)
            {
                root["current_stretch_seconds"] = (int)current.Length;
                root["current_stretch_started"] = Iso(current.Start);
            }
            return new Result(Encode(root), Code.Ok);
        }

        var lines = new List<string>
        {
            current is not null ? $"current run   {Pace.Short(current.Length)} so far" : "current run   none",
            $"longest run   {Pace.Short(longest)} of {stretches.Count} in {days} days",
            $"days running  {streak}",
            $"busiest hour  {busiest:00}:00 local",
        };
        return new Result(string.Join("\n", lines), Code.Ok);
    }

    // Helpers

    internal static string CsvField(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    internal static string Pad(string s, int width) =>
        s.Length >= width ? s + " " : s + new string(' ', width - s.Length);

    // printf's %.0f rounds half to even, so 12.5 reads "12%" as it does on macOS
    internal static string Percent(double v) =>
        Math.Round(v, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Iso(DateTimeOffset d) => DiagnosticsLog.Stamp(d);

    // Relaxed so "+", "&" and non-ASCII titles read as written, as JSONSerialization leaves them
    private static readonly JsonSerializerOptions Indented =
        new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// Pretty printed with keys sorted, like JSONSerialization's [.prettyPrinted, .sortedKeys].
    internal static string Encode(JsonObject o) => EncodeNode(o);

    internal static string EncodeNode(JsonNode node)
    {
        try { return Sorted(node)?.ToJsonString(Indented) ?? "{}"; }
        catch { return "{}"; }
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
