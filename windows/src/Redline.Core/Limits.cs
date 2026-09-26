// Rate-limit windows, normalized across providers so one display path serves all of them.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed record LimitWindow(string Provider, string Key, double Utilization,
                                 DateTimeOffset? ResetsAt, Provenance Source = Provenance.Unknown)
{
    // Provider is part of the identity: several providers share window keys
    public string Id => $"{Provider}|{Key}";

    /// Nominal length of this window in seconds, or null when the key names no duration.
    public double? Length
    {
        get
        {
            if (Key == "five_hour") return 5 * 3600;
            if (Key.StartsWith("seven_day")) return 7 * 86400;
            if (Key.StartsWith("window_") && Key.Length > 8)
            {
                var mid = Key[7..^1];
                if (double.TryParse(mid, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    if (Key.EndsWith('m')) return n * 60;
                    if (Key.EndsWith('d')) return n * 86400;
                }
            }
            return null;
        }
    }

    public string DisplayName => Key switch
    {
        "five_hour" => "Session (5h)",
        // Only Claude splits the week by model, so "all models" would mislead elsewhere
        "seven_day" => Provider == "Claude" ? "Week (all models)" : "Week",
        "seven_day_opus" => "Week (Opus)",
        "seven_day_sonnet" => "Week (Sonnet)",
        _ => Key.Replace('_', ' '),
    };

    public bool IsRecognized => Key == "five_hour" || Key.StartsWith("seven_day");

    // An unrecognized window at zero with no reset time says nothing actionable
    public bool IsUninformative => !IsRecognized && Utilization == 0 && ResetsAt is null;
}

public static class LimitParser
{
    private static readonly Dictionary<string, int> Order = new()
    {
        ["five_hour"] = 0, ["seven_day"] = 1, ["seven_day_sonnet"] = 2, ["seven_day_opus"] = 3,
    };

    public static List<LimitWindow> Sorted(IEnumerable<LimitWindow> windows) =>
        windows.OrderBy(w => Order.GetValueOrDefault(w.Key, 9))
               .ThenBy(w => w.Key, StringComparer.Ordinal).ToList();

    // Drops windows whose reset has passed: a rolled-over window's old percentage is meaningless
    public static List<LimitWindow> Unexpired(IEnumerable<LimitWindow> windows, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        return windows.Where(w => w.ResetsAt is not { } r || r > t).ToList();
    }

    public static DateTimeOffset? ParseIso(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    // The Claude usage endpoint is undocumented; scan for {utilization, resets_at} shapes
    public static List<LimitWindow> ClaudeUsage(JsonObject json)
    {
        var output = new List<LimitWindow>();
        void Walk(JsonObject dict, int depth)
        {
            foreach (var (k, v) in dict)
            {
                if (v is not JsonObject d) continue;
                if (Json.Num(d["utilization"]) is { } util)
                {
                    var resets = ParseIso(Json.Str(d["resets_at"]));
                    output.Add(new LimitWindow("Claude", k, util, resets, Provenance.Experimental));
                }
                else if (depth < 2) Walk(d, depth + 1);
            }
        }
        Walk(json, 0);
        return Sorted(output);
    }

    // Codex reports windows by length in minutes, and resets_at is epoch seconds
    public static List<LimitWindow> CodexRateLimits(JsonObject dict)
    {
        var output = new List<LimitWindow>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (dict[slot] is not JsonObject d || Json.Num(d["used_percent"]) is not { } pct) continue;
            var minutes = Json.Num(d["window_minutes"]) ?? 0;
            DateTimeOffset? resets = Json.Num(d["resets_at"]) is { } s
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)) : null;
            output.Add(new LimitWindow("Codex", KeyForWindowMinutes(minutes), pct, resets, Provenance.Official));
        }
        return Sorted(output);
    }

    public static string KeyForWindowMinutes(double m) => m switch
    {
        300 => "five_hour",
        10080 => "seven_day",
        0 => "window",
        // Keep unknown windows visible rather than dropping them silently
        _ => m < 1440 ? $"window_{(int)m}m" : $"window_{(int)(m / 1440)}d",
    };
}
