// The dashboard's charts, drawn directly rather than through a chart package: stacked daily and
// hourly bars, the cost lines, the card sparkline and the cadence hours, each with a hover readout.
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Dashboard;

public enum ChartKind
{
    /// <summary>Bars per bucket, providers stacked in legend order.</summary>
    StackedBars,
    /// <summary>One smoothed line per provider over a faint unstacked area.</summary>
    Lines,
}

/// <summary>What a chart plots and how it names a value, shared by the three dashboard charts.</summary>
public sealed record ChartSpec(
    ChartKind Kind,
    Func<UsagePoint, double> Value,
    Func<double, string> Format,
    Func<DateTimeOffset, string> AxisLabel,
    Func<DateTimeOffset, string> ReadoutTitle,
    int LabelStride,
    double Height,
    double CornerRadius = 4);

/// <summary>A chart with its legend on top, as chartLegend(position: .top, alignment: .leading).</summary>
public sealed class DashboardChart : StackPanel
{
    public DashboardChart(IReadOnlyList<ProviderTrend> trends, ChartSpec spec, Theme theme, string summary)
    {
        var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, RL.Space.Md) };
        foreach (var t in trends)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, RL.Space.Lg, 0) };
            item.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = Themed.Solid(ChartPalette.Color(t.Provider, theme)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, RL.Space.Xs, 0),
            });
            item.Children.Add(Ui.Text(t.Provider, RL.Typography.Caption, RL.Ink.Muted));
            legend.Children.Add(item);
        }
        Children.Add(legend);
        var plot = new ChartPlot(trends, spec, theme) { Height = spec.Height };
        AutomationProperties.SetName(plot, summary);
        Children.Add(plot);
    }

    /// <summary>A chart's contents in words, since a drawn chart is opaque to assistive technology.</summary>
    public static string Summary(string what, IReadOnlyList<ProviderTrend> trends, Func<UsagePoint, double> value,
                                 Func<double, string> format)
    {
        if (trends.Count == 0) return $"{what}: no data";
        var named = trends.Select(t => $"{t.Provider} {format(t.Points.Sum(value))}");
        var overall = trends.Sum(t => t.Points.Sum(value));
        var line = $"{what}. Total {format(overall)}, by provider: {string.Join(", ", named)}.";
        var peak = trends.SelectMany(t => t.Points).MaxBy(value);
        if (peak is not null && value(peak) > 0)
            line += $" Busiest bucket {Ui.DateTime(peak.Start)} at {format(value(peak))}.";
        return line;
    }
}

/// <summary>Chart series take the provider's own accent, so a series and a card share one identity.</summary>
public static class ChartPalette
{
    public static Color Color(string provider, Theme theme) => ProviderAccent.For(provider).Resolve(theme);

    /// <summary>Full colour at the data end, quieter at the baseline, so stacks stay separable.</summary>
    public static Brush Fill(string provider, Theme theme)
    {
        var c = Color(provider, theme);
        var b = new LinearGradientBrush(c, System.Windows.Media.Color.FromArgb((byte)(255 * 0.55), c.R, c.G, c.B), 90);
        b.Freeze();
        return b;
    }
}

/// <summary>The drawing surface. Hover snaps to the nearest bucket, so a gap between bars is still answerable.</summary>
public sealed class ChartPlot : FrameworkElement
{
    readonly IReadOnlyList<ProviderTrend> trends;
    readonly ChartSpec spec;
    readonly Theme theme;
    readonly IReadOnlyList<DateTimeOffset> starts;
    int? hover;
    FrameworkElement? readout;

    const double AxisGap = 8;
    const double BottomAxis = 20;

    public ChartPlot(IReadOnlyList<ProviderTrend> trends, ChartSpec spec, Theme theme)
    {
        this.trends = trends;
        this.spec = spec;
        this.theme = theme;
        starts = trends.FirstOrDefault()?.Points.Select(p => p.Start).ToList() ?? [];
        Focusable = false;
        MouseMove += OnMove;
        MouseLeave += (_, _) => SetHover(null);
        SnapsToDevicePixels = true;
    }

    Brush Muted => RL.Ink.Muted.Brush(theme);

    FormattedText Label(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(RL.Typography.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Muted,
        Themed.PixelsPerDip(this));

    double Total(int i) => trends.Sum(t => i < t.Points.Count ? spec.Value(t.Points[i]) : 0);

    double RawMax => starts.Count == 0 ? 0 : spec.Kind == ChartKind.StackedBars
        ? Enumerable.Range(0, starts.Count).Max(Total)
        : trends.SelectMany(t => t.Points).Select(spec.Value).DefaultIfEmpty(0).Max();

    /// <summary>A round top and a step between four and six ticks, as Charts picks them.</summary>
    static (double Top, double Step) Nice(double max)
    {
        if (max <= 0) return (1, 0.25);
        var rough = max / 4;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var norm = rough / mag;
        var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * mag;
        return (Math.Ceiling(max / step) * step, step);
    }

    Rect Plot(out double top, out double step)
    {
        (top, step) = Nice(RawMax);
        double widest = 0;
        for (double v = 0; v <= top + step / 2; v += step) widest = Math.Max(widest, Label(spec.Format(v)).Width);
        var left = widest + AxisGap;
        return new Rect(left, 6, Math.Max(1, RenderSize.Width - left), Math.Max(1, RenderSize.Height - BottomAxis - 6));
    }

    protected override Size MeasureOverride(Size a)
    {
        readout?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsInfinity(a.Width) ? 400 : a.Width, double.IsNaN(Height) ? spec.Height : Height);
    }

    protected override Size ArrangeOverride(Size s)
    {
        if (readout is not null && hover is int i)
        {
            var plot = Plot(out _, out _);
            var slot = plot.Width / Math.Max(1, starts.Count);
            var x = plot.Left + slot * (i + 0.5);
            var w = readout.DesiredSize.Width;
            // Fitted to the chart, so a readout at either edge is never clipped
            var left = Math.Clamp(x - w / 2, 0, Math.Max(0, s.Width - w));
            readout.Arrange(new Rect(new Point(left, 0), readout.DesiredSize));
        }
        return s;
    }

    protected override int VisualChildrenCount => readout is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => readout!;

    void OnMove(object sender, MouseEventArgs e)
    {
        if (starts.Count == 0) return;
        var plot = Plot(out _, out _);
        var x = e.GetPosition(this).X - plot.Left;
        var slot = plot.Width / starts.Count;
        SetHover(Math.Clamp((int)Math.Floor(x / slot), 0, starts.Count - 1));
    }

    void SetHover(int? index)
    {
        if (hover == index) return;
        hover = index;
        if (readout is not null) RemoveVisualChild(readout);
        readout = index is int i ? BuildReadout(i) : null;
        if (readout is not null) AddVisualChild(readout);
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    FrameworkElement BuildReadout(int i)
    {
        // Legend order, skipping providers that did nothing: a row of zeros is noise here
        var rows = trends
            .Where(t => i < t.Points.Count && spec.Value(t.Points[i]) > 0)
            .Select(t => (t.Provider, spec.Format(spec.Value(t.Points[i])))).ToList();
        return new ChartReadout(spec.ReadoutTitle(starts[i]), rows, spec.Format(Total(i)), theme) { IsHitTestVisible = false };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        // A transparent fill, so the whole plot answers the pointer and not only the bars
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (starts.Count == 0) return;
        var plot = Plot(out var top, out var step);
        var grid = Themed.Pen(Themed.Tint(Muted, 0.15), 1, PenLineCap.Flat);
        var vgrid = Themed.Pen(Themed.Tint(Muted, 0.10), 1, PenLineCap.Flat);
        double Y(double v) => plot.Bottom - plot.Height * (v / top);

        // Y axis: gridlines with their figures on the leading edge
        for (double v = 0; v <= top + step / 2; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Label(spec.Format(v));
            dc.DrawText(ft, new Point(plot.Left - AxisGap - ft.Width, y - ft.Height / 2));
        }

        var n = starts.Count;
        var slot = plot.Width / n;
        // X axis: one cadence for both daily charts, so the same range reads as one span
        for (int i = 0; i < n; i += Math.Max(1, spec.LabelStride))
        {
            var x = Math.Round(plot.Left + slot * i) + 0.5;
            dc.DrawLine(vgrid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Label(spec.AxisLabel(starts[i]));
            if (x + ft.Width <= size.Width + 1) dc.DrawText(ft, new Point(x + 2, plot.Bottom + 3));
        }

        if (spec.Kind == ChartKind.StackedBars) DrawBars(dc, plot, slot, Y);
        else DrawLines(dc, plot, slot, Y);

        if (hover is int h)
        {
            var x = plot.Left + slot * (h + 0.5);
            dc.DrawLine(Themed.Pen(RL.Ink.Primary.Brush(theme, 0.25), 1, PenLineCap.Flat), new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    void DrawBars(DrawingContext dc, Rect plot, double slot, Func<double, double> y)
    {
        var width = Math.Max(1, slot * 0.62);
        var fills = trends.Select(t => ChartPalette.Fill(t.Provider, theme)).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            var x = plot.Left + slot * i + (slot - width) / 2;
            double acc = 0;
            for (int s = 0; s < trends.Count; s++)
            {
                var pts = trends[s].Points;
                var v = i < pts.Count ? spec.Value(pts[i]) : 0;
                if (v <= 0) continue;
                var y0 = y(acc);
                var y1 = y(acc + v);
                acc += v;
                var h = Math.Max(1, y0 - y1);
                var r = Math.Min(spec.CornerRadius, Math.Min(width / 2, h / 2));
                dc.DrawRoundedRectangle(fills[s], null, new Rect(x, y0 - h, width, h), r, r);
            }
        }
    }

    void DrawLines(DrawingContext dc, Rect plot, double slot, Func<double, double> y)
    {
        // Unstacked, one closed shape per provider: a shared fill once drew a band between series
        foreach (var t in trends)
        {
            var pts = t.Points.Select((p, i) => new Point(plot.Left + slot * (i + 0.5), y(spec.Value(p)))).ToList();
            if (pts.Count == 0) continue;
            var c = ChartPalette.Color(t.Provider, theme);
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(pts[0].X, plot.Bottom), true, true);
                g.LineTo(pts[0], false, false);
                Monotone(g, pts);
                g.LineTo(new Point(pts[^1].X, plot.Bottom), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(Themed.Solid(Color.FromArgb((byte)(255 * 0.22), c.R, c.G, c.B)), null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(pts[0], false, false);
                Monotone(g, pts);
            }
            line.Freeze();
            dc.DrawGeometry(null, Themed.Pen(Themed.Solid(c), 2), line);
        }
    }

    /// <summary>Monotone cubic segments (Fritsch-Carlson), so a smoothed line never overshoots a day.</summary>
    static void Monotone(StreamGeometryContext g, IReadOnlyList<Point> p)
    {
        var n = p.Count;
        if (n < 2) return;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (p[i + 1].Y - p[i].Y) / (p[i + 1].X - p[i].X);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9) { var t = 3 / Math.Sqrt(s); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
        }
        for (int i = 0; i < n - 1; i++)
        {
            var h = (p[i + 1].X - p[i].X) / 3;
            g.BezierTo(new Point(p[i].X + h, p[i].Y + m[i] * h), new Point(p[i + 1].X - h, p[i + 1].Y - m[i + 1] * h), p[i + 1], true, true);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this, () => false);
}

/// <summary>What a hovered bucket held, drawn where the pointer is. This is the value question.</summary>
public sealed class ChartReadout : Border
{
    public ChartReadout(string title, IReadOnlyList<(string Provider, string Value)> rows, string? total, Theme theme)
    {
        Padding = new Thickness(10, 8, 10, 8);
        MinWidth = 150;
        CornerRadius = new CornerRadius(RL.Radius.Control);
        BorderThickness = new Thickness(1);
        Background = RL.Surface.Overlay.Brush(theme);
        BorderBrush = RL.Stroke.Border.Brush(theme);
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.28, BlurRadius = 20, ShadowDepth = 3, Direction = 270 };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title, FontFamily = RL.Typography.Mono, FontSize = 11, FontWeight = FontWeights.Medium,
            Foreground = RL.Ink.Primary.Brush(theme), Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var (provider, value) in rows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Fill = Themed.Solid(ChartPalette.Color(provider, theme)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            var v = new TextBlock { Text = value, FontFamily = RL.Typography.Mono, FontSize = 11, Foreground = RL.Ink.Primary.Brush(theme), Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = provider, FontFamily = RL.Typography.UI, FontSize = 11, Foreground = RL.Ink.Muted.Brush(theme) });
            stack.Children.Add(row);
        }
        if (total is not null && rows.Count > 1)
        {
            stack.Children.Add(new Border { Height = 1, Background = RL.Ink.Muted.Brush(theme, 0.25), Margin = new Thickness(0, 0, 0, 4) });
            var row = new DockPanel();
            var v = new TextBlock { Text = total, FontFamily = RL.Typography.Mono, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = RL.Ink.Primary.Brush(theme) };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = "total", FontFamily = RL.Typography.UI, FontSize = 11, Foreground = RL.Ink.Muted.Brush(theme) });
            stack.Children.Add(row);
        }
        Child = stack;
    }
}

/// <summary>A trend as bars small enough for a card: only "when was the work", figures sit beside it.</summary>
public sealed class MiniTrend : FrameworkElement
{
    readonly IReadOnlyList<int> points;
    readonly Brush tint;
    readonly Brush empty;

    public MiniTrend(IReadOnlyList<int> points, Brush tint, Brush muted, double height, string label)
    {
        this.points = points;
        this.tint = tint;
        empty = Themed.Tint(muted, 0.25);
        Height = height;
        AutomationProperties.SetName(this, Summary(points, label));
    }

    static string Summary(IReadOnlyList<int> points, string label)
    {
        if (points.Count == 0 || !points.Any(p => p > 0)) return $"{label}: no activity";
        var total = points.Sum();
        var busiest = points.Select((v, i) => (v, i)).MaxBy(t => t.v);
        var ago = points.Count - 1 - busiest.i;
        var when = ago == 0 ? "today" : ago == 1 ? "yesterday" : $"{ago} days ago";
        return $"{label}: {Usage.FmtTokens(total)} over {points.Count} days, busiest {when} at {Usage.FmtTokens(busiest.v)}";
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 120 : a.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        if (points.Count == 0) return;
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        var peak = Math.Max(points.Max(), 1);
        // A gap that disappears once the bars are hairlines, so a long range stays a shape
        var gap = points.Count > 45 ? 0.5 : 1.5;
        var slot = w / points.Count;
        var width = Math.Max(1, slot - gap);
        for (int i = 0; i < points.Count; i++)
        {
            var v = points[i];
            // A real but tiny day keeps a visible foot rather than rounding to nothing
            var bh = v > 0 ? Math.Max(2, h * v / peak) : 1;
            var r = Math.Min(1.5, width / 2);
            dc.DrawRoundedRectangle(v > 0 ? tint : empty, null, new Rect(i * slot, h - bh, width, bh), r, r);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this, () => false);
}

/// <summary>One bar per local hour, for "when do I work". The hovered hour names its tokens.</summary>
public sealed class CadenceHours : FrameworkElement
{
    readonly IReadOnlyList<int> hours;
    readonly Theme theme;
    readonly ToolTip tip = new();
    int? hover;

    public CadenceHours(IReadOnlyList<int> hours, Theme theme)
    {
        this.hours = hours;
        this.theme = theme;
        Height = 44;
        ToolTip = tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        MouseMove += (_, e) =>
        {
            if (hours.Count == 0) return;
            var i = Math.Clamp((int)(e.GetPosition(this).X / (RenderSize.Width / hours.Count)), 0, hours.Count - 1);
            if (hover == i) return;
            hover = i;
            tip.Content = $"{i:00}:00 · {Usage.FmtTokens(hours[i])} tokens";
        };
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 240 : a.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (hours.Count == 0) return;
        var peak = Math.Max(1, hours.Max());
        var slot = RenderSize.Width / hours.Count;
        var signal = RL.Brandmark.Signal.Resolve(theme);
        var idle = RL.Ink.Muted.Brush(theme, 0.18);
        for (int i = 0; i < hours.Count; i++)
        {
            var v = hours[i];
            var h = Math.Max(3, 44.0 * v / peak);
            var fill = v > 0 ? Themed.Solid(Color.FromArgb((byte)(255 * (0.35 + 0.65 * v / peak)), signal.R, signal.G, signal.B)) : idle;
            dc.DrawRoundedRectangle(fill, null, new Rect(i * slot + 1, RenderSize.Height - h, Math.Max(1, slot - 2), h), 2, 2);
        }
    }
}
