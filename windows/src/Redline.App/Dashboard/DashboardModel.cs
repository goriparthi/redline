// The dashboard's model: its data, the app's callbacks, and the range scan (port of
// DashboardModel in Dashboard.swift). Scanning runs off the UI thread; results land on it.
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Redline.Core;

namespace Redline.App.Dashboard;

public sealed class DashboardModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    DashboardData data = new();
    OllamaPanelState ollama = new();
    string ollamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://127.0.0.1:11434";

    /// <summary>The state drawn. Replacing it, or setting any of its properties, repaints.</summary>
    public DashboardData Data
    {
        get => data;
        set
        {
            if (ReferenceEquals(data, value)) return;
            data.PropertyChanged -= Forward;
            data = value;
            data.PropertyChanged += Forward;
            Raise(nameof(Data));
        }
    }

    /// <summary>Ollama's live state for the Ollama panel. The app replaces it after each probe.</summary>
    public OllamaPanelState Ollama { get => ollama; set { ollama = value; Raise(nameof(Ollama)); } }
    /// <summary>The server the Ollama panel names, OLLAMA_HOST or the loopback default.</summary>
    public string OllamaHost { get => ollamaHost; set { ollamaHost = value; Raise(nameof(OllamaHost)); } }

    /// <summary>Forces a status re-fetch past the 15 minute throttle ("Check now").</summary>
    public Action? OnStatusRefresh { get; set; }
    /// <summary>Installs the Claude usage feed from the empty Limits state.</summary>
    public Action? OnSetupClaudeTracking { get; set; }
    /// <summary>Called after the theme choice is saved; the dashboard window re-themes itself.</summary>
    public Action<string>? OnThemeChange { get; set; }
    /// <summary>Runs the findings checks again on demand.</summary>
    public Action? OnRescanFindings { get; set; }
    /// <summary>Hides one finding for findingsSnoozeDays.</summary>
    public Action<string>? OnDismissFinding { get; set; }
    /// <summary>Brings every snoozed finding back now.</summary>
    public Action? OnRestoreFindings { get; set; }
    /// <summary>Loads a model into Ollama's memory (OllamaService.start).</summary>
    public Action<string>? OnOllamaStart { get; set; }
    /// <summary>Unloads a model; the download is kept (OllamaService.stop).</summary>
    public Action<string>? OnOllamaStop { get; set; }

    /// <summary>False for sample data, so a click in a preview cannot write the real config.</summary>
    public bool WritesConfig { get; init; } = true;

    readonly UsageStore claude = new();
    readonly CodexStore codex = new();
    readonly OllamaStore ollamaStore = new();
    readonly object scanLock = new();
    readonly Dispatcher dispatcher;
    // Bumped on every load, so a scan the user has moved past cannot publish over a newer one
    int generation;

    public DashboardModel()
    {
        dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        data.PropertyChanged += Forward;
    }

    void Forward(object? sender, PropertyChangedEventArgs e) => Raise(nameof(Data));

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Read on the scan thread with everything else, since it is another file walk.</summary>
    public static DashboardData.HistorySummary? HistorySummaryOf(string? root = null)
    {
        using var warehouse = new Warehouse(root);
        var records = warehouse.Load();
        if (records.Count == 0) return null;
        var byDay = Warehouse.ByDay(records);
        return new DashboardData.HistorySummary(
            byDay.Count, byDay.FirstOrDefault().Day, byDay.LastOrDefault().Day,
            byDay.Sum(d => d.Io), byDay.Sum(d => d.Cost),
            records.All(r => r.Priced), warehouse.SizeBytes);
    }

    /// <summary>The shape of the range, across every provider even while one is focused.</summary>
    /// <remarks>Filtering to one provider would insert gaps that were never idle time.</remarks>
    public static DashboardData.CadenceSummary? CadenceSummaryOf(IReadOnlyList<Entry> entries, DateTimeOffset now)
    {
        if (entries.Count == 0) return null;
        var stretches = Cadence.Stretches(entries);
        return new DashboardData.CadenceSummary(
            Cadence.ByHourOfDay(entries),
            Cadence.Current(entries, now: now)?.Length,
            stretches.Count == 0 ? null : stretches.Max(s => s.Length),
            Cadence.Streak(entries, now),
            Cadence.ActiveDays(entries).Count);
    }

    public void SetFocus(string provider) => Data.Focus = provider;

    public void SetTheme(string theme)
    {
        if (WritesConfig && !Config.Write(new Dictionary<string, JsonNode?> { ["dashboardTheme"] = theme })) return;
        Data.Theme = theme;
        OnThemeChange?.Invoke(theme);
    }

    /// <summary>Rescans `range` days off the UI thread. `limits` are the app's current windows.</summary>
    public void Load(int range, IReadOnlyList<LimitWindow> limits)
    {
        var gen = ++generation;
        Data.Range = range;
        Data.Loading = true;
        Data.Limits = limits;
        var cfg = Config.Load();
        var days = range;
        // Read once here, so a rail, a card and an alert cannot disagree about the thresholds
        Data.ReadProviders = cfg.Providers;
        Data.YellowPct = cfg.LimitYellowPct;
        Data.PollSeconds = cfg.PollIntervalSeconds;
        Data.RedPct = cfg.LimitRedPct;
        var detected = ProviderAvailability.Detect(ollamaReachable: Data.OllamaReachableHint);
        Data.Availability = detected;
        // With one track there is nothing to aggregate across, so focus it automatically
        if (!detected.HasChoice && detected.Installed.Count > 0) Data.Focus = detected.Installed[0];

        Task.Run(() =>
        {
            lock (scanLock) Scan(gen, cfg, days);
        });
    }

    void Scan(int gen, Config cfg, int days)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-days);
        var entries = new List<Entry>();
        if (cfg.RecordHistory)
        {
            // Ask the store, not the transcripts: the range is not capped by what Claude Code kept
            using var warehouse = new Warehouse();
            entries = warehouse.Entries(since: since).Where(e => cfg.Wants(e.Provider)).ToList();
        }
        else
        {
            if (cfg.Wants(UsageStore.Provider)) entries.AddRange(claude.Scan(days));
            if (cfg.Wants(CodexStore.Provider)) entries.AddRange(codex.Scan(days).Entries);
            if (cfg.Wants(OllamaStore.Provider)) entries.AddRange(ollamaStore.Scan(days));
        }
        // One cutoff drives every ranged figure, so tiles and charts always name the same span
        var trends = Redline.Core.Trends.Trend(entries, Bucketing.Day, days, cfg, now);
        var hourly = Redline.Core.Trends.Trend(entries, Bucketing.Hour, 24, cfg, now);
        var models = Redline.Core.Trends.ByModel(entries, since, cfg);
        var today = Usage.Aggregate(entries, StartOfLocalDay(now), cfg);
        var block5h = Usage.Aggregate(entries, now.AddHours(-5), cfg);
        var day24 = Usage.Aggregate(entries, now.AddHours(-24), cfg);
        var ranged = Usage.Aggregate(entries, since, cfg);
        var history = cfg.RecordHistory ? HistorySummaryOf() : null;
        var cadence = cfg.MindfulCues ? CadenceSummaryOf(entries, now) : null;
        dispatcher.BeginInvoke(() =>
        {
            if (gen != generation) return;
            Data.Trends = trends;
            Data.Hourly = hourly;
            Data.Models = models;
            Data.Today = today;
            Data.Block5h = block5h;
            Data.Day24 = day24;
            Data.Ranged = ranged;
            Data.ScannedAt = now;
            Data.History = history;
            Data.Cadence = cadence;
            Data.Loading = false;
        });
    }

    internal static DateTimeOffset StartOfLocalDay(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local);
        var midnight = local.Date;
        while (TimeZoneInfo.Local.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight)).ToUniversalTime();
    }
}
