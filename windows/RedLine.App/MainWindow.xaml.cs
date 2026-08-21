using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RedLine.Core;

namespace RedLine.App;

/// <summary>
/// The tray icon and the window behind it. Every string shown here comes from RedLine.Core,
/// which is where the tests are, so this file makes no judgement about what a number means.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SnapshotMonitor monitor = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<WindowRow> Rows { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Title = "RedLine";
        WindowsList.ItemsSource = Rows;

        // The monitor raises on a background thread, and touching XAML from one is a crash
        monitor.Updated += snapshot => dispatcher.TryEnqueue(() => Render(snapshot));
        monitor.Start();
        Render(monitor.Current);

        Closed += (_, _) => monitor.Dispose();
    }

    private void Render(Snapshot? snapshot)
    {
        var view = TrayPresenter.From(snapshot, DateTimeOffset.UtcNow);
        TitleText.Text = view.Title;
        PhraseText.Text = view.Phrase;
        DetailText.Text = view.Detail;
        // The tooltip is the only thing most people ever read, so it carries the phrase and
        // not just the number
        Tray.ToolTipText = $"RedLine · {view.Title} · {view.Phrase}";

        Rows.Clear();
        foreach (var window in (snapshot?.Limits ?? []).OrderByDescending(w => w.Utilization))
        {
            Rows.Add(new WindowRow($"{window.Provider} · {window.DisplayName}",
                                   $"{Math.Round(window.Utilization)}%"));
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => ShowWindow();

    private void OnRefresh(object sender, RoutedEventArgs e) => monitor.Reread();

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        monitor.Dispose();
        Application.Current.Exit();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }
}

public sealed record WindowRow(string Label, string Reading);
