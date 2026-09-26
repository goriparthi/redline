// Invented data for every state the dashboard has to hold (port of Previews.swift). DEBUG only;
// nothing here touches the config, the transcripts or the network.
#if DEBUG
using Redline.Core;

namespace Redline.App.Dashboard;

/// <summary>Sample figures, in one namespace so it is obvious a number came from here and not a scan.</summary>
public static class SampleData
{
    /// <summary>Real time, not a fixed date: staleness is measured against the clock.</summary>
    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>The default pricing table, so costs are arithmetic over the sample tokens.</summary>
    public static readonly Config Config = new();

    public static LimitWindow Window(string provider, string key, double pct, double resetsIn = 2 * 3600,
                                     Provenance source = Provenance.Official) =>
        new(provider, key, pct, Now.AddSeconds(resetsIn), source);

    static readonly (string Provider, string Model, int PerDay)[] DefaultProviders =
    [
        ("Claude", "claude-sonnet-4-6", 260_000),
        ("Claude", "claude-opus-4-6", 70_000),
        ("Codex", "gpt-5-codex", 92_000),
        // No pricing entry, so it is counted in tokens and left out of cost: the "+" on every total
        ("Ollama", "qwen3-coder:30b", 27_000),
    ];

    /// <summary>Invented records; everything else is derived by the app's own aggregation.</summary>
    /// <remarks>Deterministic, not random, so one render can be compared against the last.</remarks>
    public static List<Entry> Entries(int days = 14, (string Provider, string Model, int PerDay)[]? providers = null)
    {
        var output = new List<Entry>();
        long seed = 7;
        double Next()
        {
            seed = (seed * 1_103_515_245 + 12_345) & 0x7FFF_FFFF;
            return seed % 1000 / 1000.0;
        }
        foreach (var (provider, model, perDay) in providers ?? DefaultProviders)
        {
            for (int day = 0; day < days; day++)
            {
                // One quiet stretch, so an empty bucket is visible in the charts
                var quiet = day == 4 || day == 5;
                var scale = quiet ? 0.05 : 0.3 + Next() * 0.9;
                for (int slot = 0; slot < 4; slot++)
                {
                    // Always in the past and spread across the day, so "today" and "5 hours" differ
                    var ts = Now.AddDays(day - days + 1).AddHours(-(slot * 4 + 1));
                    var io = (int)(perDay * scale / 4);
                    output.Add(new Entry(provider, null, ts, model, io * 3 / 4, io / 4, io / 3, io / 12, 0));
                }
            }
        }
        return output;
    }

    /// <summary>Everything reading normally: three providers, healthy windows, live figures.</summary>
    public static DashboardData Normal
    {
        get
        {
            var records = Entries();
            var d = new DashboardData
            {
                Loading = false,
                Availability = new ProviderAvailability(["Claude", "Codex", "Ollama"]),
                ReadProviders = ["Claude", "Codex", "Ollama"],
                Focus = Redline.Core.Config.AutoProvider,
                Range = 14,
                ScannedAt = Now,
                ClaudeLimitsAsOf = Now,
                OllamaReachableHint = true,
                Theme = "auto",
            };
            d.Limits = [Window("Claude", "five_hour", 34), Window("Claude", "seven_day", 41),
                        Window("Codex", "five_hour", 22), Window("Codex", "seven_day", 58)];
            d.Paces = PaceEstimator.Paces(d.Limits, now: Now);
            d.Trends = Redline.Core.Trends.Trend(records, Bucketing.Day, 14, Config, Now);
            d.Hourly = Redline.Core.Trends.Trend(records, Bucketing.Hour, 24, Config, Now);
            d.Models = Redline.Core.Trends.ByModel(records, Now.AddDays(-14), Config);
            d.Today = Usage.Aggregate(records, DashboardModel.StartOfLocalDay(Now), Config);
            d.Block5h = Usage.Aggregate(records, Now.AddHours(-5), Config);
            d.Day24 = Usage.Aggregate(records, Now.AddHours(-24), Config);
            d.Ranged = Usage.Aggregate(records, Now.AddDays(-14), Config);
            d.Services =
            [
                new("Claude", "none", "All Systems Operational"),
                new("Codex", "none", "All Systems Operational"),
                new("Ollama", "local", "checked directly, no network leaves this PC"),
            ];
            d.ServicesCheckedAt = Now;
            return d;
        }
    }

    /// <summary>A provider up against its cap, another about to run out early: what the warnings are for.</summary>
    public static DashboardData NearLimit
    {
        get
        {
            var d = Normal;
            d.Limits = [Window("Claude", "five_hour", 93), Window("Claude", "seven_day", 71),
                        Window("Codex", "five_hour", 64), Window("Codex", "seven_day", 88)];
            d.Paces = PaceEstimator.Paces(d.Limits, now: Now);
            d.Services =
            [
                new("Claude", "minor", "Elevated error rates"),
                new("Codex", "none", "All Systems Operational"),
            ];
            return d;
        }
    }

    /// <summary>Every way a provider can fail to answer: absent, stopped, and a last-known reading.</summary>
    public static DashboardData Degraded
    {
        get
        {
            var d = Normal;
            d.Availability = new ProviderAvailability(["Claude", "Ollama"]);
            d.ReadProviders = ["Claude", "Ollama"];
            d.OllamaReachableHint = false;
            d.ClaudeLimitsAsOf = Now.AddHours(-4);
            d.Limits = [Window("Claude", "five_hour", 47, source: Provenance.Experimental)];
            d.Paces = [];
            d.LimitsNote = "Rate limited by the usage endpoint; retrying";
            d.Services = [];
            d.ServicesCheckedAt = null;
            return d;
        }
    }

    /// <summary>Mid-scan.</summary>
    public static DashboardData Loading
    {
        get
        {
            var d = Normal;
            d.Loading = true;
            d.ScannedAt = null;
            return d;
        }
    }

    /// <summary>Installed, read, and nothing has happened.</summary>
    public static DashboardData NoData => new()
    {
        Loading = false,
        Availability = new ProviderAvailability(["Claude", "Codex", "Ollama"]),
        ReadProviders = ["Claude", "Codex", "Ollama"],
        ScannedAt = Now,
        OllamaReachableHint = true,
        Theme = "auto",
        // The real bucketing over no records, so the empty state is the one the app draws
        Trends = Redline.Core.Trends.Trend([], Bucketing.Day, 14, Config, Now),
    };

    /// <summary>One provider in view, so the detail pane is what is on screen.</summary>
    public static DashboardData Focused
    {
        get
        {
            var d = Normal;
            d.Focus = "Codex";
            return d;
        }
    }

    /// <summary>The panels the Swift previews leave out: findings, cadence and recorded history.</summary>
    public static DashboardData Everything
    {
        get
        {
            var d = NearLimit;
            var records = Entries();
            d.Findings = new FindingsReport(Now, 14, 212,
            [
                new Finding("sample-mcp", FindingKind.FixNow, FindingBasis.Estimated, "Two MCP servers load in every session but are never called",
                    "Their tool definitions are sent with every request. Sample figures, invented for this preview.",
                    [new FindingEvidence("github", "0 calls in 212 sessions"), new FindingEvidence("sentry", "0 calls in 212 sessions")],
                    estimatedTokens: 1_900_000, estimatedUSD: 5.70, fix: "claude mcp remove sentry"),
                new Finding("sample-habit", FindingKind.Habit, FindingBasis.Measured, "Long sessions carry most of the cache reads",
                    "Sessions past 200 turns account for 61% of cache reads in this window.",
                    [new FindingEvidence("sessions over 200 turns", "14"), new FindingEvidence("share of cache reads", "61%")]),
            ], hidden: 1);
            d.History = new DashboardData.HistorySummary(96, "2026-06-21", "2026-09-25", 41_200_000, 318.42, false, 12_400_000);
            d.Cadence = DashboardModel.CadenceSummaryOf(records, Now);
            return d;
        }
    }

    /// <summary>Ollama in focus with a running server, so the control panel is on screen.</summary>
    public static DashboardData OllamaFocused
    {
        get
        {
            var d = Normal;
            d.Focus = "Ollama";
            return d;
        }
    }

    public static OllamaPanelState OllamaRunning => new()
    {
        Reachable = true,
        Version = "0.12.3",
        Models =
        [
            new OllamaModel("qwen3-coder:30b", 18_600_000_000, "30.5B", "Q4_K_M", Now.AddDays(-9)),
            new OllamaModel("llama3.2:3b", 2_000_000_000, "3.2B", "Q4_K_M", Now.AddDays(-30)),
        ],
        Running = [new OllamaRunningModel("qwen3-coder:30b", 19_800_000_000, 15_840_000_000, Now.AddMinutes(24))],
        CheckedAt = Now,
    };

    public static DashboardData Named(string name) => name.ToLowerInvariant() switch
    {
        "near-limit" or "nearlimit" => NearLimit,
        "degraded" or "unavailable-stale" => Degraded,
        "loading" => Loading,
        "no-data" or "nodata" => NoData,
        "focused" or "provider-detail" => Focused,
        "everything" or "full" => Everything,
        "ollama" => OllamaFocused,
        _ => Normal,
    };

    /// <summary>A model over sample data. Its actions are no-ops, so a click cannot touch the real app.</summary>
    public static DashboardModel Model(DashboardData data)
    {
        var model = new DashboardModel { WritesConfig = false, Data = data, Ollama = OllamaRunning };
        return model;
    }
}
#endif
