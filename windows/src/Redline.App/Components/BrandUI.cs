// Shared brand pieces (port of BrandUI.swift): the RedLine mark, the widget's limit rail,
// and the provider track badge and mark.
using System.Windows;
using Redline.Core;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Redline.App.Components;

/// <summary>The symbol from brand/logo/redline-symbol.svg: three streams meeting one limit line.</summary>
/// <remarks>Fixed chalk-on-carbon tones; use RedlineMarkAdaptive on a surface that follows the theme.</remarks>
public class RedlineMark : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(RedlineMark),
        new FrameworkPropertyMetadata(26.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public RedlineMark() { }
    public RedlineMark(double size) { Size = size; }

    /// <summary>Multiplies every stroke colour, as SwiftUI's colorMultiply. White leaves the mark as drawn.</summary>
    protected virtual Color Multiply => Colors.White;

    static Color Mul(Color a, Color b) =>
        Color.FromRgb((byte)(a.R * b.R / 255), (byte)(a.G * b.G / 255), (byte)(a.B * b.B / 255));

    protected override System.Windows.Size MeasureOverride(System.Windows.Size _) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var s = Size;
        Point P(double x, double y) => new(x / 256 * s, y / 256 * s);
        var m = Multiply;

        var streams = new StreamGeometry();
        using (var g = streams.Open())
        {
            g.BeginFigure(P(48, 65), false, false);
            g.BezierTo(P(92, 65), P(108, 108), P(139, 125), true, true);
            g.BeginFigure(P(48, 191), false, false);
            g.BezierTo(P(92, 191), P(108, 148), P(139, 131), true, true);
        }
        dc.DrawGeometry(null, Themed.Pen(Themed.Solid(Mul(RL.BrandTone.Chalk, m)), s * 18 / 256), streams);

        var letter = new StreamGeometry();
        using (var g = letter.Open())
        {
            g.BeginFigure(P(48, 128), false, false);
            g.LineTo(P(178, 128), true, true);
            g.BeginFigure(P(118, 57), false, false);
            g.LineTo(P(151, 57), true, true);
            g.BezierTo(P(187, 57), P(207, 76), P(207, 103), true, true);
            g.BezierTo(P(207, 121), P(198, 131), P(184, 139), true, true);
            g.BeginFigure(P(163, 143), false, false);
            g.LineTo(P(207, 199), true, true);
        }
        dc.DrawGeometry(null, Themed.Pen(Themed.Solid(Mul(RL.BrandTone.Steel, m)), s * 18 / 256), letter);

        // The limit line, the one red element
        dc.DrawLine(Themed.Pen(Themed.Solid(Mul(RL.BrandTone.Signal, m)), s * 8 / 256), P(126, 128), P(214, 128));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this, () => false, "RedLine");
}

/// <summary>The RedLine mark re-inked by primary ink, so it follows the theme (colorMultiply in Swift).</summary>
public class RedlineMarkAdaptive : RedlineMark
{
    static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        "Ink", typeof(Brush), typeof(RedlineMarkAdaptive),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public RedlineMarkAdaptive() { this.Bind(InkProperty, RL.Ink.Primary); }
    public RedlineMarkAdaptive(double size) : this() { Size = size; }

    protected override Color Multiply => GetValue(InkProperty) is SolidColorBrush b ? b.Color : Colors.White;
}

/// <summary>The widget's usage rail: fixed brand tones, ending at its limit. Stale drains to steel.</summary>
public class LimitRail : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object v) => new(v, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty UtilizationProperty = DependencyProperty.Register(nameof(Utilization), typeof(double), typeof(LimitRail), Render(0.0));
    public static readonly DependencyProperty RailHeightProperty = DependencyProperty.Register(
        nameof(RailHeight), typeof(double), typeof(LimitRail),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowsLimitProperty = DependencyProperty.Register(nameof(ShowsLimit), typeof(bool), typeof(LimitRail), Render(true));
    public static readonly DependencyProperty StaleProperty = DependencyProperty.Register(nameof(Stale), typeof(bool), typeof(LimitRail), Render(false));

    public double Utilization { get => (double)GetValue(UtilizationProperty); set => SetValue(UtilizationProperty, value); }
    public double RailHeight { get => (double)GetValue(RailHeightProperty); set => SetValue(RailHeightProperty, value); }
    public bool ShowsLimit { get => (bool)GetValue(ShowsLimitProperty); set => SetValue(ShowsLimitProperty, value); }
    public bool Stale { get => (bool)GetValue(StaleProperty); set => SetValue(StaleProperty, value); }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size a) =>
        new(double.IsInfinity(a.Width) ? 120 : a.Width, RailHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RailHeight;
        Themed.DrawCapsule(dc, Themed.Solid(RL.BrandTone.Carbon), new Rect(0, 0, w, h));
        var fill = Stale ? RL.BrandTone.Steel : RL.BrandTone.StatusColor(Utilization);
        // Always a sliver for a real but small share, never nothing
        var fw = Math.Max(h * 0.6, w * Math.Clamp(Utilization, 0, 100) / 100);
        Themed.DrawCapsule(dc, Themed.Solid(fill), new Rect(0, 0, fw, h));
        if (ShowsLimit) dc.DrawRectangle(Themed.Solid(RL.BrandTone.Signal), null, new Rect(w - 2, 0, 2, h));
    }
}

/// <summary>A tinted tile carrying the provider's mark, beside the provider's name (TrackBadge in Swift).</summary>
public class TrackBadge : ProviderTile
{
    public TrackBadge() { }
    public TrackBadge(string? provider, double size = 22) : base(provider, size) { }
}

/// <summary>The provider mark without the tile, in a caller-supplied tint. Unknown provider draws the RedLine mark.</summary>
public class TrackMark : FrameworkElement
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(string), typeof(TrackMark), new PropertyMetadata(null, (d, _) => ((TrackMark)d).Rebuild()));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(TrackMark), new PropertyMetadata(Brushes.Gray, (d, _) => ((TrackMark)d).Rebuild()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(TrackMark), new PropertyMetadata(12.0, (d, _) => ((TrackMark)d).Rebuild()));

    public string? Provider { get => (string?)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    FrameworkElement? child;

    public TrackMark() { Rebuild(); }
    public TrackMark(string? provider, Brush tint, double size = 12) { Provider = provider; Tint = tint; Size = size; Rebuild(); }

    void Rebuild()
    {
        if (child is not null) RemoveVisualChild(child);
        var mark = ProviderIdentity.Of(Provider)?.Mark;
        child = mark is { } m
            ? new ProviderGlyph(m, Size) { Foreground = Tint }
            : new RedlineMark(Size);
        AddVisualChild(child);
        InvalidateMeasure();
    }

    protected override int VisualChildrenCount => child is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => child!;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size a)
    {
        child?.Measure(new System.Windows.Size(Size, Size));
        return new(Size, Size);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size s)
    {
        child?.Arrange(new Rect(0, 0, Size, Size));
        return new(Size, Size);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this, () => true);
}
