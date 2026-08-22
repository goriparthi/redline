using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RedLine.Core;
using Windows.Graphics;
using Windows.UI;

namespace RedLine.App;

/// <summary>
/// What the last few weeks looked like: two tiles from the published snapshot, a daily chart,
/// and which models the tokens went on.
///
/// Every figure and every label comes from the engine, including how the days are bucketed and
/// how often the axis carries a label. This file decides pixels and nothing else.
/// </summary>
public sealed partial class DashboardWindow : Window
{
    private const int WindowWidth = 720;
    private const int WindowHeight = 780;
    private const double ChartHeight = 150;

    /// <summary>The ranges offered. The engine decides the axis cadence for each.</summary>
    private static readonly int[] Ranges = [7, 14, 30, 90];

    private readonly TrendStore trends;
    private readonly Engine engine;
    private int days = 14;
    private bool building;

    /// <summary>
    /// How many bars the last load drew. The self test reports it: negative means the page
    /// threw, zero means the engine had nothing recorded yet, which is an ordinary state on a
    /// machine that has only just started watching.
    /// </summary>
    public int BarCount { get; private set; }

    public DashboardWindow(TrendStore? trends = null, Engine? engine = null)
    {
        InitializeComponent();
        this.engine = engine ?? new Engine();
        this.trends = trends ?? new TrendStore(this.engine);
        Title = "RedLine Dashboard";
        Shape();
        BuildRangePicker();
        Load();
    }

    private static SolidColorBrush Chalk => new(Color.FromArgb(255, 0xF4, 0xF1, 0xEA));
    private static SolidColorBrush Steel => new(Color.FromArgb(255, 0xA8, 0xAE, 0xBA));
    private static SolidColorBrush Muted => new(Color.FromArgb(255, 0x84, 0x8A, 0x96));
    private static SolidColorBrush Hairline => new(Color.FromArgb(255, 0x26, 0x2A, 0x32));
    private static SolidColorBrush Raised => new(Color.FromArgb(255, 0x17, 0x1A, 0x1F));
    private static SolidColorBrush Signal => new(Color.FromArgb(255, 0xFF, 0x3B, 0x30));

    private void Shape()
    {
        var window = AppWindow;
        if (window is null) return;

        window.Title = "RedLine Dashboard";
        window.Resize(new SizeInt32(WindowWidth, WindowHeight));

        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            var work = area.WorkArea;
            window.Move(new PointInt32(work.X + ((work.Width - WindowWidth) / 2),
                                       work.Y + ((work.Height - WindowHeight) / 2)));
        }
    }

    private void BuildRangePicker()
    {
        building = true;
        RangePicker.ItemsSource = Ranges.Select(r => $"Last {r} days").ToList();
        RangePicker.SelectedIndex = Array.IndexOf(Ranges, days);
        RangePicker.SelectionChanged += (_, _) =>
        {
            if (building) return;
            var picked = RangePicker.SelectedIndex;
            if (picked < 0 || picked >= Ranges.Length) return;
            days = Ranges[picked];
            Load();
        };
        building = false;
    }

    /// <summary>
    /// Asks the engine and redraws. Synchronous: it is one short run of a local binary, and a
    /// chart that arrives after the window would be a window that flashes empty first.
    /// </summary>
    public void Load()
    {
        var report = trends.Read(days);
        var snapshot = engine.ReadSnapshot();

        BuildTiles(snapshot);

        Panels.Children.Clear();
        Panels.Children.Add(ChartPanel(report));
        Panels.Children.Add(ModelPanel(report));

        BarCount = report.Available ? TrendChart.Bars(report).Count : -1;
        FooterText.Text = report.Available
            ? $"Days are {(report.DayBasis.Length > 0 ? report.DayBasis : "local")}. "
              + "Estimates over measured counts, never a bill."
            : report.Problem;
    }

    private void BuildTiles(Snapshot? snapshot)
    {
        Tiles.Children.Clear();
        Tiles.ColumnDefinitions.Clear();

        var today = snapshot?.Today;
        var week = snapshot?.Week;
        var worst = snapshot?.Worst;

        AddTile("Today", TrayPresenter.Volume(today), TrayPresenter.Spend(today));
        AddTile("This week", TrayPresenter.Volume(week), TrayPresenter.Spend(week));
        AddTile(worst is null ? "No limit reported" : $"{worst.Provider} · {worst.DisplayName}",
                worst is null ? "n/a" : $"{Math.Round(worst.Utilization)}%",
                worst is null ? "nothing to report yet" : "of the window used");
    }

    private void AddTile(string label, string figure, string detail)
    {
        Tiles.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(Line(label.ToUpperInvariant(), 10, Muted, spacing: 140));
        stack.Children.Add(Line(figure, 24, Chalk, weight: FontWeights.SemiBold));
        stack.Children.Add(Line(detail, 11, Steel));

        var tile = new Border
        {
            Background = Raised,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
        };
        Grid.SetColumn(tile, Tiles.ColumnDefinitions.Count - 1);
        Tiles.Children.Add(tile);
    }

    // The chart

    private FrameworkElement ChartPanel(TrendReport report)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Heading("Tokens by day"));
        stack.Children.Add(Line(report.Summary, 12, report.Available ? Steel : Signal));

        IReadOnlyList<ChartBar> bars = report.Available ? TrendChart.Bars(report) : [];
        if (bars.Count == 0)
        {
            // Nothing to draw is said in words. An empty axis would read as a fortnight of
            // silence, which is a different claim from having nothing recorded.
            return Card(stack);
        }

        var columns = new Grid { Height = ChartHeight, ColumnSpacing = 3 };
        var axis = new Grid { ColumnSpacing = 3 };
        var busiest = report.Busiest?.Day;

        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            columns.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            axis.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });

            // A quiet day keeps a sliver at the baseline in the hairline colour, so it reads
            // as a day with nothing in it rather than as a day that is missing.
            var block = new Rectangle
            {
                Fill = bar.Tokens == 0 ? Hairline : bar.Day == busiest ? Chalk : Steel,
                Height = Math.Max(2, bar.Share * ChartHeight),
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 2,
                RadiusY = 2,
            };
            // "at least" when anything in the window was unpriced: a per day cost shown as
            // exact would be the one number on this page nobody measured.
            var spend = report.HasUnpriced
                ? $"at least {Formatting.Cost(bar.Cost)}" : Formatting.Cost(bar.Cost);
            ToolTipService.SetToolTip(
                block, $"{bar.Day} · {Formatting.Tokens(bar.Tokens)} · {spend}");
            Grid.SetColumn(block, index);
            columns.Children.Add(block);

            if (bar.Label.Length == 0) continue;
            var label = Line(bar.Label, 10, Muted);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, index);
            axis.Children.Add(label);
        }

        stack.Children.Add(columns);
        stack.Children.Add(new Border
        {
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Child = axis,
        });
        return Card(stack);
    }

    // The model mix

    private FrameworkElement ModelPanel(TrendReport report)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Heading("Where the tokens went"));

        var (shown, hidden) = TrendChart.TopModels(report);
        if (shown.Count == 0)
        {
            stack.Children.Add(Line("No models recorded in this window", 12, Muted));
            return Card(stack);
        }

        foreach (var model in shown)
        {
            stack.Children.Add(ModelRow(model, report));
        }
        if (hidden > 0)
        {
            stack.Children.Add(Line($"+{hidden} more", 11, Muted));
        }
        if (report.HasUnpriced)
        {
            // The same sentence the macOS mix carries, for the same reason: n/a is not zero
            stack.Children.Add(Line("n/a means no pricing entry, so it is counted in tokens "
                                    + "only", 11, Muted));
        }
        return Card(stack);
    }

    private FrameworkElement ModelRow(ModelShare model, TrendReport report)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var name = Line(model.Model, 12, Chalk);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var share = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(TrendChart.Share(model, report) * 100, 0, 100),
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Foreground = Steel,
            Background = Hairline,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(share, 1);
        row.Children.Add(share);

        var tokens = Line(Formatting.Tokens(model.Tokens), 12, Steel);
        tokens.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(tokens, 2);
        row.Children.Add(tokens);

        var cost = Line(TrendChart.CostOf(model), 12, model.Priced ? Steel : Muted);
        cost.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cost, 3);
        row.Children.Add(cost);
        return row;
    }

    // The pieces

    /// <summary>Named Card rather than Panel, which is a XAML type sitting right there.</summary>
    private static Border Card(FrameworkElement content) => new()
    {
        Background = Raised,
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 14, 16, 16),
        Child = content,
    };

    private static TextBlock Heading(string text) =>
        Line(text.ToUpperInvariant(), 11, Muted, weight: FontWeights.SemiBold, spacing: 180);

    private static TextBlock Line(string text, double size, Brush colour,
                                  FontWeight? weight = null, int spacing = 0) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = colour,
        FontWeight = weight ?? FontWeights.Normal,
        CharacterSpacing = spacing,
        TextWrapping = TextWrapping.Wrap,
    };
}
