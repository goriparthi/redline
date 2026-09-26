// Wire format between the app and the widget. The widget cannot parse transcripts in its time
// budget, so the app writes this and the widget only renders it. Keys match Swift's Codable output.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed class Snapshot : IEquatable<Snapshot>
{
    public sealed record Window(string Provider, string Key, double Utilization, DateTimeOffset? ResetsAt)
    {
        // Providers share window keys, so identity must include the provider
        public string Id => $"{Provider}|{Key}";

        public string DisplayName => Key switch
        {
            "five_hour" => "Session · 5h",
            "seven_day" => Provider == "Claude" ? "Week · all models" : "Week",
            "seven_day_opus" => "Week · Opus",
            "seven_day_sonnet" => "Week · Sonnet",
            _ => Key.Replace('_', ' '),
        };
    }

    public sealed record Totals(int Io, double Cost, bool HasUnpriced)
    {
        public static Totals From(Agg agg) => new(agg.Io, agg.Cost, agg.HasUnpriced);
        internal static readonly Totals Zero = new(0, 0, false);
    }

    /// Live Ollama state, carried here so the widget never needs network access of its own.
    /// Swift's Snapshot.Ollama; renamed because C# cannot share a name with the property.
    public sealed record OllamaSection(bool Reachable, string? Version, IReadOnlyList<OllamaSection.RunningModel> Running,
                                       int DownloadedCount, long DownloadedBytes, int? CloudCount = null)
    {
        public sealed record RunningModel(string Name, long SizeBytes, double VramShare);

        /// Models actually stored on this disk, with cloud entries taken back out
        public int LocalCount => DownloadedCount - (CloudCount ?? 0);

        public bool Equals(OllamaSection? o) => o is not null && Reachable == o.Reachable &&
            Version == o.Version && Running.SequenceEqual(o.Running) && DownloadedCount == o.DownloadedCount &&
            DownloadedBytes == o.DownloadedBytes && CloudCount == o.CloudCount;
        public override int GetHashCode() => HashCode.Combine(Reachable, Version, DownloadedCount, DownloadedBytes);
    }

    /// A provider's own reported health, from its public status feed.
    public sealed record Service(string Provider, string Indicator, string Description)
    {
        public bool IsOperational => Indicator is "none" or "local";

        /// Calm and factual. The two local indicators are Ollama's, probed directly.
        public string Phrase => Indicator switch
        {
            "none" => "service ok",
            "minor" => "minor incident reported",
            "major" or "critical" => "outage reported",
            "local" => "local server running",
            "local-down" => "local server not reachable",
            _ => "status unknown",
        };
    }

    public DateTimeOffset UpdatedAt { get; }
    public IReadOnlyList<Window> Limits { get; }
    /// When Claude's windows were last true; the feed only writes while Claude Code runs.
    public DateTimeOffset? ClaudeLimitsAsOf { get; }
    public Totals Today { get; }
    public Totals Week { get; }
    // Optional so an older snapshot on disk still decodes rather than leaving the widget blank
    public IReadOnlyDictionary<string, Totals>? TodayByProvider { get; }
    public IReadOnlyDictionary<string, Totals>? WeekByProvider { get; }
    public OllamaSection? Ollama { get; }
    public List<Service>? Services { get; set; }

    public Snapshot(DateTimeOffset updatedAt, IEnumerable<LimitWindow> limits, Agg today, Agg week,
                    OllamaSection? ollama = null, List<Service>? services = null,
                    DateTimeOffset? claudeLimitsAsOf = null)
    {
        UpdatedAt = Truncate(updatedAt);
        ClaudeLimitsAsOf = claudeLimitsAsOf is { } c ? Truncate(c) : null;
        TodayByProvider = today.Providers.ToDictionary(kv => kv.Key, kv => new Totals(kv.Value.Io, kv.Value.Cost, today.HasUnpriced));
        WeekByProvider = week.Providers.ToDictionary(kv => kv.Key, kv => new Totals(kv.Value.Io, kv.Value.Cost, week.HasUnpriced));
        Ollama = ollama;
        Limits = limits.Select(w => new Window(w.Provider, w.Key, w.Utilization,
                                               w.ResetsAt is { } r ? Truncate(r) : null)).ToList();
        Today = Totals.From(today);
        Week = Totals.From(week);
        Services = services;
    }

    private Snapshot(DateTimeOffset updatedAt, List<Window> limits, DateTimeOffset? claudeLimitsAsOf,
                     Totals today, Totals week, Dictionary<string, Totals>? todayByProvider,
                     Dictionary<string, Totals>? weekByProvider, OllamaSection? ollama, List<Service>? services)
    {
        UpdatedAt = updatedAt;
        Limits = limits;
        ClaudeLimitsAsOf = claudeLimitsAsOf;
        Today = today;
        Week = week;
        TodayByProvider = todayByProvider;
        WeekByProvider = weekByProvider;
        Ollama = ollama;
        Services = services;
    }

    // Whole-second precision, since the ISO8601 encoding carries nothing finer
    internal static DateTimeOffset Truncate(DateTimeOffset d) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(d.ToUnixTimeMilliseconds() / 1000.0));

    /// Widgets refresh on the system's schedule, so callers must surface staleness.
    public bool IsStale(DateTimeOffset? now = null, double tolerance = 900) =>
        ((now ?? DateTimeOffset.UtcNow) - UpdatedAt).TotalSeconds > tolerance;

    /// Claude's windows age apart from the snapshot; stale ones render drained of status colour.
    public bool ClaudeLimitsAreStale(DateTimeOffset? now = null, double tolerance = 900)
    {
        if (ClaudeLimitsAsOf is not { } at) return false;
        return ((now ?? DateTimeOffset.UtcNow) - at).TotalSeconds > tolerance;
    }

    public Window? Worst(string prefix, string? provider = null) =>
        Limits.Where(w => w.Key.StartsWith(prefix, StringComparison.Ordinal))
              .Where(w => provider is null || string.Equals(w.Provider, provider, StringComparison.OrdinalIgnoreCase))
              .Aggregate((Window?)null, (best, w) => best is null || w.Utilization > best.Utilization ? w : best);

    public List<Window> Windows(string provider) =>
        Limits.Where(w => string.Equals(w.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();

    /// Swift's today(for:); a C# method cannot share the Today property's name. Null means all.
    public Totals TodayFor(string? provider) =>
        provider is null ? Today : TodayByProvider?.GetValueOrDefault(provider) ?? Totals.Zero;

    public Totals WeekFor(string? provider) =>
        provider is null ? Week : WeekByProvider?.GetValueOrDefault(provider) ?? Totals.Zero;

    public bool Equals(Snapshot? o) => o is not null && UpdatedAt == o.UpdatedAt &&
        Limits.SequenceEqual(o.Limits) && ClaudeLimitsAsOf == o.ClaudeLimitsAsOf &&
        Today == o.Today && Week == o.Week && DictEquals(TodayByProvider, o.TodayByProvider) &&
        DictEquals(WeekByProvider, o.WeekByProvider) && Equals(Ollama, o.Ollama) &&
        (Services is null ? o.Services is null : o.Services is not null && Services.SequenceEqual(o.Services));

    public override bool Equals(object? obj) => Equals(obj as Snapshot);
    public override int GetHashCode() => HashCode.Combine(UpdatedAt, Limits.Count, Today, Week);

    private static bool DictEquals(IReadOnlyDictionary<string, Totals>? a, IReadOnlyDictionary<string, Totals>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    // JSON, in the exact shape Swift's JSONEncoder writes: camelCase keys, nil omitted, ISO8601 dates

    internal static string Iso(DateTimeOffset d) => DiagnosticsLog.Stamp(d);

    internal static DateTimeOffset? ParseIso(string? s)
    {
        if (s is null) return null;
        return DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["updatedAt"] = Iso(UpdatedAt),
            ["limits"] = new JsonArray(Limits.Select(w =>
            {
                var j = new JsonObject { ["provider"] = w.Provider, ["key"] = w.Key, ["utilization"] = w.Utilization };
                if (w.ResetsAt is { } r) j["resetsAt"] = Iso(r);
                return (JsonNode)j;
            }).ToArray()),
        };
        if (ClaudeLimitsAsOf is { } asOf) o["claudeLimitsAsOf"] = Iso(asOf);
        o["today"] = TotalsJson(Today);
        o["week"] = TotalsJson(Week);
        if (TodayByProvider is not null) o["todayByProvider"] = MapJson(TodayByProvider);
        if (WeekByProvider is not null) o["weekByProvider"] = MapJson(WeekByProvider);
        if (Ollama is { } ol)
        {
            var j = new JsonObject { ["reachable"] = ol.Reachable };
            if (ol.Version is not null) j["version"] = ol.Version;
            j["running"] = new JsonArray(ol.Running.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name, ["sizeBytes"] = r.SizeBytes, ["vramShare"] = r.VramShare,
            }).ToArray());
            j["downloadedCount"] = ol.DownloadedCount;
            j["downloadedBytes"] = ol.DownloadedBytes;
            if (ol.CloudCount is { } cc) j["cloudCount"] = cc;
            o["ollama"] = j;
        }
        if (Services is not null)
            o["services"] = new JsonArray(Services.Select(s => (JsonNode)new JsonObject
            {
                ["provider"] = s.Provider, ["indicator"] = s.Indicator, ["description"] = s.Description,
            }).ToArray());
        return o;
    }

    private static JsonObject TotalsJson(Totals t) =>
        new() { ["io"] = t.Io, ["cost"] = t.Cost, ["hasUnpriced"] = t.HasUnpriced };

    private static JsonObject MapJson(IReadOnlyDictionary<string, Totals> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[k] = TotalsJson(v);
        return o;
    }

    private sealed class DecodeError : Exception
    {
        public DecodeError(string key) : base($"missing or mistyped key: {key}") { }
    }

    private static T Req<T>(T? v, string key) where T : struct => v ?? throw new DecodeError(key);
    private static T Req<T>(T? v, string key, int _ = 0) where T : class => v ?? throw new DecodeError(key);

    /// Throws when a key Swift's decoder requires is missing or mistyped; optional keys may be absent.
    public static Snapshot FromJson(JsonObject o)
    {
        var limits = Req(o["limits"] as JsonArray, "limits").Select(n =>
        {
            var w = Req(n as JsonObject, "limits[]");
            return new Window(Req(Json.Str(w["provider"]), "provider"), Req(Json.Str(w["key"]), "key"),
                              Req(Json.Num(w["utilization"]), "utilization"), OptDate(w, "resetsAt"));
        }).ToList();
        OllamaSection? ollama = null;
        if (Present(o, "ollama"))
        {
            var j = Req(o["ollama"] as JsonObject, "ollama");
            var running = Req(j["running"] as JsonArray, "running").Select(n =>
            {
                var r = Req(n as JsonObject, "running[]");
                return new OllamaSection.RunningModel(Req(Json.Str(r["name"]), "name"),
                    Req(Json.Long(r["sizeBytes"]), "sizeBytes"), Req(Json.Num(r["vramShare"]), "vramShare"));
            }).ToList();
            ollama = new OllamaSection(Req(Json.Bool(j["reachable"]), "reachable"),
                Present(j, "version") ? Req(Json.Str(j["version"]), "version") : null, running,
                Req(Json.Int(j["downloadedCount"]), "downloadedCount"),
                Req(Json.Long(j["downloadedBytes"]), "downloadedBytes"),
                Present(j, "cloudCount") ? Req(Json.Int(j["cloudCount"]), "cloudCount") : null);
        }
        List<Service>? services = null;
        if (Present(o, "services"))
            services = Req(o["services"] as JsonArray, "services").Select(n =>
            {
                var s = Req(n as JsonObject, "services[]");
                return new Service(Req(Json.Str(s["provider"]), "provider"),
                    Req(Json.Str(s["indicator"]), "indicator"), Req(Json.Str(s["description"]), "description"));
            }).ToList();
        return new Snapshot(Req(ParseIso(Json.Str(o["updatedAt"])), "updatedAt"), limits,
            OptDate(o, "claudeLimitsAsOf"), TotalsFrom(o["today"], "today"), TotalsFrom(o["week"], "week"),
            MapFrom(o, "todayByProvider"), MapFrom(o, "weekByProvider"), ollama, services);
    }

    // Swift's decodeIfPresent treats an explicit null like an absent key
    private static bool Present(JsonObject o, string key) => o[key] is not null;

    private static DateTimeOffset? OptDate(JsonObject o, string key) =>
        Present(o, key) ? Req(ParseIso(Json.Str(o[key])), key) : null;

    private static Totals TotalsFrom(JsonNode? n, string key)
    {
        var t = Req(n as JsonObject, key);
        return new Totals(Req(Json.Int(t["io"]), "io"), Req(Json.Num(t["cost"]), "cost"),
                          Req(Json.Bool(t["hasUnpriced"]), "hasUnpriced"));
    }

    private static Dictionary<string, Totals>? MapFrom(JsonObject o, string key)
    {
        if (!Present(o, key)) return null;
        return Req(o[key] as JsonObject, key).ToDictionary(kv => kv.Key, kv => TotalsFrom(kv.Value, key));
    }
}

/// Reads and writes the snapshot where the app and the widget can both reach it. Windows has no
/// App Group or widget sandbox, so the user path under ~/.local/share/redline carries it.
public static class SnapshotStore
{
    public const string AppGroup = "group.com.goriparthi.redline";
    public const string FileName = "snapshot.json";
    public const string WidgetBundleID = "com.goriparthi.redline.widget";

    /// The process's own app-data copy: %APPDATA%\redline, or inside an overridden home so a
    /// test profile never finds the real machine's last reading.
    public static string? LocalAppSupportUrl => RedlineHome.IsOverridden
        ? RedlineHome.PathFor($"AppData/Roaming/redline/{FileName}")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "redline", FileName);

    /// The macOS widget container, kept so the path contract is shared. Written only if it exists.
    public static string WidgetContainerUrl =>
        RedlineHome.PathFor($"Library/Containers/{WidgetBundleID}/Data/Library/Application Support/redline/{FileName}");

    // Always available, and readable by any process of this user
    public static string UserUrl => RedlineHome.PathFor($".local/share/redline/{FileName}");

    /// App Groups do not exist on Windows, so this never resolves.
    public static string? GroupUrl(string? appGroup = AppGroup) => null;

    public static string Url(string? appGroup = AppGroup) => GroupUrl(appGroup) ?? UserUrl;

    // The widget container is included only when it exists, so nothing is created for no widget
    public static List<string> WriteTargets
    {
        get
        {
            var output = new List<string> { UserUrl };
            var container = WidgetContainerUrl;
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(container));
            if (parent is not null && Directory.Exists(parent)) output.Add(container);
            if (!RedlineHome.IsOverridden && GroupUrl() is { } g) output.Add(g);
            return output;
        }
    }

    // Own container first: in a sandboxed widget that is the one guaranteed readable location
    public static List<string> ReadCandidates
    {
        get
        {
            var output = new List<string>();
            if (LocalAppSupportUrl is { } local) output.Add(local);
            if (!RedlineHome.IsOverridden && GroupUrl() is { } g) output.Add(g);
            output.Add(UserUrl);
            return output;
        }
    }

    /// Succeeds if any target takes it; a missing container must not fail the write.
    public static bool WriteEverywhere(Snapshot snapshot) =>
        WriteTargets.Select(t => Write(snapshot, t)).ToList().Contains(true);

    public static Snapshot? ReadAny()
    {
        foreach (var path in ReadCandidates)
            if (Read(path) is { } s) return s;
        return null;
    }

    public static bool Write(Snapshot snapshot, string? to = null)
    {
        var path = to ?? Url();
        string text;
        try { text = snapshot.ToJson().ToJsonString(); }
        catch
        {
            Diag.Log.Error("snapshot.encode_failed", "could not encode snapshot");
            return false;
        }
        try
        {
            Json.WriteAtomic(path, text);
            return true;
        }
        catch (Exception e)
        {
            // The widget and the CLI both read this file, so a failed write explains every stale one
            Diag.Log.Error("snapshot.write_failed", "could not write snapshot",
                new() { ["path"] = path, ["error"] = e.Message });
            return false;
        }
    }

    public static Snapshot? Read(string? from = null)
    {
        var path = from ?? Url();
        string text;
        try { text = File.ReadAllText(path); } catch { return null; }
        try
        {
            if (Json.ParseObject(text) is not { } json) throw new FormatException("not a JSON object");
            return Snapshot.FromJson(json);
        }
        catch (Exception e)
        {
            Diag.Log.Error("snapshot.decode_failed", "snapshot on disk did not decode",
                new() { ["path"] = path, ["error"] = e.Message });
            return null;
        }
    }
}
