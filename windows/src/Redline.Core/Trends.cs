// Time-bucketed usage for the dashboard charts. Every entry already carries a timestamp, so
// history comes from the transcripts themselves; nothing extra is recorded.
namespace Redline.Core;

public sealed record UsagePoint(DateTimeOffset Start, int Io, double Cost, int CacheRead, int CacheWrite);

public sealed record ProviderTrend(string Provider, IReadOnlyList<UsagePoint> Points)
{
    public int TotalIO => Points.Sum(p => p.Io);
    public double TotalCost => Points.Sum(p => p.Cost);
    public UsagePoint? Peak => Points.Count == 0 ? null : Points.MaxBy(p => p.Io);
}

public sealed record ModelShare(string Model, string Provider, int Io, double Cost, bool Priced);

public enum Bucketing { Day, Hour }

/// How far apart the dated labels on a daily axis sit. Tested here so a quarter does not
/// print a label every five days, and both daily charts share one cadence.
public static class DailyAxis
{
    /// Roughly six to eight labels at any range RedLine offers.
    public static int StrideDays(int range) => range switch
    {
        <= 7 => 1,
        <= 14 => 2,
        <= 30 => 5,
        <= 60 => 10,
        _ => 14,
    };
}

public static class Trends
{
    // Buckets are pre-filled with zeros across the whole range so a chart shows a quiet day
    // as a gap at the baseline rather than skipping the date entirely.
    public static List<ProviderTrend> Trend(IEnumerable<Entry> entries, Bucketing by, int count,
                                            Config config, DateTimeOffset? now = null,
                                            TimeZoneInfo? timeZone = null)
    {
        if (count <= 0) return new();
        var tz = timeZone ?? TimeZoneInfo.Local;
        var starts = BucketStarts(by, count, now ?? DateTimeOffset.UtcNow, tz);
        if (starts.Count == 0) return new();
        var first = starts[0];

        // provider -> bucket start -> running totals
        var acc = new Dictionary<string, Dictionary<DateTimeOffset, (int Io, double Cost, int Cr, int Cw)>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Ts < first) continue;
            var start = StartOf(by, e.Ts, tz);
            var cost = Cost(e, config);
            if (!acc.TryGetValue(e.Provider, out var byStart)) acc[e.Provider] = byStart = new();
            (int Io, double Cost, int Cr, int Cw) slot = byStart.TryGetValue(start, out var s) ? s : (0, 0.0, 0, 0);
            slot.Io += e.Input + e.Output;
            slot.Cost += cost;
            slot.Cr += e.CacheRead;
            slot.Cw += e.Cache5m + e.Cache1h;
            byStart[start] = slot;
        }

        return acc.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(provider =>
        {
            var byStart = acc[provider];
            var points = starts.Select(start =>
            {
                var s = byStart.TryGetValue(start, out var x) ? x : (0, 0.0, 0, 0);
                return new UsagePoint(start, s.Item1, s.Item2, s.Item3, s.Item4);
            }).ToList();
            return new ProviderTrend(provider, points);
        }).ToList();
    }

    internal static List<DateTimeOffset> BucketStarts(Bucketing by, int count, DateTimeOffset now,
                                                      TimeZoneInfo timeZone)
    {
        var current = StartOf(by, now, timeZone);
        var output = new List<DateTimeOffset>(Math.Max(0, count));
        // Oldest first, ending with the bucket now falls in
        for (int back = count - 1; back >= 0; back--)
        {
            if (by == Bucketing.Hour)
            {
                output.Add(current.AddHours(-back));
            }
            else
            {
                // Calendar arithmetic on days keeps local midnight across a DST change
                var local = TimeZoneInfo.ConvertTime(current, timeZone).DateTime.Date.AddDays(-back);
                output.Add(FromLocal(local, timeZone));
            }
        }
        return output;
    }

    /// Start of the local day or hour `instant` falls in, as a UTC instant.
    public static DateTimeOffset StartOf(Bucketing by, DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, timeZone);
        var dt = local.DateTime;
        if (by == Bucketing.Hour)
        {
            // Keep the instant's own offset so the two hours of a fall-back night stay distinct
            var truncated = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(truncated, local.Offset);
            if (candidate <= instant && instant - candidate < TimeSpan.FromHours(1)) return candidate.ToUniversalTime();
            return FromLocal(truncated, timeZone);
        }
        return FromLocal(dt.Date, timeZone);
    }

    // A wall-clock time to an instant: a skipped time moves forward to the first valid one,
    // an ambiguous one takes the earlier instant, as Foundation's Calendar does
    private static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo timeZone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        int guard = 0;
        while (timeZone.IsInvalidTime(t) && guard++ < 24 * 60) t = t.AddMinutes(1);
        TimeSpan offset = timeZone.IsAmbiguousTime(t)
            ? timeZone.GetAmbiguousTimeOffsets(t).Max()
            : timeZone.GetUtcOffset(t);
        return new DateTimeOffset(t, offset).ToUniversalTime();
    }

    public static List<ModelShare> ByModel(IEnumerable<Entry> entries, DateTimeOffset since, Config config)
    {
        var acc = new Dictionary<string, (string Provider, int Io, double Cost, bool Priced)>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Ts < since) continue;
            var priced = config.Price(e.Model) is not null;
            (string Provider, int Io, double Cost, bool Priced) slot = acc.TryGetValue(e.Model, out var s) ? s : (e.Provider, 0, 0.0, priced);
            slot.Io += e.Input + e.Output;
            slot.Cost += Cost(e, config);
            slot.Priced = priced;
            acc[e.Model] = slot;
        }
        // Largest first, with a stable tiebreak so the order does not jitter between refreshes
        return acc.Select(kv => new ModelShare(kv.Key, kv.Value.Provider, kv.Value.Io, kv.Value.Cost, kv.Value.Priced))
                  .OrderByDescending(m => m.Io).ThenBy(m => m.Model, StringComparer.Ordinal).ToList();
    }

    internal static double Cost(Entry e, Config config) => Usage.CostOf(e, config) ?? 0;
}
