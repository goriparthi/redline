// How fast a limit window is being spent, and whether that runs out before it resets. Two
// rates are available and they are not equally good, so which one was used travels along.
using System.Globalization;

namespace Redline.Core;

/// Which rate the projection used. Measured beats assumed. Swift nests this as Pace.Basis;
/// it is top level here because C# cannot nest a type named like the Basis property.
public abstract record PaceBasis
{
    /// Differenced from stored readings inside this same window instance.
    public sealed record Measured(int Samples, double Over) : PaceBasis;
    /// Utilization over time elapsed in the window. Blind to a burst that just started.
    public sealed record WindowAverage : PaceBasis;
}

public sealed record Pace(string Provider, string Key, double Utilization, DateTimeOffset? ResetsAt,
                          double RatePerHour, PaceBasis Basis, double? ElapsedFraction,
                          DateTimeOffset? ExhaustsAt)
{
    /// Ahead of pace by this much, as a fraction. Positive means spending faster than the clock.
    public double? PaceDelta => ElapsedFraction is { } f ? Utilization / 100 - f : null;

    /// True when the cap arrives before the reset does, the one case worth interrupting for.
    public bool HitsLimitBeforeReset =>
        ExhaustsAt is { } e && ResetsAt is { } r && e < r;

    public double? TimeToLimit(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        return ExhaustsAt is { } e && e > t ? (e - t).TotalSeconds : null;
    }

    public double? TimeToReset(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        return ResetsAt is { } r && r > t ? (r - t).TotalSeconds : null;
    }

    /// One line for a menu row. Says the cap only when the cap is the news; otherwise how the
    /// window is tracking against its own clock.
    public string? Summary(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (Utilization >= 100) return "limit reached";
        if (HitsLimitBeforeReset && TimeToLimit(t) is { } toLimit)
        {
            var line = $"~{Short(toLimit)} to limit";
            if (TimeToReset(t) is { } toReset && toReset > toLimit)
                line += $", {Short(toReset - toLimit)} before reset";
            return line;
        }
        if (PaceDelta is not { } delta) return null;
        // Inside five points of the clock is not a signal, it is noise
        if (Math.Abs(delta) < 0.05) return "on pace";
        // Ahead of the clock means spending faster than the window refills
        return delta > 0
            ? $"{Round(delta * 100)} points ahead of the clock"
            : $"{Round(-delta * 100)} points to spare";
    }

    /// The same reading without the second clause, for a row that already shows the reset.
    public string? Compact(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (Utilization >= 100) return "limit reached";
        if (HitsLimitBeforeReset && TimeToLimit(t) is { } toLimit) return $"~{Short(toLimit)} to limit";
        return Summary(t);
    }

    public string BasisNote => Basis switch
    {
        PaceBasis.Measured m => $"measured from {m.Samples} readings over {Short(m.Over)}",
        _ => "averaged across this window so far",
    };

    /// "2h 10m", "40m", "3d 4h". Compact enough for a menu row, never rounded to nothing.
    public static string Short(double interval)
    {
        long total = Math.Max(0, (long)Math.Round(interval, MidpointRounding.AwayFromZero));
        long days = total / 86400;
        long hours = (total % 86400) / 3600;
        long minutes = (total % 3600) / 60;
        if (days > 0) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        if (minutes > 0) return $"{minutes}m";
        return "under a minute";
    }

    private static string Round(double x) =>
        ((long)Math.Round(x, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
}

public static class PaceEstimator
{
    /// Minimum span between readings a measured rate is differenced from, scaled to the window,
    /// so a busy quarter hour is never extrapolated across a week.
    public static double MinMeasuredSpan(double length) => Math.Max(600, length / 24);

    /// Minimum time a window must have run before its average says anything.
    public const double MinElapsed = 900;

    /// How far back a measured rate looks, scaled to the window.
    public static double Lookback(double length) => Math.Min(Math.Max(length / 8, 1800), 12 * 3600);

    /// Null when nothing honest can be said: no length, no elapsed time, or no consumption.
    public static Pace? Pace(LimitWindow window, IEnumerable<LimitSample>? samples = null,
                             DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (window.Length is not { } length || window.ResetsAt is not { } resetsAt) return null;
        var start = resetsAt.AddSeconds(-length);
        var elapsed = (t - start).TotalSeconds;
        if (!(elapsed > 0 && elapsed <= length + 3600)) return null;
        var fraction = Math.Min(1, Math.Max(0, elapsed / length));

        double? rate = null;
        PaceBasis basis = new PaceBasis.WindowAverage();

        // Measured first: readings inside this same window instance, recent enough to
        // describe what is happening now rather than what happened this morning.
        var current = new LimitSample(t, window.Provider, window.Key, window.Utilization,
                                      window.ResetsAt, window.Source);
        var cutoff = t.AddSeconds(-Lookback(length));
        var recent = (samples ?? Enumerable.Empty<LimitSample>())
            .Where(s => s.Provider == window.Provider && s.Key == window.Key)
            .Where(s => s.At >= cutoff && s.At <= t)
            .Where(s => s.SameWindowInstance(current))
            .OrderBy(s => s.At)
            .ToList();
        if (recent.Count > 0)
        {
            var first = recent[0];
            var span = (t - first.At).TotalSeconds;
            var climb = window.Utilization - first.Utilization;
            if (span >= MinMeasuredSpan(length) && climb > 0)
            {
                rate = climb / (span / 3600);
                basis = new PaceBasis.Measured(recent.Count + 1, span);
            }
        }

        if (rate is null)
        {
            if (!(elapsed >= MinElapsed && window.Utilization > 0)) return null;
            rate = window.Utilization / (elapsed / 3600);
        }
        if (rate is not { } ratePerHour || !(ratePerHour > 0)) return null;

        var remaining = Math.Max(0, 100 - window.Utilization);
        var exhausts = remaining > 0 ? t.AddSeconds(remaining / ratePerHour * 3600) : t;
        return new Pace(window.Provider, window.Key, window.Utilization, resetsAt,
                        ratePerHour, basis, fraction, exhausts);
    }

    /// Every window that can say something, worst first: the one that will stop you soonest
    /// is the one worth showing when there is room for only one.
    public static List<Pace> Paces(IEnumerable<LimitWindow> windows, IEnumerable<LimitSample>? samples = null,
                                   DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var list = samples?.ToList();
        bool Less(Pace a, Pace b) => (a.TimeToLimit(t), b.TimeToLimit(t)) switch
        {
            ({ } x, { } y) => x < y,
            (null, not null) => false,
            (not null, null) => true,
            _ => a.Utilization > b.Utilization,
        };
        var paces = windows.Select(w => Pace(w, list, t)).OfType<Pace>().ToList();
        return paces.OrderBy(p => p, Comparer<Pace>.Create((a, b) => Less(a, b) ? -1 : Less(b, a) ? 1 : 0))
                    .ToList();
    }
}
