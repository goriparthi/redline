// A usage rail that ends at its limit: the red line at the right edge is the product's signature,
// and the fill's colour is the status. The fill animates unless Windows animations are off.
using System.Windows;
using Redline.Core;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Redline.App.Components;

public class RLUsageRail : FrameworkElement
{
    public static readonly DependencyProperty UtilizationProperty = DependencyProperty.Register(
        nameof(Utilization), typeof(double), typeof(RLUsageRail), new PropertyMetadata(0.0, OnUtilization));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(RLStatus), typeof(RLUsageRail),
        new PropertyMetadata(new RLStatus(RLStatusKind.Unknown), (d, _) => ((RLUsageRail)d).Rebind()));
    public static readonly DependencyProperty RailHeightProperty = DependencyProperty.Register(
        nameof(RailHeight), typeof(double), typeof(RLUsageRail),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowsLimitProperty = DependencyProperty.Register(
        nameof(ShowsLimit), typeof(bool), typeof(RLUsageRail), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Where the window's clock has got to, 0 to 1. Level with the fill means spending at the refill rate.</summary>
    public static readonly DependencyProperty ElapsedProperty = DependencyProperty.Register(
        nameof(Elapsed), typeof(double?), typeof(RLUsageRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((RLUsageRail)d).Describe()));
    /// <summary>Overrides the status colour on the fill, e.g. a provider accent.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(RLUsageRail), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(RLUsageRail), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    static readonly DependencyProperty StatusBrushProperty = BrushProp("StatusBrush");
    static readonly DependencyProperty SunkenProperty = BrushProp("SunkenBrush");
    static readonly DependencyProperty SignalProperty = BrushProp("SignalBrush");
    static readonly DependencyProperty InkProperty = BrushProp("InkBrush");

    static DependencyProperty BrushProp(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(RLUsageRail), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Utilization { get => (double)GetValue(UtilizationProperty); set => SetValue(UtilizationProperty, value); }
    public RLStatus Status { get => (RLStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public double RailHeight { get => (double)GetValue(RailHeightProperty); set => SetValue(RailHeightProperty, value); }
    public bool ShowsLimit { get => (bool)GetValue(ShowsLimitProperty); set => SetValue(ShowsLimitProperty, value); }
    public double? Elapsed { get => (double?)GetValue(ElapsedProperty); set => SetValue(ElapsedProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public RLUsageRail()
    {
        this.Bind(SunkenProperty, RL.Surface.Sunken);
        this.Bind(SignalProperty, RL.Brandmark.Signal);
        this.Bind(InkProperty, RL.Ink.Primary);
        Rebind();
    }

    public RLUsageRail(double utilization, RLStatus status, double height = 8, bool showsLimit = true, double? elapsed = null, Brush? tint = null) : this()
    {
        Status = status; RailHeight = height; ShowsLimit = showsLimit; Elapsed = elapsed; Tint = tint;
        Utilization = utilization;
    }

    double Clamped => Math.Clamp(Utilization, 0, 100);

    static void OnUtilization(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var rail = (RLUsageRail)d;
        var anim = new DoubleAnimation(rail.Clamped, RL.Motion.For(RL.Motion.Value)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        rail.BeginAnimation(ShownProperty, anim);
        rail.Describe();
    }

    void Rebind()
    {
        this.Bind(StatusBrushProperty, Status.Color());
        Describe();
    }

    void Describe()
    {
        AutomationProperties.SetName(this, $"{(int)Math.Round(Clamped)} percent used, {Status.Phrase}");
        ToolTip = Elapsed is double el && el > 0 && el < 1
            ? $"where the clock is: {(int)Math.Round(el * 100)}% of this window has passed"
            : null;
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 120 : a.Width, RailHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RailHeight;
        Themed.DrawCapsule(dc, (Brush)GetValue(SunkenProperty), new Rect(0, 0, w, h));
        // Always a sliver for a real but small share, never nothing
        var fw = Math.Max(h * 0.6, w * (double)GetValue(ShownProperty) / 100);
        Themed.DrawCapsule(dc, Tint ?? (Brush)GetValue(StatusBrushProperty), new Rect(0, 0, fw, h));
        if (ShowsLimit) dc.DrawRectangle((Brush)GetValue(SignalProperty), null, new Rect(w - 2, 0, 2, h));
        if (Elapsed is double el && el > 0 && el < 1)
            dc.DrawRectangle(Themed.Tint((Brush)GetValue(InkProperty), 0.6), null, new Rect(w * el, 0, 1, h));
    }
}
