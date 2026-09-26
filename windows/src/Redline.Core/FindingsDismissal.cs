// Which findings the user has already dealt with, and until when. A dismissal hides one for
// a while; if it is still true when the snooze runs out it comes back.
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed class FindingsDismissals : IEquatable<FindingsDismissals>
{
    private Dictionary<string, DateTimeOffset> _dismissed;

    /// Finding id to the moment it was dismissed. Ids are stable strings such as
    /// `mcp-unused`, so a dismissal survives a rescan.
    public IReadOnlyDictionary<string, DateTimeOffset> Dismissed => _dismissed;

    public FindingsDismissals(IDictionary<string, DateTimeOffset>? dismissed = null)
    {
        _dismissed = dismissed is null ? new() : new Dictionary<string, DateTimeOffset>(dismissed);
    }

    public bool IsEmpty => _dismissed.Count == 0;

    public bool IsHidden(string id, int snoozeDays, DateTimeOffset? now = null)
    {
        if (!_dismissed.TryGetValue(id, out var at)) return false;
        var t = now ?? DateTimeOffset.UtcNow;
        // A snooze in the future means a clock that moved backwards, not a longer snooze
        if (at > t) return true;
        return (t - at).TotalSeconds < snoozeDays * 86400.0;
    }

    public void Dismiss(string id, DateTimeOffset? at = null) => _dismissed[id] = at ?? DateTimeOffset.UtcNow;

    public void Restore(string id) => _dismissed.Remove(id);

    public void RestoreAll() => _dismissed.Clear();

    /// Drops snoozes that have run out, so the file does not grow for every finding ever.
    public void Prune(int snoozeDays, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        _dismissed = _dismissed.Where(kv => IsHidden(kv.Key, snoozeDays, t))
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public bool Equals(FindingsDismissals? o) => o is not null && _dismissed.Count == o._dismissed.Count &&
        _dismissed.All(kv => o._dismissed.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public override bool Equals(object? obj) => Equals(obj as FindingsDismissals);
    public override int GetHashCode() => _dismissed.Count;
}

public static class FindingsDismissalStore
{
    public static string PathFor(string? home = null) =>
        Path.Combine(RedlineHome.DataDir(home), "findings-dismissed.json");

    public static FindingsDismissals Load(string? path = null)
    {
        path ??= PathFor();
        // A file that exists but will not decode means dismissals silently came back
        return StateFile.Load(path, "findings.dismissals_corrupt", "dismissal file did not decode; starting over",
            Decode, () => new FindingsDismissals());
    }

    public static bool Save(FindingsDismissals state, string? path = null)
    {
        path ??= PathFor();
        var dismissed = new JsonObject();
        foreach (var (id, at) in state.Dismissed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            dismissed[id] = StateFile.Stamp(at);
        return StateFile.Save(path, new JsonObject { ["dismissed"] = dismissed },
            "findings.dismissals_save_failed", "could not write dismissals");
    }

    private static FindingsDismissals Decode(JsonObject json)
    {
        if (json["dismissed"] is not JsonObject d) throw new FormatException("dismissed missing");
        var map = new Dictionary<string, DateTimeOffset>();
        foreach (var (id, node) in d) map[id] = StateFile.ParseDate(node);
        return new FindingsDismissals(map);
    }
}

public static class FindingsReportExt
{
    /// The same report with dismissed findings removed and a count of what was hidden. Every
    /// surface reads its counts off this, so dismissing quiets the menu line too.
    public static FindingsReport Visible(this FindingsReport report, FindingsDismissals dismissals,
                                         int snoozeDays, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var kept = report.Findings.Where(f => !dismissals.IsHidden(f.Id, snoozeDays, t)).ToList();
        return new FindingsReport(report.GeneratedAt, report.WindowDays, report.SessionsScanned,
                                  kept, report.Findings.Count - kept.Count);
    }
}
