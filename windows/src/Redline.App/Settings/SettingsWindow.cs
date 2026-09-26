// The settings window: everything that changes how RedLine behaves, in named sections rather than
// one long menu. Port of SettingsView in SettingsWindow.swift; config.json stays the source of truth.
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Settings;

public sealed class SettingsWindow : Window
{
    public SettingsModel Model { get; }

    readonly StackPanel sidebar = new();
    readonly StackPanel detail = new();
    readonly ScrollViewer scroller;
    readonly Grid root = new();
    // Each control registers how it settles on the model, so a write repaints in place and a
    // slider being dragged keeps its capture instead of being rebuilt under the pointer
    readonly List<Action> syncs = [];
    bool syncing;

    public SettingsWindow(SettingsModel model)
    {
        Model = model;
        Title = "RedLine Settings";
        Width = 780;
        Height = 620;
        MinWidth = 720;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/RedLine;component/Assets/RedLine.ico")); } catch { }
        this.Bind(BackgroundProperty, RL.Surface.Ground);
        SettingsUI.UseStyles(root);

        // Two explicit columns rather than a collapsible split view: six fixed sections gain
        // nothing from hiding the sidebar
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Wide enough for "Refresh and Monitoring", which truncated at 186 on macOS
        var side = new Border { Padding = new Thickness(RL.Space.Md), Child = sidebar };
        side.Bind(Border.BackgroundProperty, RL.Surface.Raised);
        var rule = new Border();
        rule.Bind(Border.BackgroundProperty, RL.Stroke.Hairline);
        Grid.SetColumn(rule, 1);
        detail.Margin = new Thickness(RL.Space.Xxl);
        scroller = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetColumn(scroller, 2);
        root.Children.Add(side);
        root.Children.Add(rule);
        root.Children.Add(scroller);
        Content = root;

        BuildSidebar();
        BuildDetail();
        model.PropertyChanged += OnModelChanged;
        Closed += (_, _) => model.PropertyChanged -= OnModelChanged;
        ThemeManager.ApplyTitleBar(this);
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnModelChanged(sender, e)); return; }
        if (e.PropertyName == nameof(SettingsModel.Section)) { SyncSidebar(); BuildDetail(); }
        else Resync();
    }

    void Resync()
    {
        syncing = true;
        try { foreach (var s in syncs) s(); }
        finally { syncing = false; }
    }

    /// <summary>Registers a control's settle step and runs it once now.</summary>
    void Sync(Action a)
    {
        syncs.Add(a);
        var was = syncing;
        syncing = true;
        try { a(); } finally { syncing = was; }
    }

    SettingsModel M => Model;
    Config C => Model.Config;
    SettingsEnvironmentState S => Model.State;

    // MARK: sidebar

    void BuildSidebar()
    {
        foreach (var section in SettingsSectionInfo.All)
        {
            var glyph = new TextBlock
            {
                Text = section.Glyph(), FontFamily = RL.Typography.Icons, FontSize = 13, Width = 16,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            };
            glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) });
            var title = new TextBlock { Text = section.Title(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Md, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            RL.Typography.Body.Apply(title);
            title.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(glyph);
            row.Children.Add(title);
            var button = new ToggleButton { Content = row, Tag = section, ToolTip = section.Hint(), Margin = new Thickness(0, 0, 0, RL.Space.Xxs) };
            button.SetResourceReference(StyleProperty, "RL.Settings.SidebarRow");
            AutomationProperties.SetName(button, section.Title());
            AutomationProperties.SetHelpText(button, section.Hint());
            // Checked rather than Click, so keyboard, mouse and UI Automation all select the same way
            button.Checked += (_, _) => { M.Section = section; SyncSidebar(); };
            button.Unchecked += (_, _) => { if (M.Section == section) button.IsChecked = true; };
            sidebar.Children.Add(button);
        }
        SyncSidebar();
    }

    void SyncSidebar()
    {
        foreach (var b in sidebar.Children.OfType<ToggleButton>()) b.IsChecked = Equals(b.Tag, M.Section);
    }

    // MARK: detail

    void BuildDetail()
    {
        syncs.Clear();
        detail.Children.Clear();
        var heading = SettingsUI.Text(M.Section.Title(), RL.Typography.Heading, RL.Ink.Primary);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        detail.Children.Add(heading);
        var groups = M.Section switch
        {
            SettingsSection.Providers => Providers(),
            SettingsSection.Monitoring => Monitoring(),
            SettingsSection.Limits => Limits(),
            SettingsSection.Appearance => Appearance(),
            SettingsSection.Data => Data(),
            _ => About(),
        };
        foreach (var g in groups) SettingsUI.AddSpaced(detail, g, RL.Space.Xl + RL.Space.Xs);
        scroller.ScrollToTop();
    }

    SettingRow Row(string title, string explanation, Func<Config, bool> get, Action<bool> set)
    {
        var row = new SettingRow(title, explanation);
        row.Toggled += v => { if (!syncing) set(v); Resync(); };
        Sync(() => row.Set(get(C)));
        return row;
    }

    /// <summary>A toggle over a preference whose only effect is the stored value.</summary>
    SettingRow PlainRow(string title, string explanation, string key, Func<Config, bool> get) =>
        Row(title, explanation, get, v => M.Write(key, v));

    /// <summary>A toggle over a preference the app owns, handed to the app so its side effect runs once.</summary>
    SettingRow AppRow(string title, string explanation, Func<Config, bool> get, Func<SettingsActions, Action<bool>> action) =>
        Row(title, explanation, get, v => M.Apply(get, action(M.Actions), v));

    ComboBox Picker(IEnumerable<(string, string)> options, Func<Config, string> get, Action<string> set, double width = 220)
    {
        var box = SettingsUI.Picker(options, width, v => { if (!syncing) set(v); Resync(); }, out var select);
        Sync(() => select(get(C)));
        return box;
    }

    Stepper StepperFor(double min, double max, double step, Func<double, string> format, Func<Config, double> get, string key, bool integer)
    {
        var s = new Stepper(min, max, step, format);
        s.Changed += v => M.Write(key, integer ? JsonValue.Create((int)v) : JsonValue.Create(v));
        Sync(() => s.Set(get(C)));
        return s;
    }

    // MARK: Providers

    IEnumerable<UIElement> Providers()
    {
        var rows = new List<UIElement>();
        foreach (var provider in Config.KnownProviders)
        {
            if (rows.Count > 0) rows.Add(SettingsUI.Divider());
            rows.Add(ProviderRow(provider));
        }
        yield return SettingsUI.Group("What RedLine reads",
            "Everything is read from files already on this PC. At least one provider stays switched on.", [.. rows]);

        var feed = new SettingAction("", "", () => M.Actions.InstallClaudeFeed());
        Sync(() => feed.Set(
            S.ClaudeFeedInstalled ? "Reinstall the Usage Feed…" : "Set Up Claude Tracking…",
            S.ClaudeFeedInstalled
                ? "Installed. Reads the windows Claude Code hands its statusline; re-running updates the wrapper."
                : "The recommended source: the windows Claude Code hands its statusline. No sign-in, no Credential Manager, no network."));
        var sign = new SettingAction("", "", () => { if (S.SignedIn) M.Actions.SignOut(); else M.Actions.SignIn(); });
        Sync(() => sign.Set(
            S.SignedIn ? "Sign Out of Claude" : "Sign In with Browser…",
            S.SignedIn ? "Signed in. RedLine fetches live percentages with its own grant whenever the feed is quiet."
            : S.OAuthConfigured ? "Live between sessions too, and the route for claude.ai users without Claude Code."
            : "Needs oauth.clientId in the config before it can be used."));
        yield return SettingsUI.Group("Claude rate-limit percentages",
            "The same session and week percentages /usage shows. Everything else works with these off.",
            feed, SettingsUI.Divider(), sign, SettingsUI.Divider(),
            AppRow("Use the Claude Code CLI's token",
                "Reads Claude Code's token from its credentials file or Credential Manager, and only ever reads it, so it cannot sign the CLI out. The endpoint it is used against is undocumented.",
                c => c.UseCLIToken, a => a.SetCLIToken));

        var shim = new SettingAction("", "", () => M.Actions.InstallOllamaShim());
        var ollama = SettingsUI.Group("Ollama tracking",
            "Ollama keeps no usage history of its own, so anything that bypasses the shim is invisible by design.", shim);
        Sync(() =>
        {
            ollama.Visibility = S.Availability.Has(OllamaStore.Provider) ? Visibility.Visible : Visibility.Collapsed;
            shim.Set(S.OllamaShimInstalled ? "Reinstall the Ollama Shim…" : "Set Up Ollama Tracking…",
                "Installs a transparent ollama shim in %USERPROFILE%\\.local\\bin so plain `ollama run` calls are counted.");
        });
        yield return ollama;

        yield return SettingsUI.Group("Setup", null,
            new SettingAction("Open the Setup Window…",
                "The same first-run screen: which providers to read, and where Claude's percentages come from.",
                () => M.Actions.OpenSetup()));
    }

    UIElement ProviderRow(string provider)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var box = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, RL.Space.Lg, 0) };
        AutomationProperties.SetName(box, $"Read {provider}");
        box.Checked += (_, _) => { if (!syncing) M.SetProvider(provider, true); Resync(); };
        box.Unchecked += (_, _) => { if (!syncing) M.SetProvider(provider, false); Resync(); };
        var words = new StackPanel();
        Grid.SetColumn(words, 1);
        // The mark identifies the provider and the name states it; neither stands in for the other
        words.Children.Add(new ProviderBadge(provider, 14) { HorizontalAlignment = HorizontalAlignment.Left });
        words.Children.Add(SettingsUI.Caption(ProviderIdentity.Of(provider)?.Blurb ?? "", margin: new Thickness(0, RL.Space.Xs, 0, 0)));
        var note = SettingsUI.Caption("", margin: new Thickness(0, RL.Space.Xxs, 0, 0));
        words.Children.Add(note);
        grid.Children.Add(box);
        grid.Children.Add(words);
        Sync(() =>
        {
            var installed = S.Availability.Has(provider);
            var last = M.IsLastEnabled(provider);
            box.IsChecked = M.Reads(provider);
            box.IsEnabled = installed && !last;
            note.Visibility = !installed || last ? Visibility.Visible : Visibility.Collapsed;
            note.Text = !installed ? "Not found on this PC" : "The last provider stays on; switch another on first";
            note.Bind(TextBlock.ForegroundProperty, !installed ? RL.State.Warning : RL.Ink.Muted);
        });
        return grid;
    }

    // MARK: Refresh and monitoring

    static readonly (double Seconds, string Label)[] Intervals = [(60, "1m"), (300, "5m"), (600, "10m"), (1800, "30m")];

    IEnumerable<UIElement> Monitoring()
    {
        var seg = new RLSegmented(Intervals.Select(i => new RLSegment(i.Seconds, i.Label, $"Rescan every {i.Label}")), null, 52)
        { HorizontalAlignment = HorizontalAlignment.Left };
        seg.Selected += (_, v) => { if (v is double s) M.Write("pollIntervalSeconds", s); };
        var current = SettingsUI.Caption("");
        Sync(() =>
        {
            // The offered interval nearest the configured one, so a hand-edited value still
            // lights the closest button rather than none at all
            seg.Selection = Intervals.Select(i => i.Seconds).MinBy(s => Math.Abs(s - C.PollIntervalSeconds));
            current.Text = $"Currently every {(int)C.PollIntervalSeconds}s. A value outside these choices can be set in the config file.";
        });
        yield return SettingsUI.Group("Rescan interval",
            "How often transcripts are rescanned. Claude's windows also update the moment Claude Code writes them, without waiting for this.",
            seg, current);

        yield return SettingsUI.Group("Tray readout", null,
            SettingsUI.Labeled("Shows", Picker(
                [("limits", "Rate limits"), ("session", "Session only"), ("cost", "Cost today"), ("tokens", "Tokens today"), ("both", "Tokens and cost")],
                c => c.MenuBarDisplay, v => M.Write("menuBarDisplay", v))),
            SettingsUI.Labeled("Provider", Picker(
                new[] { (Config.AutoProvider, "Nearest limit (any provider)") }.Concat(Config.KnownProviders.Select(p => (p, p))),
                c => c.MenuBarProvider, v => M.Apply(c => c.MenuBarProvider, M.Actions.SetMenuBarProvider, v)),
                "Which provider the readout reports. Nearest limit shows whichever is closest to its cap, since that is the one that will interrupt you first."),
            SettingsUI.Labeled("Limit windows", Picker(
                [("all", "All limits"), ("session", "Session only"), ("week", "Week only")],
                c => c.LimitWindows, v => M.Apply(c => c.LimitWindows, M.Actions.SetLimitWindows, v))),
            SettingsUI.Labeled("Claude in the tray", Picker(
                [("stacked", "Stacked, one icon"), ("split", "Side by side, two icons")],
                c => c.TrayLayout, v => M.Write("trayLayout", v)),
                "A tray icon is a fixed square. Stacked puts the session over the week in one; side by side gives each its own icon and twice the height. Windows may park a new icon under the ^ arrow; drag it onto the taskbar once."),
            SettingsUI.Divider(),
            // No mark toggle on Windows: the icon is the percentage whenever there is one, the mark only until then
            AppRow("Show reset times", "Adds when each window rolls over, next to its percentage.",
                c => c.ShowResetTimes, a => a.SetResetTimes),
            AppRow("Show running agents",
                "Lists the Claude Code sessions running on this PC and marks the tray icon when one is waiting on you. Reads Claude Code's own session registry; nothing is written there.",
                c => c.AgentFleet, a => a.SetAgentFleet));

        var login = new SettingRow("Launch at login",
            "Managed as a per-user startup entry: RedLine starts when you sign in to Windows, and never after you quit.");
        login.Toggled += _ => { if (!syncing) M.Actions.ToggleLaunchAtLogin(); Resync(); };
        Sync(() => login.Set(S.LaunchAtLogin));
        yield return SettingsUI.Group("Start-up", null, login);
    }

    // MARK: Limits and alerts

    IEnumerable<UIElement> Limits()
    {
        var clash = new RLStateBlock(RLStateKind.Error, "Approaching sits at or above the limit threshold, so nothing will ever read as approaching.");
        Sync(() => clash.Visibility = C.LimitYellowPct >= C.LimitRedPct ? Visibility.Visible : Visibility.Collapsed);
        yield return SettingsUI.Group("Thresholds",
            "Where a window stops reading as healthy. These drive the rails, the cards, the tray colour and the notifications, so they can never disagree.",
            ThresholdRow("Approaching", "limitYellowPct", c => c.LimitYellowPct, new RLStatus(RLStatusKind.Approaching),
                "Above this a window is drawn as approaching its limit"),
            ThresholdRow("At the limit", "limitRedPct", c => c.LimitRedPct, new RLStatus(RLStatusKind.AtLimit),
                "Above this a window is drawn as at its limit"),
            clash);

        var alerts = AppRow("Notify at thresholds", "", c => c.Alerts, a => a.SetAlerts);
        Sync(() => alerts.Set(C.Alerts, explanation:
            $"Posts a notification when a window crosses {(int)C.LimitYellowPct}%, {(int)C.LimitRedPct}% or 95%, when one is about to run out before it resets, and when one rolls over. Never from a stale reading."));
        yield return SettingsUI.Group("Notifications", null,
            alerts, SettingsUI.Divider(),
            new SettingAction("Notification Style…",
                "Opens Windows Settings, where RedLine's banners can be switched on or off. How long a banner stays on screen is Windows's to decide, not RedLine's.",
                () => M.Actions.OpenNotificationSettings()));

        var cues = AppRow("Say how the day is going", "", c => c.MindfulCues, a => a.SetCues);
        Sync(() => cues.Set(C.MindfulCues, explanation:
            $"Says when a run has gone {(int)C.StretchMinutes} minutes without a break, when you are still going after {C.LateHour}:00, and when {C.StreakDays} days have run together."));
        var detailRows = new UIElement[]
        {
            SettingsUI.Divider(),
            SettingsUI.Labeled("Long run", StepperFor(15, 600, 15, v => $"{(int)v} minutes", c => c.StretchMinutes, "stretchMinutes", integer: false)),
            SettingsUI.Labeled("Late after", StepperFor(18, 23, 1, v => $"{(int)v:00}:00", c => c.LateHour, "lateHour", integer: true)),
            SettingsUI.Labeled("Days in a row", StepperFor(2, 90, 1, v => $"{(int)v} days", c => c.StreakDays, "streakDays", integer: true)),
        };
        Sync(() => { foreach (var r in detailRows) r.Visibility = C.MindfulCues ? Visibility.Visible : Visibility.Collapsed; });
        yield return SettingsUI.Group("How the day is going",
            "Counted from timestamps, stated once, never a sound and never advice.", [cues, .. detailRows]);
    }

    UIElement ThresholdRow(string title, string key, Func<Config, double> get, RLStatus status, string help)
    {
        var line = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(106), new GridLength(1, GridUnitType.Star), new GridLength(46) })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var icon = new RLStatusIndicator(status, 12) { VerticalAlignment = VerticalAlignment.Center };
        var name = SettingsUI.Text(title, RL.Typography.Body, RL.Ink.Primary, wrap: false, margin: new Thickness(RL.Space.Md, 0, 0, 0));
        name.VerticalAlignment = VerticalAlignment.Center;
        var slider = new Slider
        {
            Minimum = 5, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 5,
            MaxWidth = 260, Margin = new Thickness(RL.Space.Md, 0, RL.Space.Md, 0), ToolTip = help, VerticalAlignment = VerticalAlignment.Center,
        };
        // The system accent would put a blue track above an amber or red rail showing the same threshold
        slider.Bind(Control.ForegroundProperty, status.Color());
        AutomationProperties.SetName(slider, $"{title} threshold");
        var pct = SettingsUI.Text("", RL.Typography.MonoBody, status.Color(), wrap: false);
        pct.TextAlignment = TextAlignment.Right;
        pct.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(pct, 3);
        line.Children.Add(icon);
        line.Children.Add(name);
        line.Children.Add(slider);
        line.Children.Add(pct);
        var rail = new RLUsageRail(get(C), status, 6, showsLimit: true) { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, RL.Space.Xs, 0, 0) };
        slider.ValueChanged += (_, e) =>
        {
            pct.Text = $"{(int)e.NewValue}%";
            rail.Utilization = e.NewValue;
            if (!syncing && e.NewValue != get(C)) M.Write(key, e.NewValue);
        };
        Sync(() => { slider.Value = get(C); pct.Text = $"{(int)get(C)}%"; rail.Utilization = get(C); });
        var stack = new StackPanel();
        stack.Children.Add(line);
        stack.Children.Add(rail);
        return stack;
    }

    // MARK: Appearance

    IEnumerable<UIElement> Appearance()
    {
        var seg = new RLSegmented(
            [new RLSegment("auto", "Auto", "Follow the Windows appearance"), new RLSegment("light", "Light", "Always light"), new RLSegment("dark", "Dark", "Always dark")],
            null, 60) { HorizontalAlignment = HorizontalAlignment.Left };
        seg.Selected += (_, v) => { if (v is string t) { M.Actions.SetTheme(t); M.Reload(); } };
        Sync(() => seg.Selection = C.DashboardTheme);
        yield return SettingsUI.Group("Dashboard appearance",
            "Auto follows Windows. The tray icon always follows the taskbar, because Windows owns how the taskbar is drawn.", seg);

        var rows = new List<UIElement>();
        foreach (var provider in Config.KnownProviders)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new ProviderBadge(provider, 14) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var rail = new RLUsageRail(72, new RLStatus(RLStatusKind.Healthy), 8, showsLimit: false)
            { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Lg, 0, 0, 0) };
            rail.SetResourceReference(RLUsageRail.TintProperty, ProviderAccent.For(provider).BrushKey);
            Grid.SetColumn(rail, 1);
            row.Children.Add(badge);
            row.Children.Add(rail);
            AutomationProperties.SetName(row, $"{provider} accent colour");
            rows.Add(row);
        }
        rows.Add(SettingsUI.Caption("Checked for contrast on both appearances, for one lightness band and hue separation between providers, and for separability under protanopia, deuteranopia and tritanopia. Status is never carried by colour alone."));
        yield return SettingsUI.Group("Provider colours",
            "RedLine's own accents, used on the chip, dot, rail and chart series around each provider's mark. The marks themselves stay monochrome.", [.. rows]);
    }

    // MARK: Data and privacy

    IEnumerable<UIElement> Data()
    {
        var snooze = SettingsUI.Labeled("Hide a finding for",
            StepperFor(1, 365, 1, v => $"{(int)v} days", c => c.FindingsSnoozeDays, "findingsSnoozeDays", integer: true),
            "A dismissed finding returns after this if it is still true, because silently dropping something real is worse than repeating it.");
        Sync(() => snooze.Visibility = C.FindingsScans ? Visibility.Visible : Visibility.Collapsed);
        yield return SettingsUI.Group("What is kept on disk", null,
            AppRow("Keep local history",
                "Rolls each day up into %USERPROFILE%\\.local\\share\\redline\\history so your own numbers outlive Claude Code's 30 day transcript cleanup. Local file, no network.",
                c => c.RecordHistory, a => a.SetHistory),
            SettingsUI.Divider(),
            AppRow("Publish the usage sidecar",
                "Writes the current windows to %USERPROFILE%\\.local\\share\\redline\\usage-snapshot.json in the shape other local tools already read. Nothing leaves this PC.",
                c => c.PublishSidecar, a => a.SetSidecar),
            SettingsUI.Divider(),
            PlainRow("Scan for setup findings",
                "Looks through transcripts in the background for things worth changing about how Claude Code is set up. At most once every few hours, never on the UI thread.",
                "findingsScans", c => c.FindingsScans),
            snooze);

        yield return SettingsUI.Group("What leaves this PC",
            "RedLine reads local files by default. These are the only two settings that make a network request, and both are listed here rather than buried.",
            AppRow("Check service status pages", "Polls the providers' public status pages every 15 minutes. Off by default.",
                c => c.StatusChecks, a => a.SetStatusChecks),
            SettingsUI.Divider(),
            AppRow("Check for updates daily",
                "One call a day to the GitHub releases API, and it speaks up only when an update exists. This is the one request RedLine makes without being asked; switch it off and updates are yours to check for.",
                c => c.AutoCheckUpdates, a => a.SetAutoUpdates));

        // No Permissions group: Windows has no Full Disk Access, so there is no grant to ask for
        yield return SettingsUI.Group("The files themselves", null,
            new SettingAction("Open the Data Folder…",
                "%USERPROFILE%\\.local\\share\\redline, where history, the sidecar and the usage feed are written.",
                () => M.Actions.OpenDataFolder()),
            SettingsUI.Divider(),
            new SettingAction("Edit the Config File…", "The raw config.json. Everything in this window edits the same file.",
                () => M.Actions.EditConfig()),
            SettingsUI.Divider(),
            new SettingAction("Uninstall RedLine…",
                "Removes the app, the startup entry and the saved sign-in in Credential Manager. Asks what to do with your config and history first.",
                () => M.Actions.Uninstall(), destructive: true));
    }

    // MARK: About

    /// <summary>The bundled notice files, retained with the glyphs they describe and reachable from the app.</summary>
    public static readonly (string Title, string File, string Blurb)[] Notices =
    [
        ("Third-party notices", "THIRD_PARTY_NOTICES.md", "Which provider mark came from where, and the brand guidance for each"),
        ("Simple Icons licence", "LICENSE-simple-icons.md", "CC0-1.0, covering the Anthropic, Claude and Ollama marks"),
        ("Bootstrap Icons licence", "LICENSE-bootstrap-icons.txt", "MIT, covering the OpenAI blossom used for Codex"),
    ];

    IEnumerable<UIElement> About()
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Xl, 0, 0, 0) };
        words.Children.Add(SettingsUI.Text("RedLine", RL.Typography.Title, RL.Ink.Primary, wrap: false));
        words.Children.Add(SettingsUI.Text("Know your limit.", RL.Typography.MonoBody, RL.Ink.Muted, wrap: false, margin: new Thickness(0, RL.Space.Xxs, 0, 0)));
        var version = SettingsUI.Text("", RL.Typography.MonoSmall, RL.Ink.Secondary, wrap: false, margin: new Thickness(0, RL.Space.Xxs, 0, 0));
        Sync(() =>
        {
            version.Text = $"Version {S.AppVersion}";
            version.Visibility = S.AppVersion.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        words.Children.Add(version);
        var link = new Hyperlink(new Run("github.com/goriparthi/redline")) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, RL.Accent.Codex.BrushKey);
        link.Click += (_, _) => SettingsShell.Open(SettingsShell.RepoUrl);
        var linkText = new TextBlock(link) { Margin = new Thickness(0, RL.Space.Xs, 0, 0), ToolTip = "Source, releases and issues. Opens in your browser." };
        RL.Typography.MonoSmall.Apply(linkText);
        words.Children.Add(linkText);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new RedlineMarkAdaptive(44) { VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(words);
        yield return new RLCard { Child = head };

        yield return SettingsUI.Group("Updates", null,
            SettingsUI.Labeled("Channel", Picker([("stable", "Stable releases"), ("beta", "Beta releases")],
                c => c.UpdateChannel, v => M.Apply(c => c.UpdateChannel, M.Actions.SetUpdateChannel, v)),
                "Beta also offers prerelease builds. Newest always wins, so a stable release still reaches you the moment it outranks them."),
            SettingsUI.Divider(),
            new SettingAction("Check for Updates…", "Asks the GitHub releases API now.", () => M.Actions.CheckForUpdates()));

        var marks = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in Config.KnownProviders)
            marks.Children.Add(new ProviderBadge(provider, 15) { Margin = new Thickness(0, 0, RL.Space.Xl, 0) });
        var items = new List<UIElement> { marks, SettingsUI.Divider() };
        foreach (var (title, file, blurb) in Notices)
            items.Add(new SettingAction(title, blurb, () => M.Actions.OpenNotice(file)));
        yield return SettingsUI.Group("Provider marks",
            "The marks below identify each provider and are not RedLine branding. They do not imply sponsorship or endorsement, and open-source licensing of the vector data does not waive trademark restrictions.",
            [.. items]);
    }

#if DEBUG
    /// <summary>The whole window content, with the detail pane at its full scrolled height, as a PNG.</summary>
    internal void SaveSnapshot(string path)
    {
        var side = (Border)root.Children[0];
        var rule = (Border)root.Children[1];
        var h = Math.Max(root.ActualHeight, detail.ActualHeight + detail.Margin.Top + detail.Margin.Bottom);
        SettingsSnapshot.Render(this, root.ActualWidth, h, path, dc =>
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, h));
            dc.DrawRectangle(side.Background, null, new Rect(0, 0, side.ActualWidth, h));
            SettingsSnapshot.Draw(dc, side, 0, 0);
            dc.DrawRectangle(rule.Background, null, new Rect(side.ActualWidth, 0, 1, h));
            SettingsSnapshot.Draw(dc, detail, side.ActualWidth + 1 + detail.Margin.Left, detail.Margin.Top);
        });
    }
#endif
}
