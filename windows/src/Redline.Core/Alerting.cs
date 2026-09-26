// When to interrupt someone, and when to stay quiet: never from a stale reading, once per
// window instance per threshold, and a reset only for a window that was actually being used.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// What kind of news an alert carries.
public abstract record AlertKind
{
    /// Crossed a percentage the user set.
    public sealed record Threshold(int Level) : AlertKind;
    /// Reached the cap. Separate because the wording differs and it is never merely advisory.
    public sealed record LimitReached : AlertKind;
    /// Projected to reach the cap before the window resets.
    public sealed record Projection : AlertKind;
    /// The window rolled over and capacity came back.
    public sealed record Reset : AlertKind;
}

/// `Id` is stable per event, so a delivery layer can avoid posting the same thing twice.
public sealed record AlertEvent(AlertKind Kind, string Provider, string Key, string Title,
                                string Body, string Id);

/// What has already been said, keyed by window instance so a new window re-arms everything.
public sealed class AlertState : IEquatable<AlertState>
{
    public sealed class Seen : IEquatable<Seen>
    {
        public string Instance { get; set; }
        public double Utilization { get; set; }
        public DateTimeOffset At { get; set; }
        /// Thresholds already announced for this instance, plus 100 for the cap and -1 for
        /// the projection, which is one-shot in the same way.
        public List<int> Fired { get; set; }

        public Seen(string instance, double utilization, DateTimeOffset at, List<int> fired)
        {
            Instance = instance;
            Utilization = utilization;
            At = at;
            Fired = fired;
        }

        public Seen Copy() => new(Instance, Utilization, At, new List<int>(Fired));

        public bool Equals(Seen? o) => o is not null && Instance == o.Instance &&
            Utilization.Equals(o.Utilization) && At == o.At && Fired.SequenceEqual(o.Fired);
        public override bool Equals(object? obj) => Equals(obj as Seen);
        public override int GetHashCode() => HashCode.Combine(Instance, Utilization, At);
    }

    public Dictionary<string, Seen> Windows { get; set; } = new();

    public void Prune(DateTimeOffset before) =>
        Windows = Windows.Where(kv => kv.Value.At >= before).ToDictionary(kv => kv.Key, kv => kv.Value);

    public bool Equals(AlertState? o) => o is not null && Windows.Count == o.Windows.Count &&
        Windows.All(kv => o.Windows.TryGetValue(kv.Key, out var s) && s.Equals(kv.Value));
    public override bool Equals(object? obj) => Equals(obj as AlertState);
    public override int GetHashCode() => Windows.Count;
}

public static class Alerting
{
    /// Marker slots inside `Fired`, kept out of the 0-100 range the real thresholds occupy.
    internal const int ProjectionSlot = -1;
    internal const int LimitSlot = 100;

    /// A reset is only news if the window was being used; below this it is noise.
    public const double ResetFloor = 25;

    /// The projection fires only once the cap is close enough to act on.
    public const double ProjectionHorizon = 3600;

    public static List<int> Thresholds(Config config) =>
        new HashSet<int> { (int)config.LimitYellowPct, (int)config.LimitRedPct, 95 }
            .Where(x => x > 0 && x < 100).OrderBy(x => x).ToList();

    /// Evaluates one poll's worth of windows. A stale window still updates its recorded
    /// utilization, so a later fresh one does not mistake a gap for a reset, but never fires.
    public static List<AlertEvent> Evaluate(IEnumerable<LimitWindow> windows, Config config,
                                            AlertState state, IEnumerable<Pace>? paces = null,
                                            DateTimeOffset? now = null,
                                            Func<LimitWindow, bool>? isStale = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var paceList = paces?.ToList() ?? new List<Pace>();
        var events = new List<AlertEvent>();
        var levels = Thresholds(config);
        foreach (var window in windows)
        {
            if (window.IsUninformative) continue;
            var stale = isStale?.Invoke(window) ?? false;
            var id = window.Id;
            var instance = InstanceKey(window);
            state.Windows.TryGetValue(id, out var previous);
            var seen = previous is not null && previous.Instance == instance
                ? previous.Copy()
                : new AlertState.Seen(instance, window.Utilization, t, new List<int>());

            // A rollover: same window, new instance, and the old one had been used
            var rolledOver = previous is not null && previous.Instance != instance
                && previous.Utilization >= ResetFloor && window.Utilization < previous.Utilization;
            if (rolledOver && !stale && config.Alerts)
            {
                events.Add(new AlertEvent(new AlertKind.Reset(), window.Provider, window.Key,
                    $"{window.Provider} {window.DisplayName} reset",
                    $"Back to {Round(100 - window.Utilization)}% remaining.",
                    $"{instance}|reset"));
            }

            if (!stale && config.Alerts)
            {
                if (window.Utilization >= 100 && !seen.Fired.Contains(LimitSlot))
                {
                    seen.Fired.Add(LimitSlot);
                    events.Add(new AlertEvent(new AlertKind.LimitReached(), window.Provider, window.Key,
                        $"{window.Provider} {window.DisplayName} is at its limit",
                        ResetPhrase(window, t) ?? "No reset time reported.",
                        $"{instance}|100"));
                }
                else
                {
                    foreach (var level in levels)
                    {
                        if (window.Utilization < level || seen.Fired.Contains(level)) continue;
                        seen.Fired.Add(level);
                        var body = string.Join(" · ",
                            new[] { RemainingPhrase(window), ResetPhrase(window, t) }.Where(s => s is not null));
                        events.Add(new AlertEvent(new AlertKind.Threshold(level), window.Provider, window.Key,
                            $"{window.Provider} {window.DisplayName} at {Round(window.Utilization)}%",
                            body, $"{instance}|{level}"));
                    }
                }

                var pace = paceList.FirstOrDefault(p => p.Provider == window.Provider && p.Key == window.Key);
                if (pace is not null && pace.HitsLimitBeforeReset && window.Utilization < 100 &&
                    pace.TimeToLimit(t) is { } toLimit && toLimit <= ProjectionHorizon &&
                    !seen.Fired.Contains(ProjectionSlot))
                {
                    seen.Fired.Add(ProjectionSlot);
                    var reset = pace.TimeToReset(t) is { } r ? $", {Pace.Short(r)} until it resets" : "";
                    events.Add(new AlertEvent(new AlertKind.Projection(), window.Provider, window.Key,
                        $"{window.Provider} {window.DisplayName} will run out first",
                        $"About {Pace.Short(toLimit)} left at the current rate{reset}.",
                        $"{instance}|projection"));
                }
            }

            seen.Instance = instance;
            seen.Utilization = window.Utilization;
            seen.At = t;
            state.Windows[id] = seen;
        }
        state.Prune(t.AddSeconds(-30 * 86400));
        return events;
    }

    /// Windows are the same instance while they share a reset time. Without one the window
    /// counts as a single ongoing instance.
    internal static string InstanceKey(LimitWindow window) =>
        window.ResetsAt is { } r ? $"{window.Id}|{r.ToUnixTimeSeconds()}" : $"{window.Id}|open";

    internal static string? RemainingPhrase(LimitWindow window)
    {
        var left = 100 - window.Utilization;
        return left > 0 ? $"{Round(left)}% left" : null;
    }

    internal static string? ResetPhrase(LimitWindow window, DateTimeOffset now) =>
        window.ResetsAt is { } r && r > now ? $"resets in {Pace.Short((r - now).TotalSeconds)}" : null;

    // Swift's rounded() goes half away from zero; Math.Round alone would go to even
    internal static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// Where the alert state lives: its own file, because it is a record of what happened, not
/// something anyone should hand-edit.
public static class AlertStore
{
    public static string PathFor(string? home = null) =>
        Path.Combine(RedlineHome.DataDir(home), "alerts.json");

    public static AlertState Load(string? path = null)
    {
        path ??= PathFor();
        return StateFile.Load(path, "alerts.state_corrupt", "state file did not decode; starting over",
            Decode, () => new AlertState());
    }

    public static bool Save(AlertState state, string? path = null)
    {
        path ??= PathFor();
        var windows = new JsonObject();
        foreach (var (id, s) in state.Windows.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            windows[id] = new JsonObject
            {
                ["at"] = StateFile.Stamp(s.At),
                ["fired"] = new JsonArray(s.Fired.Select(f => (JsonNode)f).ToArray()),
                ["instance"] = s.Instance,
                ["utilization"] = s.Utilization,
            };
        return StateFile.Save(path, new JsonObject { ["windows"] = windows }, "alerts.save_failed", "could not write state");
    }

    private static AlertState Decode(JsonObject json)
    {
        if (json["windows"] is not JsonObject windows) throw new FormatException("windows missing");
        var state = new AlertState();
        foreach (var (id, node) in windows)
        {
            if (node is not JsonObject o) throw new FormatException("window is not an object");
            var fired = new List<int>();
            if (o["fired"] is not JsonArray arr) throw new FormatException("fired missing");
            foreach (var f in arr) fired.Add(Json.Int(f) ?? throw new FormatException("fired entry"));
            state.Windows[id] = new AlertState.Seen(
                Json.Str(o["instance"]) ?? throw new FormatException("instance missing"),
                Json.Num(o["utilization"]) ?? throw new FormatException("utilization missing"),
                StateFile.ParseDate(o["at"]), fired);
        }
        return state;
    }
}

/// The shared shape of RedLine's small state files: sorted-key JSON, ISO 8601 dates to the
/// second, absence is ordinary and silent, corruption leaves a diagnostic.
internal static class StateFile
{
    public static string Stamp(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static readonly string[] Formats = { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss'Z'" };

    public static DateTimeOffset ParseDate(JsonNode? node)
    {
        var s = Json.Str(node) ?? throw new FormatException("date missing");
        if (DateTimeOffset.TryParseExact(s, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d;
        throw new FormatException($"not an ISO 8601 date: {s}");
    }

    public static T Load<T>(string path, string corruptCode, string corruptMessage,
                            Func<JsonObject, T> decode, Func<T> empty)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return empty();
            text = File.ReadAllText(path);
        }
        catch { return empty(); }
        try
        {
            var json = Json.ParseObject(text) ?? throw new FormatException("not a JSON object");
            return decode(json);
        }
        catch (Exception e)
        {
            Diag.Log.Error(corruptCode, corruptMessage, new() { ["path"] = path, ["error"] = e.Message });
            return empty();
        }
    }

    public static bool Save(string path, JsonObject json, string failCode, string failMessage)
    {
        try
        {
            Json.WriteAtomic(path, json.ToJsonString());
            return true;
        }
        catch (Exception e)
        {
            Diag.Log.Error(failCode, failMessage, new() { ["path"] = path, ["error"] = e.Message });
            return false;
        }
    }
}
