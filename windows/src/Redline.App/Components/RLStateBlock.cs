// The four things a panel can say instead of showing data, so "nothing yet", "still reading"
// and "cannot read" never look like the same thing.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Redline.App.Components;

public enum RLStateKind
{
    Loading,
    /// <summary>Nothing to show, and that is a real answer rather than a fault.</summary>
    Empty,
    /// <summary>Something went wrong, said plainly.</summary>
    Error,
    /// <summary>The metric cannot exist here, with the reason. Never a fabricated zero.</summary>
    Unavailable,
}

public class RLStateBlock : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(RLStateKind), typeof(RLStateBlock), new PropertyMetadata(RLStateKind.Empty, (d, _) => ((RLStateBlock)d).Update()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RLStateBlock), new PropertyMetadata("", (d, _) => ((RLStateBlock)d).Update()));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(RLStateBlock), new PropertyMetadata(null, (d, _) => ((RLStateBlock)d).Update()));

    public RLStateKind Kind { get => (RLStateKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    readonly ShapeIcon icon = new() { Size = 13, Margin = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly Spinner spinner = new() { VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, RL.Space.Xxs, 0, 0) };

    public RLStateBlock()
    {
        RL.Typography.Body.Apply(text);
        text.Bind(TextBlock.ForegroundProperty, RL.Ink.Secondary);
        RL.Typography.Caption.Apply(hint);
        hint.Bind(TextBlock.ForegroundProperty, RL.Ink.Muted);
        var words = new StackPanel { Margin = new Thickness(RL.Space.Md, 0, 0, 0) };
        words.Children.Add(text);
        words.Children.Add(hint);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(icon);
        grid.Children.Add(spinner);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        Content = grid;
        Focusable = false;
        IsTabStop = false;
        Update();
    }

    public RLStateBlock(RLStateKind kind, string text, string? hint = null) : this()
    {
        Kind = kind; Text = text; Hint = hint;
    }

    void Update()
    {
        var loading = Kind == RLStateKind.Loading;
        spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        icon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        icon.Shape = Kind switch
        {
            RLStateKind.Empty => StatusShapes.Tray,
            RLStateKind.Error => StatusShapes.TriangleOutline,
            _ => StatusShapes.MinusCircle,
        };
        icon.Bind(ShapeIcon.ForegroundProperty, Kind == RLStateKind.Error ? RL.State.Warning : RL.Ink.Muted);
        text.Text = Text;
        hint.Text = Hint ?? "";
        hint.Visibility = Hint is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, Hint is null ? Text : $"{Text}. {Hint}");
    }
}

/// <summary>A small indeterminate spinner (ProgressView().controlSize(.small)). Still when animations are off.</summary>
public class Spinner : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        "Ink", typeof(Brush), typeof(Spinner), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    readonly RotateTransform spin = new();

    public Spinner()
    {
        this.Bind(InkProperty, RL.Ink.Muted);
        RenderTransform = spin;
        IsVisibleChanged += (_, _) => Run();
        Loaded += (_, _) => Run();
    }

    void Run()
    {
        spin.CenterX = Size / 2;
        spin.CenterY = Size / 2;
        if (!IsVisible || RL.Motion.Reduced) { spin.BeginAnimation(RotateTransform.AngleProperty, null); return; }
        spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever });
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size _) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var ink = (Brush)GetValue(InkProperty);
        var w = Math.Max(1.5, Size * 0.13);
        var r = (Size - w) / 2;
        var c = new Point(Size / 2, Size / 2);
        dc.DrawEllipse(null, new Pen(Themed.Tint(ink, 0.25), w), c, r, r);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X + r, c.Y), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(null, Themed.Pen(ink, w), arc);
    }
}
