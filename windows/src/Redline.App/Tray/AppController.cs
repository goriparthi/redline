// The tray app's heart, ported from AppDelegate.swift: the refresh loop, the provider wiring,
// and the state every surface (tray icon, flyout, widgets, dashboard, settings) is drawn from.
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Redline.App.Dashboard;
using Redline.App.Services;
using Redline.App.Settings;
using Redline.Core;

namespace Redline.App.Tray;

public sealed partial class AppController : IDisposable
{
    /// <summary>Which source produced the current Claude windows, shown as a provenance line.</summary>
    public enum ClaudeLimitsSource { Feed, External, SignIn, CliToken }

    readonly Dispatcher ui;
    Config config = Config.Load();
    readonly UsageStore claudeStore = new();
    readonly CodexStore codexStore = new();
    readonly OllamaStore ollamaStore = new();
    readonly OAuthManager oauth;
    readonly SerialQueue queue = new("usage-scan");
    // Its own queue: a fleet read behind a full transcript ingest would answer minutes late
    readonly SerialQueue fleetQueue = new("fleet-scan");

    DateTimeOffset? lastRefresh;
    bool refreshing;
    Agg today = new();
    Agg block5h = new();
    Agg week = new();
    List<LimitWindow> claudeLimits = new();
    List<LimitWindow> codexLimits = new();
    string? limitsStatus;
    string? updateStatus;
    string? updateURL;
    Updates.CheckResult.Available? updatePackage;
    string? updateVersion;
    bool updateInFlight;
    ServiceStatus.Report? claudeService;
    ServiceStatus.Report? codexService;
    DateTimeOffset? serviceStatusAt;
    DateTimeOffset? claudeLimitsAt;
    ClaudeLimitsSource? claudeLimitsSource;
    FileSystemWatcher? feedWatcher;
    DispatcherTimer? feedWatchDebounce;
    DateTime? feedSeenMtime;
    // Rebuilding while a submenu is open collapses it, so that rebuild waits for the close
    bool menuNeedsRebuild;
    Snapshot.OllamaSection? ollamaSection;
    /// <summary>Daily rollups and limit samples that outlive the transcripts they came from.</summary>
    readonly Warehouse warehouse = new();
    DateTimeOffset? lastPrune;
    readonly AlertCenter alertCenter = new();
    readonly FindingsService findingsService;
    FindingsReport? findingsReport;
    FindingsDismissals findingsDismissals = FindingsDismissalStore.Load();
    List<LimitSample> limitSamples = new();
    DateTimeOffset? samplesLoadedAt;
    List<Pace> paces = new();
    List<LimitWindow> recordedWindows = new();
    readonly ClaudeFleetStore fleetStore = new();
    FleetSnapshot fleet = new();
    FileSystemWatcher? fleetWatcher;
    readonly Dictionary<string, FileSystemWatcher> fleetRecordWatchers = new(StringComparer.OrdinalIgnoreCase);
    DispatcherTimer? fleetWatchDebounce;
    DispatcherTimer? fleetTimer;
    // Recomputed on each refresh so a provider installed later shows up without a restart
    ProviderAvailability availability = ProviderAvailability.Detect();
    DispatcherTimer? timer;
    readonly Updates updates = new();
    readonly LaunchAtLogin login = new();
    readonly OllamaService ollamaService = new();
    readonly ServiceStatusService statusFeeds = new();

    Window? dashboardWindow;
    Window? setupWindow;
    SettingsWindow? settingsWindow;
    readonly DashboardModel dashboardModel = new();
    readonly SettingsModel settingsModel;

    TrayIcon? tray;
    // The second icon of the split layout: Claude's week, beside the session in `tray`
    TrayIcon? weekTray;
    readonly FlyoutWindow flyout = new();
    readonly WidgetManager widgets;

    IEnumerable<LimitWindow> AllLimits => claudeLimits.Concat(codexLimits);
    List<LimitWindow> InformativeLimits => AllLimits.Where(w => !w.IsUninformative).ToList();

    public AppController()
    {
        ui = Application.Current.Dispatcher;
        oauth = new OAuthManager(config.OAuth, config.UseCLIToken);
        settingsModel = new SettingsModel(config);
        findingsService = new FindingsService(context: new DispatcherSynchronizationContext(ui));
        widgets = new WidgetManager(() => OpenDashboard());
        flyout.Dismissed += (_, _) => menuNeedsRebuild = false;
    }

    /// <summary>applicationDidFinishLaunching: the icon, the callbacks, the watchers, the first refresh.</summary>
    public void Start(bool openDashboard)
    {
        Diag.Log.Info("app.launched", "RedLine started", new() { ["windows"] = Environment.OSVersion.VersionString });
        tray = new TrayIcon();
        tray.Clicked += OnTrayClicked;
        alertCenter.Notifier = new NotifyIconNotifier(tray.NotifyIcon);
        UpdateTitle();
        var firstRun = Config.IsFirstRun();

        // The dashboard's "Check now" button: past the throttle, straight to the feeds
        dashboardModel.OnSetupClaudeTracking = () => InstallStatuslineFeed();
        dashboardModel.OnStatusRefresh = () =>
        {
            serviceStatusAt = null;
            RefreshServiceStatus();
        };
        findingsService.Updated += report =>
        {
            findingsReport = report;
            PublishFindings();
            dashboardModel.Data.FindingsScanning = false;
            RebuildMenu();
        };
        dashboardModel.OnDismissFinding = id =>
        {
            findingsDismissals.Dismiss(id);
            findingsDismissals.Prune(config.FindingsSnoozeDays);
            FindingsDismissalStore.Save(findingsDismissals);
            PublishFindings();
            RebuildMenu();
        };
        dashboardModel.OnRestoreFindings = () =>
        {
            findingsDismissals.RestoreAll();
            FindingsDismissalStore.Save(findingsDismissals);
            PublishFindings();
            RebuildMenu();
        };
        dashboardModel.OnRescanFindings = () =>
        {
            // Flipped before the scan starts, so the click always changes something on screen
            dashboardModel.Data.FindingsScanning = true;
            if (!findingsService.Refresh(config) && !findingsService.IsRunning)
                dashboardModel.Data.FindingsScanning = false;
        };
        dashboardModel.OnOllamaStart = model => _ = Task.Run(() => ollamaService.StartAsync(model));
        dashboardModel.OnOllamaStop = model => _ = Task.Run(() => ollamaService.StopAsync(model));
        dashboardModel.OllamaHost = ollamaService.HostDescription;
        ollamaService.StateChanged += state => ui.BeginInvoke(() => dashboardModel.Ollama = PanelState(state));

        ApplyLaunchAtLoginPreference();
        ScheduleTimer();
        ScheduleUpdateTimer();
        WatchFeedDirectory();
        WatchFleetDirectory();
        Refresh();
        // Off the critical path: pace needs yesterday's readings, and nothing waits on them
        LoadStoredSamples();
        widgets.Restore(SnapshotStore.ReadAny());
        if (openDashboard) OpenDashboard();
        // Ask once what to read, rather than switching every provider on by default
        if (firstRun) ShowSetup();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ui.BeginInvoke(Refresh);
    }

    static OllamaPanelState PanelState(OllamaState s) => new()
    {
        Reachable = s.Reachable, Models = s.Models, Running = s.Running, Version = s.Version,
        Error = s.Error, Busy = s.Busy, CheckedAt = s.CheckedAt,
    };

    void RefreshOllamaPanel() => _ = Task.Run(ollamaService.ReloadAsync);

    /// <summary>Config says launch at sign-in; the Run value follows, except from a test profile or a dev build.</summary>
    void ApplyLaunchAtLoginPreference()
    {
        if (RedlineHome.IsOverridden || Updates.IsDevelopmentBuild()) return;
        if (config.LaunchAtLogin && !login.PointsHere) login.Enable();
    }

    // Service status

    /// <summary>Statuspage feeds for the hosted providers, only when switched on, at most every 15 minutes.</summary>
    void RefreshServiceStatus()
    {
        if (!config.StatusChecks) return;
        if (serviceStatusAt is { } last && (DateTimeOffset.UtcNow - last).TotalSeconds < 900) return;
        serviceStatusAt = DateTimeOffset.UtcNow;
        var status = statusFeeds;
        void Land(bool claude, ServiceStatus.Report? report)
        {
            if (claude) claudeService = report; else codexService = report;
            PublishSnapshot();
            dashboardModel.Data.Services = SnapshotServices();
            dashboardModel.Data.ServicesCheckedAt = DateTimeOffset.UtcNow;
            RebuildMenu();
        }
        _ = Task.Run(async () =>
        {
            var r = await status.FetchAsync(ServiceStatus.ClaudeUrl);
            await ui.InvokeAsync(() => Land(true, r));
        });
        _ = Task.Run(async () =>
        {
            var r = await status.FetchAsync(ServiceStatus.CodexUrl);
            await ui.InvokeAsync(() => Land(false, r));
        });
    }

    List<Snapshot.Service> SnapshotServices()
    {
        var services = new List<Snapshot.Service>();
        if (claudeService is { } c) services.Add(new Snapshot.Service(UsageStore.Provider, c.Indicator, c.Description));
        if (codexService is { } x) services.Add(new Snapshot.Service(CodexStore.Provider, x.Indicator, x.Description));
        // Ollama's probe is local, so it needs no network opt-in, and comes from the live section
        if (config.Wants(OllamaStore.Provider) && ollamaSection is { } o)
            services.Add(new Snapshot.Service(OllamaStore.Provider, o.Reachable ? "local" : "local-down",
                                              "checked directly, no network leaves this PC"));
        return services;
    }

    /// <summary>The provider's health indicator and the words beside it; null when nothing was checked.</summary>
    (string Indicator, string Phrase)? ServiceMark(string provider)
    {
        if (provider == OllamaStore.Provider)
        {
            if (ollamaSection?.Reachable is not { } reachable) return null;
            return reachable ? ("local", "local, running") : ("local-down", "local, not reachable");
        }
        if (!config.StatusChecks) return null;
        var report = provider == UsageStore.Provider ? claudeService
                   : provider == CodexStore.Provider ? codexService : null;
        // Honest interim state: the fetch is in flight, and silence would read as broken
        if (report is null) return (ServiceGlyph.Checking, "checking status…");
        return (report.Indicator, report.Phrase);
    }

    // Timers

    void ScheduleTimer()
    {
        timer?.Stop();
        timer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(15, config.PollIntervalSeconds)), DispatcherPriority.Background,
            (_, _) => { ReloadConfig(); Refresh(); }, ui);
        timer.Start();
    }

    void ReloadConfig()
    {
        var old = config.PollIntervalSeconds;
        var oldAuto = config.AutoCheckUpdates;
        config = Config.Load();
        oauth.Update(config.OAuth, config.UseCLIToken);
        if (config.PollIntervalSeconds != old) ScheduleTimer();
        if (config.AutoCheckUpdates != oldAuto) ScheduleUpdateTimer();
        UpdateTitle();
    }

    // Refresh

    /// <summary>Claude Code rewrites the feed on every statusline draw; this re-reads limits when its mtime moves.</summary>
    /// <remarks>The directory is watched because the feeder replaces the file atomically.</remarks>
    void WatchFeedDirectory()
    {
        var path = StatuslineFeed.DefaultPath();
        var dir = Path.GetDirectoryName(path)!;
        try { Directory.CreateDirectory(dir); } catch { }
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler onChange = (_, _) => ui.BeginInvoke(ScheduleFeedRead);
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, _) => ui.BeginInvoke(ScheduleFeedRead);
            w.EnableRaisingEvents = true;
            feedWatcher = w;
        }
        catch (Exception e)
        {
            Diag.Log.Warn("feed.watch_failed", "could not watch the data directory", new() { ["error"] = e.Message });
        }
    }

    void ScheduleFeedRead()
    {
        // Coalesce the burst of writes a statusline redraw produces into one read
        feedWatchDebounce?.Stop();
        feedWatchDebounce = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            feedWatchDebounce?.Stop();
            DateTime? mtime = null;
            try
            {
                var p = StatuslineFeed.DefaultPath();
                if (File.Exists(p)) mtime = File.GetLastWriteTimeUtc(p);
            }
            catch { }
            // Our own snapshot writes land in the same directory; only the sidecar moving counts
            if (mtime is null || mtime == feedSeenMtime) return;
            feedSeenMtime = mtime;
            RefreshLimits();
        }, ui);
        feedWatchDebounce.Start();
    }

    FindingsReport? VisibleFindings => findingsReport?.Visible(findingsDismissals, config.FindingsSnoozeDays);

    void PublishFindings() => dashboardModel.Data.Findings = VisibleFindings;

    void Refresh()
    {
        // Cheap wiring check first, so a clobbered settings.json costs at most one poll
        StatuslineInstaller.RepairIfNeeded();
        RefreshLocal();
        RefreshLimits();
        RefreshOllamaSection();
        RefreshFleet();
        RefreshAvailability();
        RefreshServiceStatus();
        findingsService.RefreshIfDue(config);
        PruneHistoryIfDue(DateTimeOffset.UtcNow);
    }

    /// <summary>Pace needs yesterday's readings, and they live in a file. Read once at launch, off the UI thread.</summary>
    void LoadStoredSamples()
    {
        if (!config.RecordHistory) return;
        var now = DateTimeOffset.UtcNow;
        queue.Post(() =>
        {
            var samples = warehouse.LimitSamples(since: now.AddSeconds(-86400));
            ui.BeginInvoke(() =>
            {
                limitSamples = samples;
                samplesLoadedAt = now;
                UpdatePaces();
            });
        });
    }

    // Polled here rather than in the widget, which reads only the snapshot
    void RefreshAvailability()
    {
        var reachable = ollamaSection?.Reachable ?? false;
        var next = ProviderAvailability.Detect(ollamaReachable: reachable,
                                               claudeAccount: oauth.IsSignedIn || config.OAuth.IsConfigured);
        if (next.Equals(availability)) return;
        availability = next;
        RebuildMenu();
    }

    void RefreshOllamaSection()
    {
        if (!config.Wants(OllamaStore.Provider))
        {
            if (ollamaSection is not null)
            {
                ollamaSection = null;
                PublishSnapshot();
            }
            return;
        }
        _ = Task.Run(async () =>
        {
            var section = await ollamaService.SnapshotSectionAsync();
            await ui.InvokeAsync(() =>
            {
                ollamaSection = section;
                PublishSnapshot();
                // The dashboard renders these; stale defaults painted "not reachable" while ollama answered
                dashboardModel.Data.Services = SnapshotServices();
                dashboardModel.Data.ServicesCheckedAt = DateTimeOffset.UtcNow;
                dashboardModel.Data.OllamaReachableHint = section.Reachable;
                RebuildMenu();
            });
        });
    }

    static DateTimeOffset StartOfToday(DateTimeOffset now)
    {
        var local = now.ToLocalTime().Date;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    void RefreshLocal()
    {
        if (refreshing) return;
        refreshing = true;
        var cfg = config.Clone();
        queue.Post(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var entries = new List<Entry>();
            var codex = new List<LimitWindow>();
            Agg t = new(), b = new(), w = new();
            try
            {
                if (cfg.RecordHistory)
                {
                    // The durable path: each transcript is read from where the last pass stopped
                    if (cfg.Wants(UsageStore.Provider)) claudeStore.Ingest(warehouse, now);
                    if (cfg.Wants(CodexStore.Provider)) codex = codexStore.Ingest(warehouse, now).Limits;
                    if (cfg.Wants(OllamaStore.Provider)) ollamaStore.Ingest(warehouse, now);
                    warehouse.RollupPending(cfg);
                    entries = warehouse.Entries(now.AddSeconds(-7 * 86400));
                }
                else
                {
                    // Keeping no history means there is no store to ask, so the whole window is parsed
                    if (cfg.Wants(UsageStore.Provider)) entries.AddRange(claudeStore.Scan(7, now));
                    if (cfg.Wants(CodexStore.Provider))
                    {
                        var snap = codexStore.Scan(7, now);
                        entries.AddRange(snap.Entries);
                        codex = snap.Limits;
                    }
                    if (cfg.Wants(OllamaStore.Provider)) entries.AddRange(ollamaStore.Scan(7, now));
                }
                EvaluateCadence(entries, cfg, now);
                t = Usage.Aggregate(entries, StartOfToday(now), cfg);
                b = Usage.Aggregate(entries, now.AddSeconds(-5 * 3600), cfg);
                w = Usage.Aggregate(entries, now.AddSeconds(-7 * 86400), cfg);
            }
            catch (Exception e)
            {
                Diag.Log.Error("scan.failed", "usage scan failed", new() { ["error"] = e.Message });
            }
            ui.BeginInvoke(() =>
            {
                today = t;
                block5h = b;
                week = w;
                codexLimits = codex;
                lastRefresh = DateTimeOffset.UtcNow;
                refreshing = false;
                UpdateTitle();
                PublishSnapshot();
                RebuildMenu();
            });
        });
    }

    /// <summary>The widget cannot poll or parse transcripts, so it is handed a snapshot.</summary>
    void PublishSnapshot()
    {
        // Everything that reacts to a new reading hangs off this one call, whichever route it came by
        UpdatePaces();
        RecordLimitsIfChanged();
        EvaluateAlerts();
        PublishSidecar();
        var services = SnapshotServices();
        // Same filter the menu applies, so the widget never inherits empty unnamed windows
        var snap = new Snapshot(lastRefresh ?? DateTimeOffset.UtcNow, InformativeLimits, today, week, ollamaSection,
                                services.Count == 0 ? null : services, claudeLimitsAt);
        if (!SnapshotStore.WriteEverywhere(snap)) return;
        // Nudge the widgets rather than waiting for their own minute tick
        widgets.Update(snap);
    }

    /// <summary>Burn rate and time to limit for every window that can support the claim.</summary>
    void UpdatePaces()
    {
        paces = PaceEstimator.Paces(InformativeLimits, limitSamples);
        dashboardModel.Data.Paces = paces;
    }

    /// <summary>Appends readings to the history and refreshes the samples, off the UI thread, only when moved.</summary>
    void RecordLimitsIfChanged()
    {
        if (!config.RecordHistory) return;
        var windows = InformativeLimits;
        if (windows.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var unchanged = windows.SequenceEqual(recordedWindows);
        var sampleAge = samplesLoadedAt is { } at ? (now - at).TotalSeconds : double.MaxValue;
        if (unchanged && sampleAge <= 300) return;
        recordedWindows = windows;
        samplesLoadedAt = now;
        queue.Post(() =>
        {
            warehouse.RecordLimits(windows, now);
            var samples = warehouse.LimitSamples(since: now.AddSeconds(-86400));
            ui.BeginInvoke(() => limitSamples = samples);
        });
    }

    /// <summary>Cues about how the work is spread out. Runs on the scan thread; a streak needs more days.</summary>
    void EvaluateCadence(List<Entry> entries, Config cfg, DateTimeOffset now)
    {
        if (!cfg.MindfulCues) return;
        IReadOnlyList<Entry> window = entries;
        if (cfg.RecordHistory)
        {
            double days = Math.Max(cfg.StreakDays + 2, 9);
            window = warehouse.Entries(now.AddSeconds(-days * 86400));
        }
        var cues = alertCenter.EvaluateCadence(window, cfg, now);
        if (cues.Count == 0) return;
        ui.BeginInvoke(() => dashboardModel.Data.Cues = cues);
    }

    /// <summary>Ages entries out once a day. The rollups are kept forever, so this trims grain, not history.</summary>
    void PruneHistoryIfDue(DateTimeOffset now)
    {
        if (!config.RecordHistory) return;
        if (lastPrune is { } last && (now - last).TotalSeconds < 86400) return;
        lastPrune = now;
        queue.Post(() => warehouse.PruneEntries(now));
    }

    /// <summary>A reading nobody can vouch for is not news, so staleness is decided per window.</summary>
    void EvaluateAlerts()
    {
        var stale = ClaudeLimitsAreStale;
        alertCenter.Evaluate(InformativeLimits, paces, config,
                             isStale: w => w.Provider == UsageStore.Provider && stale);
    }

    /// <summary>The windows in the shape other local tools read; removed when the setting is off.</summary>
    void PublishSidecar()
    {
        if (!config.PublishSidecar)
        {
            Sidecar.Remove();
            return;
        }
        Sidecar.Publish(InformativeLimits, $"redline/{Updates.CurrentVersion}", lastRefresh ?? DateTimeOffset.UtcNow,
                        new Sidecar.Totals(today.Io, today.Cost, !today.HasUnpriced),
                        new Sidecar.Totals(week.Io, week.Cost, !week.HasUnpriced),
                        claudeLimitsAt);
    }

    void RefreshLimits()
    {
        if (!config.Wants(UsageStore.Provider))
        {
            // Switching Claude off has to drop its rows too, or the menu looks unchanged
            if (claudeLimits.Count > 0 || claudeLimitsAt is not null || limitsStatus is not null)
            {
                claudeLimits = new();
                claudeLimitsAt = null;
                claudeLimitsSource = null;
                limitsStatus = null;
                UpdateTitle();
                PublishSnapshot();
                RebuildMenu();
            }
            return;
        }
        // While the sidecar is fresh it wins outright: no credential, no prompt, no request
        var feed = StatuslineFeed.Read(StatuslineFeed.DefaultPath());
        if (feed is { IsEmpty: false } && feed.IsFresh())
        {
            ApplyLive(feed.Windows, feed.UpdatedAt, ClaudeLimitsSource.Feed);
            return;
        }
        // Someone else's sidecar, only after our own feed had first refusal and only while fresh
        if (Sidecar.ReadExternal(config.ExternalUsagePath) is { } external)
        {
            ApplyLive(external.Windows, external.UpdatedAt, ClaudeLimitsSource.External);
            return;
        }
        // Nothing to fetch without a credential; the sidecar's last reading is the whole answer
        if (!(oauth.IsSignedIn || config.UseCLIToken))
        {
            ApplyFeedOnly(feed);
            return;
        }
        _ = Task.Run(async () =>
        {
            (List<LimitWindow>? Windows, string? Error) result;
            try { result = await oauth.FetchLimitsAsync(); }
            catch (Exception e) { result = (null, e.Message); }
            await ui.InvokeAsync(() => LandFetch(result.Windows, result.Error, feed));
        });
    }

    void ApplyLive(IReadOnlyList<LimitWindow> windows, DateTimeOffset? at, ClaudeLimitsSource source)
    {
        claudeLimits = windows.ToList();
        claudeLimitsAt = at;
        claudeLimitsSource = source;
        limitsStatus = null;
        dashboardModel.Data.LimitsNote = null;
        dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
        UpdateTitle();
        PublishSnapshot();
        RebuildMenu();
    }

    void LandFetch(List<LimitWindow>? limits, string? err, StatuslineSnapshot? feed)
    {
        if (limits is { Count: > 0 })
        {
            claudeLimits = limits;
            claudeLimitsAt = DateTimeOffset.UtcNow;
            claudeLimitsSource = oauth.UsingCLIToken ? ClaudeLimitsSource.CliToken : ClaudeLimitsSource.SignIn;
            limitsStatus = null;
        }
        else if (feed is { IsEmpty: false })
        {
            // No live source answered; the feed's last reading beats a blank, and its age travels with it
            claudeLimits = feed.Windows.ToList();
            claudeLimitsAt = feed.UpdatedAt;
            claudeLimitsSource = ClaudeLimitsSource.Feed;
            limitsStatus = oauth.IsSignedIn ? err : null;
        }
        else
        {
            // A lost token invalidates the cache, but a wanted feed is only quiet, so it drains by age
            if (err is not null && !oauth.IsSignedIn && !StatuslineInstaller.IsWanted())
            {
                claudeLimits = new();
                claudeLimitsAt = null;
                claudeLimitsSource = null;
            }
            else
            {
                claudeLimits = LimitParser.Unexpired(claudeLimits);
            }
            limitsStatus = err ?? (claudeLimits.Count == 0 ? ClaudeSourceNote : null);
        }
        // The dashboard gets the same honesty as the menu: a missing rail with no reason reads as broken
        dashboardModel.Data.LimitsNote = limitsStatus is { } s ? $"Claude limits: {s}" : null;
        dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
        UpdateTitle();
        PublishSnapshot();
        RebuildMenu();
    }

    /// <summary>The feed alone, with no credential behind it. A gap between readings keeps the last windows.</summary>
    void ApplyFeedOnly(StatuslineSnapshot? feed)
    {
        if (feed is { IsEmpty: false })
        {
            claudeLimits = feed.Windows.ToList();
            claudeLimitsAt = feed.UpdatedAt;
            claudeLimitsSource = ClaudeLimitsSource.Feed;
        }
        else
        {
            claudeLimits = LimitParser.Unexpired(claudeLimits);
            if (claudeLimits.Count == 0)
            {
                claudeLimitsAt = null;
                claudeLimitsSource = null;
            }
        }
        limitsStatus = claudeLimits.Count == 0 ? ClaudeSourceNote : null;
        dashboardModel.Data.LimitsNote = limitsStatus is { } s ? $"Claude limits: {s}" : null;
        dashboardModel.Data.ClaudeLimitsAsOf = claudeLimitsAt;
        UpdateTitle();
        PublishSnapshot();
        RebuildMenu();
    }

    const string FeedWaiting = "Waiting for Claude Code to report usage";
    const string FeedIdle = "No Claude Code activity since the last reset";

    /// <summary>A sidecar on disk has reported before, so expiry is idleness; none at all has yet to see a draw.</summary>
    static string FeedNote => File.Exists(StatuslineFeed.DefaultPath()) ? FeedIdle : FeedWaiting;

    /// <summary>A wired feed with no reading waits on Claude Code; only an unwired one needs a choice.</summary>
    static string ClaudeSourceNote => StatuslineInstaller.IsWanted() ? FeedNote : "No limits source set up";

    // Agent fleet

    /// <summary>Watching the registry turns "which agent is blocked" into a push; the 30s sweep reaps killed sessions.</summary>
    void WatchFleetDirectory()
    {
        if (!config.AgentFleet) return;
        if (fleetTimer is null)
        {
            fleetTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshFleet(), ui);
            fleetTimer.Start();
        }
        if (fleetWatcher is not null) return;
        // Never created here: this is read only against ~/.claude, and the sweep retries the watch
        var root = ClaudeFleetStore.DefaultRoot;
        if (!Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            w.Changed += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
            w.Created += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
            w.Deleted += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
            w.Renamed += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
            w.Error += (_, _) => ui.BeginInvoke(() => { fleetWatcher?.Dispose(); fleetWatcher = null; });
            w.EnableRaisingEvents = true;
            fleetWatcher = w;
        }
        catch { fleetWatcher = null; }
    }

    /// <summary>Follows the current records, so a session that starts is watched and one that exits stops being.</summary>
    void SyncFleetRecordWatchers()
    {
        var wanted = fleet.Sessions.Select(s => s.RecordPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in fleetRecordWatchers.Keys.Where(p => !wanted.Contains(p)).ToList())
        {
            fleetRecordWatchers[path].Dispose();
            fleetRecordWatchers.Remove(path);
        }
        foreach (var path in wanted.Where(p => !fleetRecordWatchers.ContainsKey(p)))
        {
            try
            {
                // A rewrite in place is a change; an atomic replace arrives as a rename or a delete
                var w = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                w.Changed += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
                w.Deleted += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
                w.Renamed += (_, _) => ui.BeginInvoke(ScheduleFleetRead);
                w.EnableRaisingEvents = true;
                fleetRecordWatchers[path] = w;
            }
            catch { }
        }
    }

    /// <summary>A fleet of ten writes ten records for one visible change, so the burst is one read.</summary>
    void ScheduleFleetRead()
    {
        fleetWatchDebounce?.Stop();
        fleetWatchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            fleetWatchDebounce?.Stop();
            RefreshFleet();
        }, ui);
        fleetWatchDebounce.Start();
    }

    void StopWatchingFleet()
    {
        fleetWatcher?.Dispose();
        fleetWatcher = null;
        foreach (var w in fleetRecordWatchers.Values) w.Dispose();
        fleetRecordWatchers.Clear();
        fleetWatchDebounce?.Stop();
        fleetWatchDebounce = null;
        fleetTimer?.Stop();
        fleetTimer = null;
    }

    /// <summary>Off the UI thread: a directory walk plus a process lookup per record.</summary>
    void RefreshFleet()
    {
        if (!config.AgentFleet) return;
        // Claude Code may have started since launch, when the directory did not exist
        if (fleetWatcher is null) WatchFleetDirectory();
        fleetQueue.Post(() =>
        {
            var snap = fleetStore.Scan();
            ui.BeginInvoke(() =>
            {
                if (snap.Equals(fleet)) return;
                fleet = snap;
                SyncFleetRecordWatchers();
                // The badge is the whole point, so it cannot wait for the next poll
                UpdateTitle();
                RebuildMenu();
            });
        });
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        timer?.Stop();
        updates.StopDailyChecks();
        feedWatcher?.Dispose();
        StopWatchingFleet();
        widgets.CloseAll();
        flyout.Close();
        tray?.Dispose();
        weekTray?.Dispose();
        queue.Dispose();
        fleetQueue.Dispose();
    }
}
