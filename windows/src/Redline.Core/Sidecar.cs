// The usage sidecar, published in the shape ClaudeHUD, claude-monitor and CodexBar already read.
// Standard keys first; anything RedLine-specific lives under "redline", which others can ignore.
using System.Text.Json.Nodes;

namespace Redline.Core;

public static class Sidecar
{
    /// Not the feeder's file: reading our own output back would be a loop.
    public static string PublishUrl(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".local/share/redline/usage-snapshot.json");

    /// Seconds an external sidecar may age before it is ignored, same as RedLine's own feed.
    public const double DefaultFreshness = 900;

    public sealed record Totals(int Io, double Cost, bool Priced);

    internal static string Iso(DateTimeOffset d) => DiagnosticsLog.Stamp(d);

    /// Builds the payload apart from writing it, so the shape is testable without a filesystem.
    public static JsonObject Payload(IEnumerable<LimitWindow> windows, DateTimeOffset updatedAt,
                                     string producer, Totals? today = null, Totals? week = null,
                                     DateTimeOffset? limitsAsOf = null)
    {
        var all = windows.ToList();
        var output = new JsonObject
        {
            ["updated_at"] = Iso(updatedAt),
            ["source"] = "redline",
            ["producer"] = producer,
        };

        var claude = all.Where(w => string.Equals(w.Provider, "Claude", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var w in claude.Where(w => w.Key is "five_hour" or "seven_day"))
        {
            // Both spellings on purpose: readers exist for the statusline's and the endpoint's
            var block = new JsonObject { ["used_percentage"] = w.Utilization, ["utilization"] = w.Utilization };
            if (w.ResetsAt is { } r) block["resets_at"] = Iso(r);
            output[w.Key] = block;
        }

        var scoped = claude.Where(w => w.Key.StartsWith("seven_day_", StringComparison.Ordinal)).ToList();
        if (scoped.Count > 0)
        {
            output["model_scoped"] = new JsonArray(scoped.Select(w =>
            {
                var entry = new JsonObject
                {
                    ["display_name"] = DisplayName(w.Key),
                    ["utilization"] = w.Utilization,
                };
                if (w.ResetsAt is { } r) entry["resets_at"] = Iso(r);
                return (JsonNode)entry;
            }).ToArray());
        }

        var extra = new JsonObject
        {
            ["windows"] = new JsonArray(all.Where(w => !w.IsUninformative).Select(w =>
            {
                var entry = new JsonObject
                {
                    ["provider"] = w.Provider,
                    ["key"] = w.Key,
                    ["utilization"] = w.Utilization,
                    ["source"] = w.Source.RawValue(),
                };
                if (w.ResetsAt is { } r) entry["resets_at"] = Iso(r);
                return (JsonNode)entry;
            }).ToArray()),
        };
        if (limitsAsOf is { } asOf) extra["claude_limits_as_of"] = Iso(asOf);
        if (today is not null) extra["today"] = TotalsBlock(today);
        if (week is not null) extra["week"] = TotalsBlock(week);
        output["redline"] = extra;
        return output;
    }

    private static JsonObject TotalsBlock(Totals t) => new()
    {
        ["tokens"] = t.Io,
        ["cost_usd"] = t.Cost,
        // Tokens are the provider's count; dollars are arithmetic over a pricing table
        ["tokens_basis"] = Provenance.Official.RawValue(),
        ["cost_basis"] = Provenance.LocalEstimate.RawValue(),
        ["cost_partial"] = !t.Priced,
    };

    /// "seven_day_opus" -> "Opus": puts back what the display-name round trip took out.
    internal static string DisplayName(string scopedKey)
    {
        var slug = scopedKey.StartsWith("seven_day_", StringComparison.Ordinal) ? scopedKey[10..] : scopedKey;
        return string.Join(" ", slug.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p[..1].ToUpperInvariant() + p[1..]));
    }

    public static bool Publish(IEnumerable<LimitWindow> windows, string producer,
                               DateTimeOffset? updatedAt = null, Totals? today = null,
                               Totals? week = null, DateTimeOffset? limitsAsOf = null, string? to = null)
    {
        var path = to ?? PublishUrl();
        var json = Payload(windows, updatedAt ?? DateTimeOffset.UtcNow, producer, today, week, limitsAsOf);
        string text;
        try { text = SortKeys(json)!.ToJsonString(Json.Pretty); }
        catch
        {
            Diag.Log.Error("sidecar.encode_failed", "could not encode sidecar payload");
            return false;
        }
        try
        {
            Json.WriteAtomic(path, text);
            return true;
        }
        catch (Exception e)
        {
            Diag.Log.Error("sidecar.write_failed", "could not publish sidecar",
                new() { ["path"] = path, ["error"] = e.Message });
            return false;
        }
    }

    // Sorted keys, like JSONSerialization's .sortedKeys, so the file diffs cleanly
    internal static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, SortKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(SortKeys).ToArray()),
        _ => node?.DeepClone(),
    };

    public static void Remove(string? at = null)
    {
        try { File.Delete(at ?? PublishUrl()); } catch { }
    }

    /// Someone else's sidecar: absolute .json path, fresh enough to be about now, not empty.
    public static StatuslineSnapshot? ReadExternal(string path, double freshness = DefaultFreshness,
                                                   DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (ValidExternalPath(path) is not { } p) return null;
        if (StatuslineFeed.Read(p, t) is not { } snap) return null;
        if (snap.UpdatedAt is not { } at || (t - at).TotalSeconds > freshness || snap.IsEmpty) return null;
        return snap;
    }

    /// Null for anything but an absolute path to a .json file; "~" expands to the account home.
    public static string? ValidExternalPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0) return null;
        var expanded = trimmed == "~" ? RedlineHome.AccountHome
            : trimmed.StartsWith("~/") || trimmed.StartsWith("~\\")
                ? Path.Combine(RedlineHome.AccountHome, trimmed[2..]) : trimmed;
        if (!Path.IsPathFullyQualified(expanded)) return null;
        return expanded.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? expanded : null;
    }
}
