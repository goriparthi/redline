// The dashboard's content (DashboardContent in Dashboard.swift): header, overview or provider
// detail, tiles, limits, status, findings, Ollama, charts, models, cadence and history.
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Redline.App.Components;
using Redline.App.Services;
using Redline.Core;

namespace Redline.App.Dashboard;

/// <summary>Everything the window scrolls, kept apart from the scroller so it can be rendered on its own.</summary>
public sealed class DashboardContent : Border
{
    // Entries are kept a year, so the long ranges answer from the store, not from what is left on disk
    static readonly int[] Ranges = [7, 14, 30, 60, 90];

    readonly DashboardModel model;
    readonly Action<int> onReload;
    readonly Action<string> onFocus;
    readonly Action? onOpenSettings;
    bool pending;
    Theme theme;

    DashboardData data => model.Data;

    public DashboardContent(DashboardModel model, Action<int> onReload, Action<string> onFocus, Action? onOpenSettings = null)
    {
        this.model = model;
        this.onReload = onReload;
        this.onFocus = onFocus;
        this.onOpenSettings = onOpenSettings;
        Padding = new Thickness(RL.Space.Xl);
        model.PropertyChanged += OnModelChanged;
        Loaded += (_, _) => ThemeManager.ThemeChanged += OnSystemTheme;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnSystemTheme;
        Rebuild();
    }

    /// <summary>The appearance this content resolves to: the dashboard's own choice, or the app's.</summary>
    public Theme EffectiveTheme => data.Theme switch
    {
        "light" => Theme.Light,
        "dark" => Theme.Dark,
        _ => ThemeManager.Current,
    };

    void OnSystemTheme(object? sender, Theme _)
    {
        if (data.Theme is not ("light" or "dark")) Schedule();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

    // Many properties land at once when a scan finishes; they are drawn as one change
    void Schedule()
    {
        if (pending) return;
        pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            pending = false;
            Rebuild();
        });
    }

    void ApplyTheme()
    {
        theme = EffectiveTheme;
        var merged = Resources.MergedDictionaries;
        foreach (var d in merged.Where(d => d.Source?.OriginalString.EndsWith("Themes/Dark.xaml") == true
                                            || d.Source?.OriginalString.EndsWith("Themes/Light.xaml") == true).ToList())
            merged.Remove(d);
        // "auto" leaves the app's palette in charge; a forced choice pins this subtree
        if (data.Theme is "light" or "dark") ThemeManager.Pin(this, theme);
        Background = RL.Surface.Ground.Brush(theme);
        if (Window.GetWindow(this) is { } w)
        {
            w.Background = Background;
            ThemeManager.ApplyTitleBar(w, theme);
        }
    }

    public void Rebuild()
    {
        ApplyTheme();
        var body = new StackPanel();
        Ui.Add(body, Header(), 0);
        if (data.Loading)
        {
            Ui.Add(body, Panel("Reading transcripts", null, null, null,
                new RLStateBlock(RLStateKind.Loading, $"Scanning {data.Range} days of usage",
                    "Transcripts are read off the UI thread, so the window stays responsive while this runs")), RL.Space.Lg);
        }
        else
        {
            // The overview answers "is anything about to stop me" before any chart is read
            Ui.Add(body, data.FocusingAll ? Overview() : ProviderDetail(), RL.Space.Lg);
            Ui.Add(body, Tiles(), RL.Space.Lg);
            // Limits before service status: the rails are the product's headline
            Ui.Add(body, LimitsPanel(), RL.Space.Lg);
            Ui.Add(body, ServicePanel(), RL.Space.Lg);
            Ui.Add(body, FindingsPanel(), RL.Space.Lg);
            if (data.Focus == OllamaStore.Provider) Ui.Add(body, OllamaPanel(), RL.Space.Lg);
            Ui.Add(body, ChartsPanels(), RL.Space.Lg);
            Ui.Add(body, HistoryPanel(), RL.Space.Lg);
        }
        Child = body;
    }

    // MARK: - Building blocks

    /// <summary>A titled section: one card, one header, one vocabulary for every panel.</summary>
    FrameworkElement Panel(string title, string? note, string? badge, UIElement? accessory, UIElement content)
    {
        var header = new RLSectionHeader(title, note, accessory);
        UIElement top = badge is null ? header : Ui.Row([new ProviderTile(badge, 18), header], [], RL.Space.Md);
        return new RLCard { Child = Ui.VStack(RL.Space.Lg, top, content) };
    }

    FrameworkElement Empty() => new RLStateBlock(RLStateKind.Empty, "No usage recorded in this range",
        // Ollama keeps no history of its own, so say how to start collecting it
        data.Focus == OllamaStore.Provider ? "Use Set Up Ollama Tracking in Settings to record calls" : null)
    { Height = 60, VerticalAlignment = VerticalAlignment.Top };

    static Grid Columns(double spacing, params UIElement[] items)
    {
        var g = new Grid();
        for (int i = 0; i < items.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i > 0 && items[i] is FrameworkElement fe) fe.Margin = new Thickness(spacing, 0, 0, 0);
            Grid.SetColumn(items[i], i);
            g.Children.Add(items[i]);
        }
        return g;
    }

    // MARK: - Header

    FrameworkElement Header()
    {
        var top = Ui.Row([Identity()], [MonitoringStatus()], RL.Space.Lg);
        // The red rule under the wordmark, as in the supplied lockup
        var rule = new Border
        {
            Width = 132, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
        };
        rule.Bind(Border.BackgroundProperty, RL.Brandmark.Signal);
        return Ui.VStack(RL.Space.Lg, top, rule, ScopeBar());
    }

    FrameworkElement Identity()
    {
        var tagline = new TrackedText { Text = "Know your limit.", UpperCase = false, Tracking = 0.8, TypeStyle = RL.Typography.MonoBody };
        tagline.Bind(TrackedText.ForegroundProperty, RL.Ink.Muted);
        var name = Ui.Text("RedLine", RL.Typography.Title, RL.Ink.Primary);
        var words = Ui.VStack(1, name, tagline);
        var row = Ui.HStack(RL.Space.Lg, new RedlineMarkAdaptive(31) { VerticalAlignment = VerticalAlignment.Center }, words);
        AutomationProperties.SetName(row, "RedLine. Know your limit.");
        return row;
    }

    /// <summary>Whether monitoring is running and when it last did something.</summary>
    FrameworkElement MonitoringStatus()
    {
        var first = Ui.HStack(RL.Space.Sm,
            new RLStatusIndicator(new RLStatus(RLStatusKind.Healthy, "Monitoring"), 11),
            Ui.Text($"Monitoring {ReadProvidersPhrase()}", RL.Typography.Caption, RL.Ink.Secondary));
        first.HorizontalAlignment = HorizontalAlignment.Right;
        var every = Ui.Text($"· every {CadencePhrase()}", RL.Typography.MonoSmall, RL.Ink.Muted);
        every.ToolTip = "How often RedLine rescans. Claude's windows also update the moment Claude Code writes them, without waiting for this.";
        var second = Ui.HStack(RL.Space.Sm,
            Ui.Text(data.ScannedAt is { } at ? $"Updated {Ui.TimeWithSeconds(at)}" : "Reading…", RL.Typography.MonoSmall, RL.Ink.Muted),
            every,
            Ui.IconButton("", "Rescan now", () => onReload(data.Range)),
            onOpenSettings is { } open ? Ui.IconButton("", "Open settings", open) : null);
        second.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.VStack(RL.Space.Xs, first, second);
    }

    /// <summary>"Claude, Codex and Ollama", or "nothing yet" when every provider is off.</summary>
    string ReadProvidersPhrase()
    {
        var names = Config.KnownProviders.Where(p => data.Availability.Has(p)
            && data.ReadProviders.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase))).ToList();
        return names.Count switch
        {
            0 => "nothing yet",
            1 => names[0],
            2 => string.Join(" and ", names),
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>Human units: "every 300s" made users ask what was wrong.</summary>
    string CadencePhrase()
    {
        var s = data.PollSeconds;
        return s % 60 == 0 ? $"{(int)(s / 60)}m" : $"{(int)s}s";
    }

    /// <summary>Provider, appearance and range; two rows when one will not fit, so names never truncate.</summary>
    FrameworkElement ScopeBar()
    {
        var wide = Ui.Row([ProviderScope()], [ThemeControl(), RangeControl()], RL.Space.Lg);
        var narrow = Ui.VStack(RL.Space.Md, ProviderScope(), Ui.Row([ThemeControl()], [RangeControl()], RL.Space.Lg));
        return new ThatFits(wide, narrow);
    }

    FrameworkElement RangeControl()
    {
        var seg = new RLSegmented(Ranges.Select(r => new RLSegment(r, $"{r}d", $"Show the last {r} days")), data.Range);
        seg.Selected += (_, v) => { if (v is int r) onReload(r); };
        return seg;
    }

    FrameworkElement ThemeControl()
    {
        var seg = new RLSegmented(
            [new RLSegment("auto", "Auto", "Follow the system appearance"),
             new RLSegment("light", "Light", "Always use the light appearance"),
             new RLSegment("dark", "Dark", "Always use the dark appearance")], data.Theme, width: 46);
        seg.Selected += (_, v) => { if (v is string t) model.SetTheme(t); };
        return seg;
    }

    FrameworkElement ProviderScope()
    {
        if (!data.Availability.HasChoice)
            // Nothing to choose between, so state the track rather than offer it
            return data.Availability.Installed.Count > 0 ? new ProviderBadge(data.Availability.Installed[0], 14) : new Border();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var choice in data.Availability.TrackChoices)
        {
            var active = string.Equals(choice, data.Focus, StringComparison.OrdinalIgnoreCase);
            var all = choice == Config.AutoProvider;
            var accent = all ? RL.Brandmark.Signal : ProviderAccent.For(choice);
            FrameworkElement mark = all
                ? new RedlineMarkAdaptive(13)
                : ProviderIdentity.Of(choice) is { } id
                    ? new ProviderGlyph(id.Mark, 13) { Foreground = active ? accent.Brush(theme) : RL.Ink.Muted.Brush(theme) }
                    : new Border();
            mark.VerticalAlignment = VerticalAlignment.Center;
            // The name is what makes the mark an identification, so it is never what gets truncated
            var name = Ui.Text(all ? "All" : choice, RL.Typography.UI, 12, (active ? RL.Ink.Primary : RL.Ink.Muted).Brush(theme), FontWeights.Medium);
            name.TextTrimming = TextTrimming.None;
            var chip = new Border
            {
                Child = Ui.HStack(RL.Space.Sm, mark, name), Height = 26, Padding = new Thickness(RL.Space.Lg, 0, RL.Space.Lg, 0),
                CornerRadius = new CornerRadius(RL.Radius.Chip), BorderThickness = new Thickness(1),
                Background = active ? accent.Brush(theme, 0.16) : RL.Surface.Sunken.Brush(theme),
                BorderBrush = active ? accent.Brush(theme, 0.5) : Brushes.Transparent,
            };
            var button = new Button
            {
                Content = chip, Cursor = System.Windows.Input.Cursors.Hand,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                ToolTip = all ? "Show every provider together" : $"Show only {choice}",
                Margin = new Thickness(row.Children.Count > 0 ? RL.Space.Xs : 0, 0, 0, 0),
            };
            AutomationProperties.SetName(button, all ? "All providers" : choice);
            AutomationProperties.SetItemStatus(button, active ? "selected" : "");
            var target = choice;
            button.Click += (_, _) => onFocus(target);
            row.Children.Add(button);
        }
        return row;
    }

    // MARK: - Overview

    FrameworkElement Overview()
    {
        var stack = new StackPanel();
        var warnings = data.Warnings;
        if (warnings.Count > 0) Ui.Add(stack, OverviewWarnings.Build(warnings, theme, onFocus), 0);
        Ui.Add(stack, SummaryCard(), RL.Space.Lg);
        var grid = new ProviderCardGrid();
        foreach (var card in data.ProviderCards)
        {
            var provider = card.Provider;
            grid.Children.Add(ProviderCardView.Build(card, data.YellowPct, data.RedPct, $"last {data.RangeLabel}",
                data.ScannedAt, false, theme, () => onFocus(provider)));
        }
        Ui.Add(stack, grid, RL.Space.Lg);
        return stack;
    }

    /// <summary>The one number that answers "how close am I", with its window named. Stacks when narrow.</summary>
    FrameworkElement SummaryCard()
    {
        var worst = data.VisibleLimits.MaxBy(w => w.Utilization);
        var stale = worst is not null && data.StaleProviders.Contains(worst.Provider);
        var wide = new Grid();
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var divider = Ui.VDivider(84);
        divider.Margin = new Thickness(RL.Space.Xxl, 0, RL.Space.Xxl, 0);
        var totals = RangeTotals();
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(totals, 2);
        wide.Children.Add(NearestLimit(worst, stale));
        wide.Children.Add(divider);
        wide.Children.Add(totals);
        var narrow = Ui.VStack(RL.Space.Lg, NearestLimit(worst, stale), Ui.Divider(), RangeTotals());
        return new RLCard { Child = new ThatFits(wide, narrow) };
    }

    FrameworkElement NearestLimit(LimitWindow? worst, bool stale)
    {
        var stack = new StackPanel();
        Ui.Add(stack, new RLSectionHeader("Nearest limit", worst is null ? "across every provider" : $"{worst.Provider} · {worst.DisplayName}"), 0);
        if (worst is not null)
        {
            var status = RLStatus.ForUtilization(worst.Utilization, data.YellowPct, data.RedPct, stale);
            var big = Ui.Text(Ui.Pct(worst.Utilization), RL.Typography.Mono, 34, status.Color().Brush(theme), FontWeights.SemiBold);
            Ui.Add(stack, Ui.HStack(RL.Space.Md, big, new RLStatusIndicator(status, 13, true) { Margin = new Thickness(0, 6, 0, 0) }), RL.Space.Md);
            Ui.Add(stack, new RLUsageRail(worst.Utilization, status, 8, elapsed: stale ? null : data.PaceFor(worst)?.ElapsedFraction), RL.Space.Md);
            Ui.Add(stack, Ui.Text(worst.ResetsAt is { } r ? $"Resets {Ui.DateTime(r)}" : "No reset time reported", RL.Typography.MonoSmall, RL.Ink.Muted), RL.Space.Md);
        }
        else
        {
            Ui.Add(stack, new RLStateBlock(RLStateKind.Unavailable, "No rate limit is being reported", "Cost and token counts below are unaffected"), RL.Space.Md);
        }
        return stack;
    }

    FrameworkElement RangeTotals()
    {
        var r = data.RangedSlice;
        return Columns(RL.Space.Xxl,
            new RLMetricTile { Label = $"Tokens · {data.RangeLabel}", Value = Usage.FmtTokens(r.Io), Note = "in + out" },
            new RLMetricTile
            {
                Label = "Estimated cost", Value = Usage.FmtCost(r.Cost) + (r.HasUnpriced ? "+" : ""),
                Note = r.HasUnpriced ? "some models unpriced" : "configured pricing",
                Tint = RL.Brandmark.Money.Brush(theme),
                Help = "Estimated from your pricing table over measured counts. Never a bill.",
            });
    }

    // MARK: - Provider detail

    /// <summary>One provider in full: spend, what is left, the reset, and where the figures came from.</summary>
    FrameworkElement? ProviderDetail()
    {
        var card = data.ProviderCards.FirstOrDefault(c => string.Equals(c.Provider, data.Focus, StringComparison.OrdinalIgnoreCase));
        if (card is null) return null;
        var status = card.Status(data.YellowPct, data.RedPct);
        var accent = (card.Identity?.Token() ?? RL.Accent.Neutral).Brush(theme);
        var stack = new StackPanel();
        Ui.Add(stack, Ui.Row(
            [new ProviderBadge(card.Provider, 17), new RLStatusIndicator(status, 14, true)],
            [new RLInlineButton("All providers", "", "Back to the overview", () => onFocus(Config.AutoProvider))],
            RL.Space.Lg), 0);
        if (card.Identity?.Blurb is { } blurb) Ui.Add(stack, Ui.Text(blurb, RL.Typography.Body, RL.Ink.Secondary, wrap: true), RL.Space.Lg);
        Ui.Add(stack, Ui.Divider(), RL.Space.Lg);
        Ui.Add(stack, DetailMetrics(card), RL.Space.Lg);
        if (card.WorstWindow is { } window)
            Ui.Add(stack, new RLUsageRail(window.Utilization, status, 8, elapsed: card.Pace?.ElapsedFraction), RL.Space.Lg);
        if (card.LimitNote is { } note)
            Ui.Add(stack, new RLStateBlock(RLStateKind.Unavailable, note,
                card.Identity?.IsLocal == true ? "Token counts and cost are still recorded" : null), RL.Space.Lg);
        Ui.Add(stack, ProvenanceRow(card), RL.Space.Lg);
        return new RLCard { Accent = accent, Child = stack };
    }

    FrameworkElement DetailMetrics(ProviderCard card)
    {
        var ranged = data.RangedSlice;
        var today = data.TodaySlice;
        var w = card.WorstWindow;
        return Columns(RL.Space.Xxl,
            new RLMetricTile
            {
                Label = "This period", Value = w is null ? Usage.FmtTokens(today.Io) : Ui.Pct(w.Utilization),
                Note = w?.DisplayName ?? "tokens today",
                Help = w is null ? "This provider reports no limit window, so the figure is volume" : "Share of the window that has been consumed",
            },
            new RLMetricTile
            {
                Label = "Remaining", Value = card.RemainingPercent is { } rem ? Ui.Pct(rem) : "not reported",
                Note = card.RemainingPercent is null ? "no limit to have capacity in" : "of this window",
                Help = card.RemainingPercent is null ? "Nothing is inferred here: with no limit reported there is no capacity figure to give" : null,
            },
            new RLMetricTile
            {
                Label = "Resets", Value = w?.ResetsAt is { } r ? Ui.Time(r) : "not reported",
                Note = w?.ResetsAt is { } r2 ? Ui.Relative(r2) : null,
            },
            new RLMetricTile
            {
                Label = $"Tokens · {data.RangeLabel}", Value = Usage.FmtTokens(ranged.Io),
                Note = Usage.FmtCost(ranged.Cost) + (ranged.HasUnpriced ? "+" : "") + " est",
                Help = ranged.HasUnpriced ? "Some models here have no pricing entry, so they are counted in tokens only and the total carries a plus" : null,
            });
    }

    /// <summary>Where the figures came from and how fresh they are, so a reading can be argued with.</summary>
    FrameworkElement ProvenanceRow(ProviderCard card)
    {
        var leading = new List<UIElement?>();
        if (card.WorstWindow is { } window) leading.Add(new RLPill(window.Source.Label(), RL.Ink.Muted.Brush(theme), window.Source.Note()));
        if (card.Identity?.IsLocal == true)
            leading.Add(new RLPill("local", (card.Identity.Token()).Brush(theme), "Probed directly on this PC; nothing leaves the machine"));
        if (card.AsOf is { } asOf)
            leading.Add(Ui.Text($"windows as of {Ui.TimeWithSeconds(asOf)}", RL.Typography.MonoSmall, card.IsStale ? RL.State.Warning : RL.Ink.Muted));
        var trailing = new List<UIElement?>();
        if (data.ScannedAt is { } at) trailing.Add(Ui.Text($"transcripts read {Ui.TimeWithSeconds(at)}", RL.Typography.MonoSmall, RL.Ink.Muted));
        return Ui.Row(leading, trailing, RL.Space.Md);
    }

    // MARK: - Tiles

    FrameworkElement Tiles()
    {
        // Labels name the track so a focused figure cannot be read as the global one
        var scope = data.FocusingAll ? "" : $" · {data.Focus}";
        var today = data.TodaySlice;
        var block = data.Block5hSlice;
        var ranged = data.RangedSlice;
        var peak = data.VisibleTrends.Select(t => t.Peak).OfType<UsagePoint>().Select(p => (int?)p.Io).Max();
        static string Est(DashboardData.Slice s) => $"{Usage.FmtCost(s.Cost)}{(s.HasUnpriced ? "+" : "")} est";
        return new RLCard
        {
            Child = Columns(RL.Space.Lg,
                new RLMetricTile { Label = $"Today{scope}", Value = Usage.FmtTokens(today.Io), Note = Est(today) },
                new RLMetricTile { Label = $"Last 5 hours{scope}", Value = Usage.FmtTokens(block.Io), Note = Est(block) },
                new RLMetricTile { Label = $"Last {data.RangeLabel}{scope}", Value = Usage.FmtTokens(ranged.Io), Note = Est(ranged) },
                new RLMetricTile
                {
                    Label = "Cache read", Value = Usage.FmtTokens(ranged.CacheRead), Note = data.RangeLabel,
                    Help = "Cache reads are billed at a fraction of the input rate, so they are counted separately from in+out",
                },
                new RLMetricTile { Label = "Busiest day", Value = peak is { } p ? Usage.FmtTokens(p) : "no data", Note = "in range" }),
        };
    }

    // MARK: - Limits

    /// <summary>The rails, or a way to get them: an empty panel with only an error was a dead end.</summary>
    FrameworkElement? LimitsPanel()
    {
        var limits = data.VisibleLimits;
        if (limits.Count == 0 && data.LimitsNote is null) return null;
        var stack = new StackPanel();
        foreach (var window in limits)
            Ui.Add(stack, LimitRailRow(window, data.PaceFor(window), window.Provider == "Claude" ? data.ClaudeLimitsAsOf : null), RL.Space.Lg);
        if (data.LimitsNote is { } note) Ui.Add(stack, new RLStateBlock(RLStateKind.Unavailable, note), RL.Space.Lg);
        // No Claude rails and no feed installed: offer the fix right here
        if (data.Matches("Claude") && !limits.Any(w => w.Provider == "Claude") && data.Availability.Has("Claude") && !FeedInstalled())
            Ui.Add(stack, new RLInlineButton("Set Up Claude Tracking…", "",
                "Reads the windows Claude Code hands its statusline. No sign-in, no Credential Manager, no network.",
                () => model.OnSetupClaudeTracking?.Invoke()) { HorizontalAlignment = HorizontalAlignment.Left }, RL.Space.Lg);
        return Panel("Limits", "the red line marks the limit", null, null, stack);
    }

    static bool FeedInstalled()
    {
        try { return StatuslineInstaller.IsInstalled(); }
        catch (Exception) { return false; }
    }

    FrameworkElement LimitRailRow(LimitWindow window, Pace? pace, DateTimeOffset? asOf)
    {
        // Stale rails drain and carry their timestamp in amber, so an old reading never impersonates a current one
        var stale = asOf is { } a && (DateTimeOffset.UtcNow - a).TotalSeconds > ProviderOverview.StalenessThreshold;
        var status = RLStatus.ForUtilization(window.Utilization, data.YellowPct, data.RedPct, stale);
        var trailing = new List<UIElement?>();
        if (stale && asOf is { } when)
        {
            var t = Ui.Text($"as of {Ui.Time(when)}", RL.Typography.MonoSmall, RL.State.Warning);
            t.ToolTip = "The last reading RedLine has. Claude Code only feeds the usage feed while it runs.";
            trailing.Add(t);
        }
        trailing.Add(Ui.Text($"{Ui.Round(window.Utilization)}% used", RL.Typography.MonoBody.Family, 13, status.Color().Brush(theme)));
        var head = Ui.Row(
            [new ProviderTile(window.Provider, 19), Ui.Text($"{window.Provider} · {window.DisplayName}", RL.Typography.Subheading, RL.Ink.Primary),
             new RLStatusIndicator(status, 11)], trailing, RL.Space.Md);
        var foot = new List<UIElement?>();
        if (window.ResetsAt is { } r) foot.Add(Ui.Text($"Resets {Ui.DateTime(r)}", RL.Typography.MonoSmall, RL.Ink.Muted));
        if (!stale && pace?.Summary() is { } summary)
        {
            var s = Ui.Text("· " + summary, RL.Typography.MonoSmall, pace.HitsLimitBeforeReset ? RL.State.Warning : RL.Ink.Muted);
            s.ToolTip = pace.BasisNote;
            foot.Add(s);
        }
        // Where a percentage came from, in a word: three sources can produce the same number
        var row = Ui.VStack(RL.Space.Sm, head,
            new RLUsageRail(window.Utilization, status, 8, elapsed: stale ? null : pace?.ElapsedFraction),
            Ui.Row(foot, [new RLPill(window.Source.Label(), RL.Ink.Muted.Brush(theme), window.Source.Note())], RL.Space.Md));
        AutomationProperties.SetName(row, $"{window.Provider} {window.DisplayName}, {Ui.Round(window.Utilization)} percent used, {status.Phrase}");
        return row;
    }

    // MARK: - Service status

    FrameworkElement? ServicePanel()
    {
        var services = data.VisibleServices;
        if (services.Count == 0) return null;
        var stack = new StackPanel();
        foreach (var s in services) Ui.Add(stack, ServiceStatusRow(s), RL.Space.Lg);
        if (data.ServicesCheckedAt is { } at)
            Ui.Add(stack, Ui.Text("last checked " + Ui.Time(at), RL.Typography.MonoSmall, RL.Ink.Muted), RL.Space.Lg);
        return Panel("Service status", "as reported by each operator", null,
            new RLInlineButton("Check now", "", "Re-check every status immediately", () => model.OnStatusRefresh?.Invoke()), stack);
    }

    /// <summary>One provider's health as its operator reports it: a glyph, with the check time on hover.</summary>
    FrameworkElement ServiceStatusRow(Snapshot.Service s)
    {
        var status = RLStatus.ForTone(ServiceGlyph.ToneFor(s.Indicator), s.Phrase);
        var when = data.ServicesCheckedAt is { } at ? "last checked " + Ui.Time(at) : "not checked yet";
        var row = Ui.Row(
            [new RLStatusIndicator(status, 14), new ProviderTile(s.Provider, 18),
             Ui.Text(s.Provider, RL.Typography.Subheading, RL.Ink.Primary), Ui.Text(s.Phrase, RL.Typography.Caption, RL.Ink.Secondary)],
            [Ui.Text(s.Description, RL.Typography.MonoSmall, RL.Ink.Muted)], RL.Space.Lg);
        row.ToolTip = $"{s.Phrase} · {when}";
        row.Background = Brushes.Transparent;
        AutomationProperties.SetName(row, $"{s.Provider}: {s.Phrase}");
        return row;
    }

    // MARK: - Findings

    /// <summary>Shown when Claude is in view. "Nothing found" is an answer, so an empty report keeps its panel.</summary>
    FrameworkElement? FindingsPanel()
    {
        if (!data.Matches("Claude") || data.Findings is not { } report) return null;
        var stack = new StackPanel();
        if (report.IsEmpty)
        {
            Ui.Add(stack, new RLStateBlock(RLStateKind.Empty, report.Hidden > 0
                ? $"Nothing left in this window; {report.Hidden} marked as read."
                : "Nothing worth changing in this window.") { Margin = new Thickness(0, 0, 0, RL.Space.Lg) }, 0);
        }
        else
        {
            for (int i = 0; i < report.Findings.Count; i++)
            {
                var finding = report.Findings[i];
                if (i > 0) Ui.Add(stack, Ui.Divider(new Thickness(0, RL.Space.Lg, 0, RL.Space.Lg)), 0);
                Ui.Add(stack, FindingRow.Build(finding, theme, () => model.OnDismissFinding?.Invoke(finding.Id)), 0);
            }
            Ui.Add(stack, Ui.Divider(new Thickness(0, RL.Space.Lg, 0, RL.Space.Lg)), 0);
        }
        Ui.Add(stack, FindingsFooter(report), 0);
        return Panel("Findings", $"{report.SessionsScanned} sessions · {report.WindowDays} days", null, null, stack);
    }

    /// <summary>The caveat that applies to every row, said once, beside the control that regenerates them.</summary>
    FrameworkElement FindingsFooter(FindingsReport report)
    {
        var trailing = new List<UIElement?>();
        // Said wherever findings are counted, so a hidden one is never mistaken for one that stopped being true
        if (report.Hidden > 0)
            trailing.Add(new RLInlineButton($"{report.Hidden} read", null, "Marked as read and hidden. Click to show them again now.",
                () => model.OnRestoreFindings?.Invoke()));
        if (data.FindingsScanning)
            trailing.Add(Ui.HStack(RL.Space.Sm, new Spinner { Size = 11, VerticalAlignment = VerticalAlignment.Center },
                Ui.Text("Scanning", RL.Typography.MonoSmall, RL.Ink.Muted)));
        else
            trailing.Add(new RLInlineButton("Scan again", "", "Reads the transcripts again now, rather than waiting for the background pass",
                () => model.OnRescanFindings?.Invoke()));
        var generated = Ui.Text(Ui.TimeWithSeconds(report.GeneratedAt), RL.Typography.MonoSmall, RL.Ink.Muted);
        generated.ToolTip = "when this report was generated";
        trailing.Add(generated);
        var caveat = Ui.Text("Estimates over measured counts, never a bill. A finding with nothing honest to put on it carries no figure.",
            RL.Typography.Caption, RL.Ink.Muted, new Thickness(0, 0, RL.Space.Xl, 0), wrap: true);
        return Ui.Row([caveat], trailing, RL.Space.Lg);
    }

    // MARK: - Ollama

    FrameworkElement OllamaPanel()
    {
        var s = model.Ollama;
        var running = s.Running.Select(m => m.Name).ToHashSet();
        var stack = new StackPanel();
        FrameworkElement status = s.Reachable
            ? Ui.Row([Ui.Dot(7, RL.State.Success.Brush(theme)), Ui.Text($"Running{(s.Version is { } v ? $" · v{v}" : "")}", RL.Typography.UI, 14, RL.Ink.Primary.Brush(theme))],
                     [Ui.Text($"{s.Running.Count} loaded · {s.Models.Count} downloaded", RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme))], RL.Space.Md)
            // Brand voice: state the fact, do not scold
            : Ui.Row([Ui.Dot(7, RL.Ink.Muted.Brush(theme)), Ui.Text(s.Error ?? "Ollama is not running", RL.Typography.UI, 14, RL.Ink.Primary.Brush(theme))],
                     [Ui.Text("start it with: ollama serve", RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme))], RL.Space.Md);
        Ui.Add(stack, Panel("Ollama", model.OllamaHost, OllamaStore.Provider, null, status), 0);

        if (s.Running.Count > 0)
        {
            var loaded = new StackPanel();
            foreach (var m in s.Running)
            {
                var name = m.Name;
                var words = Ui.VStack(2, Ui.Text(m.Name, RL.Typography.Mono, 14, RL.Ink.Primary.Brush(theme)),
                    Ui.Text(VramNote(m), RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme)));
                Ui.Add(loaded, Ui.Row([words], [Ui.PushButton("Stop", "Unloads from memory. The download is kept.", !s.Busy.Contains(name),
                    () => model.OnOllamaStop?.Invoke(name))], RL.Space.Md), 10);
            }
            Ui.Add(stack, Panel("Loaded now", "in memory", null, null, loaded), 14);
        }

        FrameworkElement downloaded;
        if (s.Models.Count == 0)
        {
            downloaded = Ui.Text(s.Reachable ? "No models downloaded" : "Unavailable while Ollama is stopped", RL.Typography.Mono, 14, RL.Ink.Muted.Brush(theme));
        }
        else
        {
            var list = new StackPanel();
            foreach (var m in s.Models)
            {
                var name = m.Name;
                var isRunning = running.Contains(name);
                var label = Ui.Text(m.Name, RL.Typography.Mono, 14, RL.Ink.Primary.Brush(theme));
                label.Width = 230;
                var detail = string.Join(" · ", new[] { m.ParameterSize, m.Quantization, Ollama.FmtBytes(m.SizeBytes) }.Where(x => x is not null));
                var button = isRunning
                    ? Ui.PushButton("Stop", null, !s.Busy.Contains(name), () => model.OnOllamaStop?.Invoke(name))
                    : Ui.PushButton("Start", "Loads the model into memory, ready to answer", s.Reachable && !s.Busy.Contains(name),
                        () => model.OnOllamaStart?.Invoke(name));
                Ui.Add(list, Ui.Row(
                    [Ui.Dot(6, isRunning ? RL.State.Success.Brush(theme) : RL.Ink.Muted.Brush(theme, 0.4)), label,
                     Ui.Text(detail, RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme))],
                    [button], 10), RL.Space.Md);
            }
            downloaded = list;
        }
        Ui.Add(stack, Panel("Downloaded", $"{s.Models.Count} models", null, null, downloaded), 14);
        return stack;
    }

    static string VramNote(OllamaRunningModel m)
    {
        var share = Ui.Round(m.VramShare * 100);
        var expiry = m.ExpiresAt is { } e ? $" · unloads {Ui.Time(e)}" : "";
        return $"{Ollama.FmtBytes(m.SizeBytes)} · {share}% on GPU{expiry}";
    }

    // MARK: - Charts

    FrameworkElement ChartsPanels()
    {
        var stack = new StackPanel();
        var trends = data.VisibleTrends;
        var stride = DailyAxis.StrideDays(data.Range);
        double Io(UsagePoint p) => p.Io;
        string Tokens(double v) => Usage.FmtTokens((long)v);
        Ui.Add(stack, Panel("Tokens per day", data.RangeLabel, null, null, trends.Count == 0 ? Empty()
            : new DashboardChart(trends, new ChartSpec(ChartKind.StackedBars, Io, Tokens, Ui.MonthDay, Ui.Day, stride, 215), theme,
                DashboardChart.Summary($"Tokens per day over {data.Range} days", trends, Io, Tokens))), 0);
        Ui.Add(stack, Panel("Estimated cost per day", $"{data.RangeLabel} · configured pricing", null, null, trends.Count == 0 ? Empty()
            : new DashboardChart(trends, new ChartSpec(ChartKind.Lines, p => p.Cost, Usage.FmtCost, Ui.MonthDay, Ui.Day, stride, 175), theme,
                DashboardChart.Summary($"Estimated cost per day over {data.Range} days", trends, p => p.Cost, Usage.FmtCost))), RL.Space.Lg);
        var hourly = data.VisibleHourly;
        Ui.Add(stack, Panel("Last 24 hours", HourlyNote(), null, null, hourly.Count == 0 ? Empty()
            : new DashboardChart(hourly, new ChartSpec(ChartKind.StackedBars, Io, Tokens, Ui.Hour, Ui.Time, 4, 155, 3), theme,
                DashboardChart.Summary("Tokens per hour over the last 24 hours", hourly, Io, Tokens))), RL.Space.Lg);
        var models = data.VisibleModels;
        Ui.Add(stack, Panel("Models", $"last {data.RangeLabel}", null, null, models.Count == 0 ? Empty() : ModelMix(models)), RL.Space.Lg);
        return stack;
    }

    /// <summary>The rolling day's own total, so the shape can be read against a figure.</summary>
    string HourlyNote()
    {
        var d = data.Day24Slice;
        if (d.Io <= 0) return "no usage";
        return $"{Usage.FmtTokens(d.Io)} · {Usage.FmtCost(d.Cost)}{(d.HasUnpriced ? "+" : "")} est";
    }

    FrameworkElement ModelMix(IReadOnlyList<ModelShare> models)
    {
        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(205), new GridLength(1, GridUnitType.Star), new GridLength(68), new GridLength(96) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var maxIO = Math.Max(models.FirstOrDefault()?.Io ?? 1, 1);
        var row = 0;
        void Cell(UIElement e, int col, int span = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, span);
            grid.Children.Add(e);
        }
        void NextRow(double gap)
        {
            if (grid.RowDefinitions.Count > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row = grid.RowDefinitions.Count - 1;
        }
        foreach (var m in models.Take(8))
        {
            NextRow(8);
            Cell(new TrackBadge(m.Provider, 18) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, 0);
            Cell(Ui.Text(OllamaLocality.Marked(m.Model), RL.Typography.Mono, 14, RL.Ink.Primary.Brush(theme)), 1);
            Cell(new ShareBar((double)m.Io / maxIO, ChartPalette.Color(m.Provider, theme)) { Margin = new Thickness(8, 0, 8, 0) }, 2);
            Cell(Right(Ui.Text(Usage.FmtTokens(m.Io), RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme))), 3);
            // An unpriced model reads "n/a", never a zero that reads as free
            Cell(Right(Ui.Text(m.Priced ? Usage.FmtCost(m.Cost) : "n/a", RL.Typography.Mono, 13,
                (m.Priced ? RL.Brandmark.Money : RL.Ink.Muted).Brush(theme))), 4);
        }
        // The list answers "which model"; this row answers "how much altogether"
        NextRow(8);
        Cell(new Border { Height = 1, Background = RL.Ink.Muted.Brush(theme, 0.25) }, 0, 5);
        NextRow(8);
        Cell(Ui.Text("Total", RL.Typography.Mono, 14, RL.Ink.Primary.Brush(theme), FontWeights.SemiBold), 0, 2);
        Cell(Right(Ui.Text(Usage.FmtTokens(models.Sum(m => m.Io)), RL.Typography.Mono, 13, RL.Ink.Primary.Brush(theme), FontWeights.SemiBold)), 3);
        Cell(Right(Ui.Text(Usage.FmtCost(models.Where(m => m.Priced).Sum(m => m.Cost)) + (models.Any(m => !m.Priced) ? "+" : ""),
            RL.Typography.Mono, 13, RL.Brandmark.Money.Brush(theme), FontWeights.SemiBold)), 4);
        if (models.Any(m => !m.Priced))
        {
            NextRow(8);
            Cell(Ui.Text("n/a means no pricing entry, so it is counted in tokens only", RL.Typography.Mono, 13, RL.Ink.Muted.Brush(theme)), 0, 5);
        }
        return grid;
    }

    static TextBlock Right(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Right;
        t.TextAlignment = TextAlignment.Right;
        return t;
    }

    // MARK: - History

    /// <summary>What has been recorded, as opposed to what can still be read; the two diverge on pruning.</summary>
    FrameworkElement? HistoryPanel()
    {
        if (data.History is not { Days: > 0 } history) return null;
        var stack = new StackPanel();
        if (data.Cadence is { } cadence) Ui.Add(stack, CadencePanel(cadence), 0);
        var tiles = Ui.HStack(RL.Space.Xxl,
            new RLMetricTile { Label = "Days", Value = $"{history.Days}", Note = string.Join(" to ", new[] { history.Earliest, history.Latest }.OfType<string>()) },
            new RLMetricTile { Label = "Tokens", Value = Usage.FmtTokens(history.Tokens) },
            new RLMetricTile
            {
                Label = "Estimated cost", Value = Usage.FmtCost(history.Cost) + (history.Complete ? "" : "+"),
                Note = history.Complete ? null : "some models have no price", Tint = RL.Brandmark.Money.Brush(theme),
            });
        var body = Ui.VStack(RL.Space.Md, tiles,
            Ui.Text($"Recorded as RedLine polls, so it survives Claude Code's own transcript cleanup. {Ui.Bytes(history.SizeBytes)} on disk.",
                RL.Typography.Caption, RL.Ink.Muted, wrap: true));
        Ui.Add(stack, Panel("Recorded history", "kept locally, UTC days", null, null, body), RL.Space.Lg);
        return stack;
    }

    /// <summary>The shape of the day, counted from timestamps. None of it is a claim about the person.</summary>
    FrameworkElement CadencePanel(DashboardData.CadenceSummary cadence)
    {
        string? busiest = null;
        if (cadence.Hours.Count > 0)
        {
            var top = cadence.Hours.Select((v, i) => (v, i)).MaxBy(t => t.v);
            if (top.v > 0) busiest = $"{top.i:00}:00";
        }
        // Names the scope first, because every other panel narrows to the focused track and this does not
        var note = string.Join(" · ", new[] { data.FocusingAll ? null : "all providers", busiest is null ? null : $"busiest at {busiest}" }.OfType<string>());
        var tiles = Ui.HStack(22,
            new RLMetricTile
            {
                Label = "Current run", Value = cadence.CurrentStretch is { } c ? Pace.Short(c) : "none",
                Note = cadence.CurrentStretch is null ? "no activity just now" : null,
            },
            new RLMetricTile { Label = "Longest run", Value = cadence.LongestStretch is { } l ? Pace.Short(l) : "none", Note = "in this range" },
            new RLMetricTile { Label = "Days running", Value = $"{cadence.Streak}", Note = $"{cadence.Days} active in range" });
        var labels = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var quarters = new[] { 0, 6, 12, 18 };
        for (int i = 0; i < quarters.Length; i++)
        {
            if (i > 0) labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Ui.Text($"{quarters[i]:00}", RL.Typography.Mono, 10, RL.Ink.Muted.Brush(theme));
            Grid.SetColumn(t, labels.ColumnDefinitions.Count - 1);
            labels.Children.Add(t);
        }
        var body = Ui.VStack(12, tiles, Ui.VStack(0, new CadenceHours(cadence.Hours, theme), labels),
            data.Cues.LastOrDefault() is { } latest ? Ui.Text($"{latest.Title}. {latest.Body}", RL.Typography.Caption, RL.Ink.Muted, wrap: true) : null);
        return Panel("Cadence", note.Length == 0 ? null : note, null, null, body);
    }
}

/// <summary>A model's share of the largest model, as a capsule. Always a sliver for a real share.</summary>
internal sealed class ShareBar : FrameworkElement
{
    readonly double fraction;
    readonly Brush fill;

    public ShareBar(double fraction, Color color)
    {
        this.fraction = Math.Clamp(fraction, 0, 1);
        fill = Themed.Solid(color);
        Height = 8;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override Size MeasureOverride(Size a) => new(0, 8);

    protected override void OnRender(DrawingContext dc) =>
        Themed.DrawCapsule(dc, fill, new Rect(0, 0, Math.Max(2, RenderSize.Width * fraction), 8));
}
