// Provider-agnostic usage records and aggregation.
using System.Globalization;

namespace Redline.Core;

/// One usage record. `Origin` is "path#byteoffset" for records with no id of their own.
public sealed record Entry(string Provider, string? Key, DateTimeOffset Ts, string Model,
                           int Input, int Output, int CacheRead, int Cache5m, int Cache1h,
                           string? Origin = null);

public sealed record ModelUsage
{
    public int Io { get; set; }
    public double Cost { get; set; }
    // False when no pricing key matched, so cost is deliberately absent rather than zero
    public bool Priced { get; set; } = true;
}

public sealed class ProviderUsage
{
    public int Io { get; set; }
    public double Cost { get; set; }
    public int CacheRead { get; set; }
    public int CacheWrite { get; set; }
    public Dictionary<string, ModelUsage> Models { get; } = new();

    // Largest first, with a name tiebreak so the order does not jitter between refreshes
    public List<(string Model, ModelUsage Usage)> RankedModels =>
        Models.Select(kv => (kv.Key, kv.Value))
            .OrderByDescending(t => t.Value.Io).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
}

public sealed class Agg
{
    public int Io { get; set; }
    public int CacheRead { get; set; }
    public int CacheWrite { get; set; }
    public double Cost { get; set; }
    // Models nest under the provider that produced them
    public Dictionary<string, ProviderUsage> Providers { get; } = new();
    public bool HasUnpriced { get; set; }

    public List<(string Provider, ProviderUsage Usage)> RankedProviders =>
        Providers.Select(kv => (kv.Key, kv.Value))
            .OrderByDescending(t => t.Value.Io).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();

    public double Share(int io) => Io > 0 ? (double)io / Io : 0;
}

public static class Usage
{
    public static double? CostOf(Entry e, Config config)
    {
        if (config.Price(e.Model) is not { } p) return null;
        // Cache writes bill at 1.25x (5m) and 2x (1h) of the input rate
        double total = e.Input * p.Input + e.Output * p.Output + e.CacheRead * p.CacheRead
                     + e.Cache5m * p.Input * 1.25 + e.Cache1h * p.Input * 2.0;
        return total / 1_000_000;
    }

    public static Agg Aggregate(IEnumerable<Entry> entries, DateTimeOffset since, Config config)
    {
        var a = new Agg();
        foreach (var e in entries)
        {
            if (e.Ts < since) continue;
            var priced = CostOf(e, config);
            var cost = priced ?? 0;
            if (priced is null && e.Input + e.Output > 0) a.HasUnpriced = true;
            var io = e.Input + e.Output;
            a.Io += io;
            a.CacheRead += e.CacheRead;
            a.CacheWrite += e.Cache5m + e.Cache1h;
            a.Cost += cost;
            if (!a.Providers.TryGetValue(e.Provider, out var pu)) a.Providers[e.Provider] = pu = new ProviderUsage();
            pu.Io += io;
            pu.Cost += cost;
            pu.CacheRead += e.CacheRead;
            pu.CacheWrite += e.Cache5m + e.Cache1h;
            if (!pu.Models.TryGetValue(e.Model, out var mu)) pu.Models[e.Model] = mu = new ModelUsage();
            mu.Io += io;
            mu.Cost += cost;
            mu.Priced = priced is not null;
        }
        return a;
    }

    public static string FmtTokens(long n)
    {
        double d = n;
        var c = CultureInfo.InvariantCulture;
        if (d >= 1_000_000_000) return (d / 1_000_000_000).ToString("0.0", c) + "B";
        if (d >= 1_000_000) return (d / 1_000_000).ToString("0.0", c) + "M";
        if (d >= 1_000) return (d / 1_000).ToString("0.0", c) + "K";
        return n.ToString(c);
    }

    /// "$24,320.91", grouped, because an ungrouped dollar figure misreads by 10x at a glance.
    public static string FmtCost(double c) => "$" + c.ToString("#,##0.00", CultureInfo.InvariantCulture);
}
