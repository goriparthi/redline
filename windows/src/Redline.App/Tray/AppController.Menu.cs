// The dropdown's content, in rebuildMenu's order: token trouble, limits per provider with pace,
// the agent fleet, usage grouped by provider with share bars, then the app's own actions.
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Redline.App.Components;
using Redline.App.Services;
using Redline.Core;

namespace Redline.App.Tray;

public sealed partial class AppController
{
    // Only the name indents; every later column sits at a fixed offset so bars can be compared
    const int LeadWidth = 30;
    const int BarWidth = 10;

    void OnTrayClicked(System.Windows.Forms.MouseButtons button)
    {
        if (button is not (System.Windows.Forms.MouseButtons.Left or System.Windows.Forms.MouseButtons.Right)) return;
        if (flyout.IsVisible) { flyout.Dismiss(); return; }
        // The click that dismissed the flyout by deactivating it must not reopen it
        if ((DateTime.UtcNow - flyout.LastHidden).TotalMilliseconds < 300) return;
        OpenMenu(System.Windows.Forms.Cursor.Position);
    }

    /// <summary>menuWillOpen: build fresh, show, and refresh when the data is over a minute old.</summary>
    void OpenMenu(System.Drawing.Point anchor)
    {
        menuNeedsRebuild = false;
        flyout.ShowAt(BuildMenu(), anchor);
        if (lastRefresh is { } last && (DateTimeOffset.UtcNow - last).TotalSeconds < 60) return;
        Refresh();
    }

    /// <summary>Never rips rows out from under an open submenu; that rebuild lands when it closes.</summary>
    void RebuildMenu()
    {
        if (!flyout.IsVisible) return;
        if (flyout.HasOpenSubmenu)
        {
            if (!menuNeedsRebuild)
            {
                menuNeedsRebuild = true;
                ui.BeginInvoke(WaitForSubmenu, System.Windows.Threading.DispatcherPriority.Background);
            }
            return;
        }
        menuNeedsRebuild = false;
        flyout.Replace(BuildMenu());
    }

    void WaitForSubmenu()
    {
        if (!menuNeedsRebuild || !flyout.IsVisible) return;
        if (flyout.HasOpenSubmenu)
        {
            var t = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500),
                System.Windows.Threading.DispatcherPriority.Background, (s, _) =>
                {
                    ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                    WaitForSubmenu();
                }, ui);
            t.Start();
            return;
        }
        RebuildMenu();
    }

    Color MenuWindowColor(LimitWindow w) => IsStale(w) ? MenuInk.Secondary : MenuInk.Contrasted(LimitColor(w.Utilization));

    static MenuInfo Info(string title, bool secondary = false) =>
        new(new[] { new MenuRun(title, secondary ? MenuInk.Secondary : MenuInk.Primary) });

    static MenuRun Glyph(Func<FrameworkElement> make) => MenuRun.Of(make);

    static FrameworkElement StatusGlyph(string indicator, double size = 13) => new ShapeIcon
    {
        Shape = WidgetView.IndicatorStatus(indicator).Shape(), Size = size,
        Foreground = new SolidColorBrush(MenuInk.Contrasted(MenuInk.Tone(ServiceGlyph.ToneFor(indicator)))),
        Margin = new Thickness(0, 0, 0, -2),
    };

    List<MenuEntry> BuildMenu()
    {
        var menu = new List<MenuEntry>();
        AddTokenTrouble(menu);

        // Drop empty unnamed windows; anything actually being consumed still shows
        var grouped = InformativeLimits.GroupBy(w => w.Provider).ToDictionary(g => g.Key, g => g.ToList());
        if (availability.IsEmpty)
        {
            menu.Add(Info("No supported tool found"));
            menu.Add(Info("    RedLine reads Claude Code, Codex, or Ollama", true));
        }
        else if (grouped.Count == 0)
        {
            menu.Add(Info("Rate limits:"));
            menu.Add(Info("    none available", true));
        }
        // Claude's notes belong under Claude's own section
        var claudeNotesEmitted = false;
        void EmitClaudeNotes()
        {
            claudeNotesEmitted = true;
            if (limitsStatus is { } s) menu.Add(Info($"    {s}"));
            // Provenance, always: three sources can produce the same percentage
            if (claudeLimits.Count > 0 && claudeLimitsSource is { } source)
            {
                menu.Add(Info(source switch
                {
                    ClaudeLimitsSource.Feed => "    via the usage feed",
                    ClaudeLimitsSource.External => "    via another tool's usage sidecar",
                    ClaudeLimitsSource.SignIn => "    via your Claude sign-in",
                    _ => "    via the Claude CLI's token",
                }, true));
            }
        }
        foreach (var provider in grouped.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            menu.Add(ProviderHeader(provider));
            foreach (var w in LimitParser.Sorted(grouped[provider]).Where(WantsWindow))
            {
                var reset = config.ShowResetTimes ? FmtReset(w.ResetsAt) : "";
                // A stale window is drained wholesale; the header's "as of" says when it was true
                var stale = IsStale(w);
                var runs = new List<MenuRun>
                {
                    new("    ● ", MenuWindowColor(w), true),
                    new($"{w.DisplayName}: {Pct(w)}%{reset}", stale ? MenuInk.Secondary : MenuInk.Primary, true),
                };
                // The pace rides on the same row, and is never drawn from a stale reading
                if (!stale && paces.FirstOrDefault(p => p.Provider == w.Provider && p.Key == w.Key) is { } pace &&
                    pace.Compact() is { } summary)
                {
                    runs.Add(new($" · {summary}", pace.HitsLimitBeforeReset ? RL.BrandTone.Amber : MenuInk.Secondary, true));
                }
                menu.Add(new MenuInfo(runs));
            }
            if (provider == UsageStore.Provider) EmitClaudeNotes();
        }
        if (!claudeNotesEmitted) EmitClaudeNotes();

        // Percentages that are off must say so where the user is looking, with the fix one click away
        if (config.Wants(UsageStore.Provider) && !oauth.IsSignedIn && !config.UseCLIToken &&
            claudeLimits.Count == 0 && !StatuslineInstaller.IsWanted())
        {
            menu.Add(Info("    Claude percentages are off", true));
            // The feed needs Claude Code, so a claude.ai-only user is sent to the browser sign-in
            var hasCLI = availability.Has(UsageStore.Provider);
            menu.Add(new MenuAction("Show Claude Percentages…",
                hasCLI ? () => InstallStatuslineFeed() : () => BrowserSignIn(),
                Tooltip: hasCLI
                    ? "Sets up the usage feed: the windows Claude Code hands its statusline. No sign-in, no credential, no network. Other sources are under Settings > Claude Limits Source."
                    : "Signs in with your Claude account in a browser. Other sources are under Settings > Claude Limits Source."));
        }
        menu.Add(MenuSeparator.Instance);

        AddFleet(menu);
        AddSection(menu, "Today", today, detail: true);
        menu.Add(MenuSeparator.Instance);
        AddSection(menu, "Last 5 hours", block5h, detail: false);
        menu.Add(MenuSeparator.Instance);
        AddSection(menu, "Last 7 days", week, detail: true, showIdle: true);
        menu.Add(MenuSeparator.Instance);

        var updated = lastRefresh is { } lr ? lr.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.InvariantCulture) : "never";
        // Human units: "polling every 300s" made users ask whether something was wrong
        var every = config.PollIntervalSeconds % 60 == 0
            ? $"{(int)(config.PollIntervalSeconds / 60)}m" : $"{(int)config.PollIntervalSeconds}s";
        menu.Add(Info($"Updated {updated} · rescans every {every}"));

        // Nothing is shown until a scan has run, so an empty row never implies a clean bill
        if (config.FindingsScans && VisibleFindings is { IsEmpty: false } report)
        {
            menu.Add(new MenuAction($"Setup findings: {report.Summary}", () => OpenDashboard(),
                Tooltip: "What your transcripts say about how Claude Code is configured. Opens the dashboard."));
        }

        menu.Add(new MenuAction("Open Usage Dashboard…", () => OpenDashboard(), "Ctrl+D"));
        menu.Add(new MenuAction("Refresh Now", RefreshNow, "Ctrl+R"));
        menu.Add(MenuSeparator.Instance);

        // A window rather than a submenu: a threshold or a privacy choice needs room to explain itself
        menu.Add(new MenuAction("Settings…", () => OpenSettings(), "Ctrl+,"));
        menu.Add(new MenuSubmenu(new[] { new MenuRun("Desktop Widgets") }, WidgetsMenu,
            "Small windows on the desktop that show the same reading, one per track."));
        menu.Add(MenuSeparator.Instance);

        // The update line doubles as the way to the repository
        if (updateStatus is { } us)
        {
            menu.Add(new MenuAction(us, OpenRepo, Tooltip: "Open the RedLine repository on GitHub",
                Rich: new[] { new MenuRun(us, MenuInk.Secondary) },
                Icon: () => GitHubMark.Image(13, MenuInk.Secondary), Indent: 1));
        }
        if (updatePackage is not null && updateVersion is { } uv)
            menu.Add(new MenuAction($"Install Update to {uv}…", InstallUpdate, Enabled: !updateInFlight));
        if (updateURL is not null)
            menu.Add(new MenuAction("Open Release Page…", OpenUpdate));
        menu.Add(new MenuAction("Check for Updates…", CheckForUpdates));
        menu.Add(MenuSeparator.Instance);

        menu.Add(new MenuAction("Uninstall RedLine…", Uninstall));
        menu.Add(new MenuAction("Quit", Quit, "Ctrl+Q"));
        return menu;
    }

    /// <summary>The one state where the dropdown opens on a problem: a borrowed credential that stopped reading.</summary>
    void AddTokenTrouble(List<MenuEntry> menu)
    {
        if (!config.Wants(UsageStore.Provider) || oauth.IsSignedIn || !config.UseCLIToken) return;
        // The loud row is the one you can click, named for what the user gets back
        var attention = MenuInk.Contrasted(RL.State.Warning.Dark);
        menu.Add(new MenuAction("Reconnect Claude usage…", FixCredentialAccess,
            Tooltip: "RedLine could not read Claude Code's credential. This tries again now.",
            Rich: new[]
            {
                Glyph(() => StatusGlyph("minor")),
                new MenuRun("  Reconnect Claude usage…", attention, true),
            }));
        menu.Add(Info("    Percentages are paused until the credential reads again", true));
        menu.Add(MenuSeparator.Instance);
    }

    /// <summary>"Claude limits:" plus the track mark, and the health glyph and words when something was checked.</summary>
    MenuInfo ProviderHeader(string provider)
    {
        var runs = new List<MenuRun>
        {
            // The dashboard and widget badge a provider with its track glyph; the menu does too
            Glyph(() => new TrackMark(provider, new SolidColorBrush(MenuInk.Provider(provider)), 13) { Margin = new Thickness(0, 0, 0, -2) }),
            new("  "),
            new($"{provider} limits:{StaleSuffix(provider)}", MenuInk.Primary),
        };
        if (ServiceMark(provider) is { } mark)
        {
            runs.Add(new("  "));
            runs.Add(Glyph(() => StatusGlyph(mark.Indicator)));
            runs.Add(new($" {mark.Phrase}", MenuInk.Secondary));
        }
        return new MenuInfo(runs);
    }

    // A fetch that keeps failing must not leave old percentages looking current
    string StaleSuffix(string provider)
    {
        if (provider != UsageStore.Provider || !ClaudeLimitsAreStale || claudeLimitsAt is not { } at) return "";
        return $"  (as of {at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture)})";
    }

    void AddSection(List<MenuEntry> menu, string label, Agg agg, bool detail, bool showIdle = false)
    {
        var partial = agg.HasUnpriced ? "+" : "";
        menu.Add(Info($"{label}: {Usage.FmtCost(agg.Cost)}{partial} est, {Usage.FmtTokens(agg.Io)} in+out"));
        menu.Add(Info($"    cache read {Usage.FmtTokens(agg.CacheRead)}, write {Usage.FmtTokens(agg.CacheWrite)}", true));
        if (!detail) return;
        foreach (var (provider, usage) in agg.RankedProviders)
        {
            var accent = MenuInk.Provider(provider);
            AddRow(menu, 1, accent, provider, agg.Share(usage.Io), usage.Cost, true, usage.Io, accent);
            // Models sit under the provider that produced them, never in one flat list
            foreach (var (model, m) in usage.RankedModels)
            {
                // Detected on the full name, since shortening may drop the cloud tag
                var cloud = OllamaLocality.IsCloud(model) ? "☁ " : "";
                AddRow(menu, 2, null, cloud + Sparkline.ShortModel(model), agg.Share(m.Io), m.Cost, m.Priced, m.Io,
                       MenuInk.Blend(accent, RL.Surface.Ground.Dark, 0.25));
            }
        }
        if (agg.HasUnpriced) menu.Add(Info("    + no pricing entry for models shown as n/a", true));
        // An installed provider that went quiet is not the same as one absent or broken
        if (showIdle)
            foreach (var provider in IdleProviders(agg)) AddIdleRow(menu, provider, label.ToLowerInvariant());
    }

    /// <summary>Providers this PC has, and the config reads, that produced nothing in this window.</summary>
    IEnumerable<string> IdleProviders(Agg agg) =>
        availability.Installed.Where(p => config.Wants(p) && !agg.Providers.ContainsKey(p));

    /// <summary>A hollow dot and words rather than a zero: $0.00 would read as "ran, cost nothing".</summary>
    static void AddIdleRow(List<MenuEntry> menu, string provider, string period)
    {
        const string pad = "    ", marker = "○ ";
        var nameWidth = Math.Max(4, LeadWidth - pad.Length - marker.Length);
        menu.Add(new MenuInfo(new[]
        {
            new MenuRun(pad + marker, MenuInk.Contrasted(MenuInk.Provider(provider))),
            new MenuRun(Sparkline.Pad(provider, nameWidth), MenuInk.Secondary),
            new MenuRun($"no usage in the {period}", MenuInk.Secondary),
        }, Mono: true));
    }

    static void AddRow(List<MenuEntry> menu, int indent, Color? dot, string name, double share, double cost,
                       bool priced, int io, Color tint)
    {
        var pad = new string(' ', 4 * indent);
        var marker = dot is null ? "  " : "● ";
        var nameWidth = Math.Max(4, LeadWidth - pad.Length - marker.Length);
        // "n/a" rather than $0.00, which would read as free. Money is green wherever it appears
        var costText = priced ? Usage.FmtCost(cost) : "n/a";
        menu.Add(new MenuInfo(new[]
        {
            new MenuRun(pad + marker, dot is { } d ? MenuInk.Contrasted(d) : null),
            new MenuRun(Sparkline.Pad(name, nameWidth), MenuInk.Primary),
            new MenuRun(" " + Sparkline.Bar(share, BarWidth), MenuInk.Contrasted(tint)),
            new MenuRun(" " + Sparkline.Percent(share), MenuInk.Secondary),
            new MenuRun("  " + Sparkline.Pad(costText, 11, alignRight: true),
                        priced ? MenuInk.Contrasted(RL.Brandmark.Money.Dark) : MenuInk.Secondary),
            new MenuRun("  " + Sparkline.Pad(Usage.FmtTokens(io), 7, alignRight: true), MenuInk.Secondary),
        }, Mono: true));
    }

    /// <summary>One row per session, waiting first: what the agents are, between what they cost.</summary>
    void AddFleet(List<MenuEntry> menu)
    {
        if (!config.AgentFleet || fleet.IsEmpty) return;
        var now = DateTimeOffset.UtcNow;
        // Named for its source: this is Claude Code's session registry, not every provider's
        menu.Add(new MenuInfo(new[] { new MenuRun("Agents:", MenuInk.Primary), new MenuRun("  Claude Code", MenuInk.Secondary) }));
        foreach (var s in fleet.Sessions)
            menu.Add(new MenuSubmenu(FleetRowTitle(s, now), () => FleetRowMenu(s)));
        menu.Add(MenuSeparator.Instance);
    }

    static IReadOnlyList<MenuRun> FleetRowTitle(FleetSession s, DateTimeOffset now)
    {
        var runs = new List<MenuRun>
        {
            new("    ● ", MenuInk.Contrasted(MenuInk.Tone(FleetTone(s.State)))),
            new(s.Label, MenuInk.Primary),
        };
        // The folder earns its place only when it is not already the name
        if (s.Folder != s.Label) runs.Add(new($"  {s.Folder}", MenuInk.Secondary));
        runs.Add(new($"  {FleetStatusPhrase(s, now)}",
            s.State == FleetState.Waiting ? MenuInk.Contrasted(MenuInk.Tone(ServiceGlyph.Tone.Warning)) : MenuInk.Secondary));
        return runs;
    }

    /// <summary>"waiting 14m, input needed". An unknown status is echoed verbatim, so it reads as itself.</summary>
    static string FleetStatusPhrase(FleetSession s, DateTimeOffset now)
    {
        var phrase = s.Status ?? "unknown";
        if (s.TimeInStatus(now) is { } seconds) phrase += $" {Pace.Short(seconds)}";
        if (s.State == FleetState.Waiting && !string.IsNullOrEmpty(s.WaitingFor)) phrase += $", {s.WaitingFor}";
        return phrase;
    }

    static ServiceGlyph.Tone FleetTone(FleetState state) => state switch
    {
        FleetState.Waiting => ServiceGlyph.Tone.Warning,
        FleetState.Busy => ServiceGlyph.Tone.Healthy,
        _ => ServiceGlyph.Tone.Unknown,
    };

    IReadOnlyList<MenuEntry> FleetRowMenu(FleetSession s)
    {
        var m = new List<MenuEntry>();
        // Resolved up front so an unfocusable session offers the copy instead of a dead row
        if (TerminalFocus.Owner(s.Pid) is { } owner)
        {
            m.Add(new MenuAction(TerminalFocus.MenuTitle(owner), () => FocusFleetSession(s.Pid),
                Tooltip: TerminalFocus.ToolTip(owner)));
        }
        m.Add(new MenuAction("Copy Folder Path", () => CopyText(s.Cwd), Tooltip: s.Cwd));
        if (s.ClaudeUrl is { } url)
        {
            m.Add(new MenuAction("Open in claude.ai", () => OpenUrl(url.AbsoluteUri),
                Tooltip: "The same session on the web. The only link between the local and cloud views that Claude Code hands out."));
        }
        m.Add(MenuSeparator.Instance);
        var detail = $"PID {s.Pid}";
        if (s.Version is { } v) detail += $" · Claude Code {v}";
        if (s.Entrypoint is { } e) detail += $" · {e}";
        m.Add(Info(detail, true));
        m.Add(Info(s.Cwd, true));
        return m;
    }

    IReadOnlyList<MenuEntry> WidgetsMenu()
    {
        var m = new List<MenuEntry>();
        foreach (var t in Enum.GetValues<TrackChoice>())
        {
            var track = t;
            m.Add(new MenuAction($"Add {track.Title()} Widget", () => widgets.Add(track)));
        }
        var existing = widgets.Widgets;
        if (existing.Count > 0)
        {
            m.Add(MenuSeparator.Instance);
            foreach (var w in existing)
            {
                var id = w.Id;
                m.Add(new MenuAction($"Remove {w.Track.Title()} · {w.Family}", () => widgets.Remove(id)));
            }
            m.Add(new MenuAction("Remove All Widgets", widgets.RemoveAll));
        }
        m.Add(MenuSeparator.Instance);
        m.Add(Info("Right-click a widget to change its track or size", true));
        return m;
    }
}
