// What the dashboard draws, and every figure derived from it (port of DashboardData in
// Dashboard.swift). Setting any property raises PropertyChanged, so the window repaints.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Redline.Core;

namespace Redline.App.Dashboard;

/// <summary>The dashboard's state. Set properties on the UI thread; each set repaints the window.</summary>
public sealed class DashboardData : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>A copy with no listeners, as Swift's value semantics give a struct.</summary>
    public DashboardData Clone()
    {
        var c = (DashboardData)MemberwiseClone();
        c.PropertyChanged = null;
        return c;
    }

    int range = 14;
    ProviderAvailability availability = ProviderAvailability.Detect();
    string focus = Config.AutoProvider;
    IReadOnlyList<ProviderTrend> trends = [];
    IReadOnlyList<ProviderTrend> hourly = [];
    IReadOnlyList<ModelShare> models = [];
    IReadOnlyList<LimitWindow> limits = [];
    IReadOnlyList<Pace> paces = [];
    FindingsReport? findings;
    bool findingsScanning;
    HistorySummary? history;
    CadenceSummary? cadence;
    IReadOnlyList<CadenceCue> cues = [];
    IReadOnlyList<Snapshot.Service> services = [];
    DateTimeOffset? servicesCheckedAt;
    string theme = Config.Load().DashboardTheme;
    string? limitsNote;
    DateTimeOffset? claudeLimitsAsOf;
    Agg today = new();
    Agg block5h = new();
    Agg day24 = new();
    Agg ranged = new();
    bool loading = true;
    DateTimeOffset? scannedAt;
    bool ollamaReachableHint;
    double pollSeconds = 300;
    IReadOnlyList<string> readProviders = Config.KnownProviders;
    double yellowPct = 60;
    double redPct = 85;

    public int Range { get => range; set => Set(ref range, value); }
    public ProviderAvailability Availability { get => availability; set => Set(ref availability, value); }
    public string Focus { get => focus; set => Set(ref focus, value); }
    public IReadOnlyList<ProviderTrend> Trends { get => trends; set => Set(ref trends, value); }
    public IReadOnlyList<ProviderTrend> Hourly { get => hourly; set => Set(ref hourly, value); }
    public IReadOnlyList<ModelShare> Models { get => models; set => Set(ref models, value); }
    public IReadOnlyList<LimitWindow> Limits { get => limits; set => Set(ref limits, value); }
    /// <summary>Burn rate and projection per window, computed by the app from stored readings.</summary>
    public IReadOnlyList<Pace> Paces { get => paces; set => Set(ref paces, value); }
    /// <summary>Setup findings, refreshed in the background rather than on every open.</summary>
    public FindingsReport? Findings { get => findings; set => Set(ref findings, value); }
    /// <summary>True while a findings scan runs; the button turns into a progress row.</summary>
    public bool FindingsScanning { get => findingsScanning; set => Set(ref findingsScanning, value); }
    /// <summary>What the local warehouse holds, as opposed to what the transcripts still say.</summary>
    public HistorySummary? History { get => history; set => Set(ref history, value); }
    /// <summary>How the work is spread out. Present only when mindful cues are on.</summary>
    public CadenceSummary? Cadence { get => cadence; set => Set(ref cadence, value); }
    /// <summary>Cues raised by the last poll, kept so the panel can show what was said.</summary>
    public IReadOnlyList<CadenceCue> Cues { get => cues; set => Set(ref cues, value); }
    public IReadOnlyList<Snapshot.Service> Services { get => services; set => Set(ref services, value); }
    public DateTimeOffset? ServicesCheckedAt { get => servicesCheckedAt; set => Set(ref servicesCheckedAt, value); }
    /// <summary>"auto", "light" or "dark", as the config spells it.</summary>
    public string Theme { get => theme; set => Set(ref theme, value); }
    /// <summary>Why Claude's rails may be missing, so an empty panel never reads as broken.</summary>
    public string? LimitsNote { get => limitsNote; set => Set(ref limitsNote, value); }
    /// <summary>When Claude's windows were last true; old rails drain to a last-known reading.</summary>
    public DateTimeOffset? ClaudeLimitsAsOf { get => claudeLimitsAsOf; set => Set(ref claudeLimitsAsOf, value); }
    public Agg Today { get => today; set => Set(ref today, value); }
    /// <summary>The session window, so the Session (5h) rail has a volume beside its percentage.</summary>
    public Agg Block5h { get => block5h; set => Set(ref block5h, value); }
    /// <summary>Rolling 24 hours, the window the hourly chart draws.</summary>
    public Agg Day24 { get => day24; set => Set(ref day24, value); }
    /// <summary>Totals over the selected range, not a fixed week.</summary>
    public Agg Ranged { get => ranged; set => Set(ref ranged, value); }
    public bool Loading { get => loading; set => Set(ref loading, value); }
    public DateTimeOffset? ScannedAt { get => scannedAt; set => Set(ref scannedAt, value); }
    public bool OllamaReachableHint { get => ollamaReachableHint; set => Set(ref ollamaReachableHint, value); }
    /// <summary>How often the app rescans, so the header can state the monitoring cadence.</summary>
    public double PollSeconds { get => pollSeconds; set => Set(ref pollSeconds, value); }
    /// <summary>Which providers the config reads. Installed but off has a card saying so.</summary>
    public IReadOnlyList<string> ReadProviders { get => readProviders; set => Set(ref readProviders, value); }
    /// <summary>Config thresholds, so rails, cards and notifications agree on "approaching".</summary>
    public double YellowPct { get => yellowPct; set => Set(ref yellowPct, value); }
    public double RedPct { get => redPct; set => Set(ref redPct, value); }

    /// <summary>Tokens by local hour, the current and longest runs, and consecutive days.</summary>
    public sealed record CadenceSummary(IReadOnlyList<int> Hours, double? CurrentStretch, double? LongestStretch,
                                        int Streak, int Days);

    public sealed record HistorySummary(int Days, string? Earliest, string? Latest, int Tokens, double Cost,
                                        bool Complete, long SizeBytes);

    /// <summary>Totals narrowed to the focused provider, so a focused tile never shows global figures.</summary>
    public sealed record Slice(int Io = 0, double Cost = 0, int CacheRead = 0, bool HasUnpriced = false);

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool FocusingAll => Focus == Config.AutoProvider;

    public IReadOnlyList<Snapshot.Service> VisibleServices => Services.Where(s => Matches(s.Provider)).ToList();

    /// <summary>Providers whose percentages are past the staleness threshold. Only Claude can go stale.</summary>
    public ISet<string> StaleProviders =>
        ClaudeLimitsAsOf is { } asOf && (DateTimeOffset.UtcNow - asOf).TotalSeconds > ProviderOverview.StalenessThreshold
            ? new HashSet<string> { "Claude" } : new HashSet<string>();

    /// <summary>One card per known provider, installed or not: "not found" answers a question.</summary>
    public IReadOnlyList<ProviderCard> ProviderCards => Config.KnownProviders.Select(provider =>
    {
        var reads = ReadProviders.Any(p => Same(p, provider));
        var usage = Ranged.Providers.FirstOrDefault(kv => Same(kv.Key, provider)).Value;
        var service = Services.FirstOrDefault(s => Same(s.Provider, provider));
        var trend = Trends.FirstOrDefault(t => Same(t.Provider, provider))?.Points.Select(p => p.Io).ToList() ?? [];
        return ProviderOverview.Card(
            provider: provider,
            installed: Availability.Has(provider),
            read: reads,
            // Only a local provider can be unreachable; a hosted one's transcripts are on disk
            reachable: ProviderIdentity.Of(provider)?.IsLocal == true ? OllamaReachableHint : null,
            usage: usage,
            hasUnpriced: usage?.Models.Values.Any(m => !m.Priced) ?? false,
            windows: Limits,
            paces: Paces,
            asOf: provider == UsageStore.Provider ? ClaudeLimitsAsOf : null,
            serviceTone: service is null ? null : ServiceGlyph.ToneFor(service.Indicator),
            servicePhrase: service?.Phrase,
            trend: trend,
            limitsNote: provider == UsageStore.Provider ? LimitsNote : null);
    }).ToList();

    /// <summary>What is worth reading before the cards. Empty is the ordinary case.</summary>
    public IReadOnlyList<ProviderOverview.Warning> Warnings =>
        ProviderOverview.Warnings(Limits, Paces, YellowPct, RedPct, StaleProviders);

    public bool Matches(string provider) => FocusingAll || Same(provider, Focus);

    public Slice SliceOf(Agg agg)
    {
        if (FocusingAll) return new Slice(agg.Io, agg.Cost, agg.CacheRead, agg.HasUnpriced);
        var usage = agg.Providers.FirstOrDefault(kv => Same(kv.Key, Focus)).Value;
        if (usage is null) return new Slice();
        return new Slice(usage.Io, usage.Cost, usage.CacheRead, usage.Models.Values.Any(m => !m.Priced));
    }

    public Slice TodaySlice => SliceOf(Today);
    public Slice Block5hSlice => SliceOf(Block5h);
    public Slice Day24Slice => SliceOf(Day24);
    public Slice RangedSlice => SliceOf(Ranged);

    /// <summary>"14 days". One source for every label that names the window.</summary>
    public string RangeLabel => $"{Range} days";

    public IReadOnlyList<ProviderTrend> VisibleTrends => Trends.Where(t => Matches(t.Provider)).ToList();
    public IReadOnlyList<ProviderTrend> VisibleHourly => Hourly.Where(t => Matches(t.Provider)).ToList();
    public IReadOnlyList<ModelShare> VisibleModels => Models.Where(m => Matches(m.Provider)).ToList();
    /// <summary>Unnamed windows at zero are dropped, matching the menu.</summary>
    public IReadOnlyList<LimitWindow> VisibleLimits => Limits.Where(w => !w.IsUninformative && Matches(w.Provider)).ToList();

    public Pace? PaceFor(LimitWindow window) =>
        Paces.FirstOrDefault(p => p.Provider == window.Provider && p.Key == window.Key);
}

/// <summary>Ollama's live server state (OllamaState in OllamaService.swift), fed by the app.</summary>
public sealed record OllamaPanelState
{
    public bool Reachable { get; init; }
    public IReadOnlyList<OllamaModel> Models { get; init; } = [];
    public IReadOnlyList<OllamaRunningModel> Running { get; init; } = [];
    public string? Version { get; init; }
    public string? Error { get; init; }
    /// <summary>Models with a start or stop in flight, whose buttons are disabled.</summary>
    public IReadOnlySet<string> Busy { get; init; } = new HashSet<string>();
    public DateTimeOffset? CheckedAt { get; init; }
}
