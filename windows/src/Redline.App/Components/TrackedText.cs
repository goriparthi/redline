// A single line of text with letter spacing, which WPF's TextBlock cannot do.
// Used for the tracked upper-case labels (RL.Typography.Label with LabelTracking).
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Redline.App.Components;

public class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(RL.Typography.LabelTracking, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpperCaseProperty = DependencyProperty.Register(
        nameof(UpperCase), typeof(bool), typeof(TrackedText),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(RL.Typography.Mono, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(FontWeights.Medium, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public bool UpperCase { get => (bool)GetValue(UpperCaseProperty); set => SetValue(UpperCaseProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Applies a type style from RL.Typography.</summary>
    public TextStyle TypeStyle
    {
        set { FontFamily = value.Family; FontSize = value.Size; FontWeight = value.Weight; }
    }

    string Shown => UpperCase ? (Text ?? "").ToUpper(CultureInfo.CurrentCulture) : Text ?? "";

    FormattedText Format(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground,
        Themed.PixelsPerDip(this));

    protected override Size MeasureOverride(Size available)
    {
        var s = Shown;
        double w = 0;
        foreach (var ch in s) w += Format(ch.ToString()).WidthIncludingTrailingWhitespace + Tracking;
        if (s.Length > 0) w -= Tracking;
        var h = Format(s.Length > 0 ? s : " ").Height;
        return new Size(Math.Min(w, available.Width), h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0;
        var limit = RenderSize.Width;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        foreach (var ch in Shown)
        {
            var ft = Format(ch.ToString());
            if (x > limit) break;
            dc.DrawText(ft, new Point(x, 0));
            x += ft.WidthIncludingTrailingWhitespace + Tracking;
        }
        dc.Pop();
    }
}
