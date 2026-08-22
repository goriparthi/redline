using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedLine.Core;
using Windows.Graphics;
using Windows.UI;

namespace RedLine.App;

/// <summary>
/// The tray icon and the window behind it. Every string shown here comes from RedLine.Core,
/// which is where the tests are, so this file makes no judgement about what a number means.
/// </summary>
public sealed partial class MainWindow : Window
{
    // The app keeps its own data current, the way the macOS app does by being the watcher.
    // Without this it would show whatever was last published and quietly go stale.
    private readonly EngineHost host = new();
    private readonly SnapshotMonitor monitor = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private TaskbarIcon? tray;
    /// The settings page, kept only while it is open: closing a WinUI window destroys it
    private SettingsWindow? settings;
    private DashboardWindow? dashboard;
    /// The thresholds the engine is using, so the colour here means what it means there
    private double approaching = TrayPresenter.ApproachingPercent;
    private double atLimit = TrayPresenter.AtLimitPercent;
    /// The last icon drawn for the tray, kept so its GDI handle can be released when replaced
    private System.Drawing.Icon? trayIcon;

    /// <summary>What the self test reports: enough to tell a real start from an empty one.</summary>
    public string SelfTestSummary =>
        $"tray={(tray?.IsCreated == true ? "created" : "missing")} "
        + $"engine={(host.IsRunning ? "running" : "stopped")} {renderSummary}"
        + $" settings={settingsControls} dashboard={dashboardBars}"
        + (EngineNote.Length > 0 ? $" note=\"{EngineNote}\"" : "");

    /// <summary>Not probed. Zero would be a real count, so it cannot be the resting value.</summary>
    private int settingsControls = -1;
    private int dashboardBars = -1;

    /// <summary>
    /// Builds both pages and remembers what they made. Only the self test calls this: a page
    /// that renders nothing looks exactly like one that rendered fine, and CI is the only
    /// machine here that ever runs WinUI.
    ///
    /// The bar count can honestly be zero, because the watcher may not have ingested yet when
    /// the snapshot has already arrived through the feed. Negative is the failure.
    /// </summary>
    public void ProbeWindows()
    {
        var page = new SettingsWindow();
        settingsControls = page.ControlCount;
        page.Close();

        var chart = new DashboardWindow();
        dashboardBars = chart.BarCount;
        chart.Close();
    }

    private string renderSummary = "not rendered";

    /// <summary>True once a snapshot with something in it has been drawn. The self test waits
    /// on this rather than on a fixed delay, because it is proving a whole chain: a transcript
    /// on disk, through the watcher this app started, into the window.</summary>
    public bool HasRenderedData { get; private set; }

    /// <summary>Set when the engine will not run, so the reason can reach a person instead of
    /// leaving them with an empty window.</summary>
    public string EngineNote { get; private set; } = "";

    public bool EngineRunning { get; private set; }

    /// <summary>Stops everything this window started. Called on close, and by the self test,
    /// which exits the process outright and so runs no finalizers.</summary>
    public void ShutDown()
    {
        settings?.Close();
        settings = null;
        dashboard?.Close();
        dashboard = null;
        monitor.Dispose();
        host.Dispose();
        tray?.Dispose();
        TrayGlyph.Release(trayIcon);
        trayIcon = null;
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = "RedLine";
        ShapeWindow();
        BuildTray();

        host.GaveUp += reason => dispatcher.TryEnqueue(() => EngineNote = reason);
        EngineRunning = host.Start();

        // The monitor raises on a background thread, and touching XAML from one is a crash
        monitor.Updated += snapshot => dispatcher.TryEnqueue(() => Render(snapshot));
        monitor.Start();
        Render(monitor.Current);
        ReadThresholds();

        Closed += (_, _) => ShutDown();
    }

    private void BuildTray()
    {
        var menu = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "Open RedLine" };
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);

        var refresh = new MenuFlyoutItem { Text = "Refresh now" };
        refresh.Click += (_, _) => monitor.Reread();
        menu.Items.Add(refresh);

        var history = new MenuFlyoutItem { Text = "Dashboard" };
        history.Click += (_, _) => ShowDashboard();
        menu.Items.Add(history);

        var configure = new MenuFlyoutItem { Text = "Settings" };
        configure.Click += (_, _) => ShowSettings();
        menu.Items.Add(configure);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) =>
        {
            ShutDown();
            Application.Current.Exit();
        };
        menu.Items.Add(quit);

        tray = new TaskbarIcon
        {
            ToolTipText = "RedLine",
            ContextFlyout = menu,
            NoLeftClickDelay = true,
        };
        // The mark, until there is a reading to draw instead. An HICON from the .ico, not an
        // ImageSource: IconSource takes an ImageSource, but a BitmapImage decodes
        // asynchronously and the conversion runs before it has finished, which fails at
        // startup with "must be a picture that can be used as a Icon".
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "RedLine.ico");
        if (File.Exists(ico))
        {
            // 32 rather than 16: Windows scales it down cleanly, and asking for 16 leaves
            // nothing to scale up on a high DPI display.
            tray.Icon = new System.Drawing.Icon(ico, new System.Drawing.Size(32, 32));
        }
        tray.LeftClickCommand = new RelayCommand(ShowWindow);
        tray.ForceCreate();
    }

    /// <summary>
    /// A status readout, not a document. Sized and placed like the thing it is: small, near
    /// the tray it belongs to, and without a resize grip inviting someone to stretch it.
    /// </summary>
    private void ShapeWindow()
    {
        var window = AppWindow;
        if (window is null) return;

        window.Title = "RedLine";
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        const int width = 400;
        const int height = 520;
        window.Resize(new SizeInt32(width, height));

        // Bottom right, above the taskbar, which is where the icon that opens it lives
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            var work = area.WorkArea;
            window.Move(new PointInt32(work.X + work.Width - width - 16,
                                       work.Y + work.Height - height - 16));
        }
    }

    private static SolidColorBrush Fill(TrayLevel level) => new(level switch
    {
        TrayLevel.AtLimit => Color.FromArgb(255, 0xFF, 0x3B, 0x30),      // signal
        TrayLevel.Approaching => Color.FromArgb(255, 0xFF, 0x9F, 0x0A), // amber
        TrayLevel.Healthy => Color.FromArgb(255, 0x32, 0xD7, 0x4B),     // clear
        _ => Color.FromArgb(255, 0x84, 0x8A, 0x96),                     // muted, for unknown
    });

    private void Render(Snapshot? snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var view = TrayPresenter.From(snapshot, now, approaching, atLimit);
        TitleText.Text = view.Title;
        TitleText.Foreground = Fill(view.Level);
        PhraseText.Text = view.Phrase;
        DetailText.Text = view.Detail;
        FooterText.Text = Footer(snapshot, now);
        UpdateTray(view);

        // A settable class, not a record: any public type reachable from XAML gets bindable
        // type info generated for it, and the generator assigns to properties, so init-only
        // members fail the build.
        WindowsList.ItemsSource = (snapshot?.Limits ?? [])
            .OrderByDescending(w => w.Utilization)
            .Select(w => new LimitRow
            {
                Label = $"{w.Provider} · {w.DisplayName}",
                Reading = $"{Math.Round(w.Utilization)}%",
                Value = Math.Clamp(w.Utilization, 0, 100),
                Fill = Fill(TrayPresenter.Classify(w.Utilization, approaching, atLimit)),
            })
            .ToList();

        renderSummary = $"title={view.Title} windows={(snapshot?.Limits.Count ?? 0)}";
        if (snapshot?.Limits.Count > 0 || (snapshot?.Today?.Io ?? 0) > 0) HasRenderedData = true;
    }

    /// <summary>
    /// Where the numbers came from and how old they are. A reading presented as current when
    /// it is hours old is worse than no reading.
    /// </summary>
    private string Footer(Snapshot? snapshot, DateTimeOffset now)
    {
        if (EngineNote.Length > 0) return EngineNote;
        if (snapshot is null) return "Waiting for the first reading";
        var age = snapshot.AgeAt(now);
        var when = age < TimeSpan.FromMinutes(1) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
            : $"{(int)age.TotalHours} h ago";
        return $"Read {when}" + (snapshot.ClaudeLimitsAsOf is null ? "" : " · limits from Claude Code");
    }

    /// <summary>
    /// Puts the reading in the notification area itself. Windows offers no menu-bar text the
    /// way macOS does, so the number is drawn into the icon, which is what every other meter
    /// on the taskbar does.
    /// </summary>
    private void UpdateTray(TrayView view)
    {
        if (tray is null) return;

        // The phrase, not just the number: the icon carries colour, and colour alone says
        // nothing to someone who cannot separate these three
        tray.ToolTipText = $"RedLine · {view.Title} · {view.Phrase}\n{view.Detail}";

        // Only a percentage is worth drawing. A token count does not fit in sixteen pixels.
        var label = view.Title.EndsWith('%') ? view.Title.TrimEnd('%') : null;
        if (label is null) return;

        var colour = view.Level switch
        {
            TrayLevel.AtLimit => System.Drawing.Color.FromArgb(0xFF, 0x3B, 0x30),
            TrayLevel.Approaching => System.Drawing.Color.FromArgb(0xFF, 0x9F, 0x0A),
            TrayLevel.Healthy => System.Drawing.Color.FromArgb(0x32, 0xD7, 0x4B),
            _ => System.Drawing.Color.FromArgb(0x84, 0x8A, 0x96),
        };

        var previous = trayIcon;
        trayIcon = TrayGlyph.Render(label, colour);
        tray.Icon = trayIcon;
        TrayGlyph.Release(previous);
    }

    /// <summary>
    /// The thresholds are a setting, so the colour in the tray has to follow them or this app
    /// calls a window healthy while the engine calls it red. Off the UI thread: it runs the
    /// engine, and the window must not wait on that to appear.
    /// </summary>
    private void ReadThresholds()
    {
        Task.Run(() =>
        {
            var catalog = new SettingsStore().Read();
            if (!catalog.Available) return;
            var yellow = catalog.Find("limitYellowPct")?.Number;
            var red = catalog.Find("limitRedPct")?.Number;
            dispatcher.TryEnqueue(() =>
            {
                approaching = yellow ?? approaching;
                atLimit = red ?? atLimit;
                Render(monitor.Current);
            });
        });
    }

    private void ShowSettings()
    {
        if (settings is null)
        {
            var page = new SettingsWindow();
            // A change to the thresholds has to reach the tray without a restart
            page.Changed += () => dispatcher.TryEnqueue(ReadThresholds);
            page.Closed += (_, _) => settings = null;
            settings = page;
        }
        settings.Activate();
    }

    private void ShowDashboard()
    {
        if (dashboard is null)
        {
            var page = new DashboardWindow();
            page.Closed += (_, _) => dashboard = null;
            dashboard = page;
        }
        else
        {
            // Reopened rather than rebuilt, so it shows what has happened since
            dashboard.Load();
        }
        dashboard.Activate();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }
}

/// <summary>The smallest ICommand that will do. A whole MVVM package for one click is not a
/// trade worth making.</summary>
internal sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}

/// <summary>
/// One limit window as the template wants it. A class with settable properties rather than a
/// record, because XAML's generated type info assigns to properties and init-only members
/// fail the build.
/// </summary>
public sealed class LimitRow
{
    public string Label { get; set; } = "";
    public string Reading { get; set; } = "";
    public double Value { get; set; }
    public Microsoft.UI.Xaml.Media.Brush? Fill { get; set; }
}
