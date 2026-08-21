using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RedLine.Core;

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

    /// <summary>What the self test reports: enough to tell a real start from an empty one.</summary>
    public string SelfTestSummary =>
        $"tray={(tray?.IsCreated == true ? "created" : "missing")} "
        + $"engine={(host.IsRunning ? "running" : "stopped")} {renderSummary}"
        + (EngineNote.Length > 0 ? $" note=\"{EngineNote}\"" : "");

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
        monitor.Dispose();
        host.Dispose();
        tray?.Dispose();
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = "RedLine";
        BuildTray();

        host.GaveUp += reason => dispatcher.TryEnqueue(() => EngineNote = reason);
        EngineRunning = host.Start();

        // The monitor raises on a background thread, and touching XAML from one is a crash
        monitor.Updated += snapshot => dispatcher.TryEnqueue(() => Render(snapshot));
        monitor.Start();
        Render(monitor.Current);

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
        // An HICON from the .ico, not an ImageSource. IconSource takes an ImageSource, but a
        // BitmapImage decodes asynchronously and the conversion to an icon runs before it has
        // finished, which fails at startup with "must be a picture that can be used as a Icon".
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

    private void Render(Snapshot? snapshot)
    {
        var view = TrayPresenter.From(snapshot, DateTimeOffset.UtcNow);
        TitleText.Text = view.Title;
        PhraseText.Text = view.Phrase;
        DetailText.Text = view.Detail;
        // The tooltip is the only thing most people ever read, so it carries the phrase and
        // not just the number
        if (tray is not null) tray.ToolTipText = $"RedLine · {view.Title} · {view.Phrase}";

        // Projected to plain strings rather than to a model type. Any public type reachable
        // from XAML gets bindable type info generated for it, and the generator writes to
        // properties, so an init-only record fails the build.
        WindowsList.ItemsSource = (snapshot?.Limits ?? [])
            .OrderByDescending(w => w.Utilization)
            .Select(w => $"{w.Provider} · {w.DisplayName}   {Math.Round(w.Utilization)}%")
            .ToList();

        renderSummary = $"title={view.Title} windows={(snapshot?.Limits.Count ?? 0)}";
        if (snapshot?.Limits.Count > 0 || (snapshot?.Today?.Io ?? 0) > 0) HasRenderedData = true;
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
