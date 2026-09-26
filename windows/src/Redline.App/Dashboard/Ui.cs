// Small builders the dashboard repeats: themed text, dividers, spacing, a width-aware layout
// switch (ViewThatFits) and the date phrasing Swift's formatters give.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Redline.App.Components;

namespace Redline.App.Dashboard;

internal static class Ui
{
    public static TextBlock Text(string s, TextStyle style, ColorToken ink, Thickness margin = default, bool wrap = false)
    {
        var t = new TextBlock
        {
            Text = s, Margin = margin, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        style.Apply(t);
        t.Bind(TextBlock.ForegroundProperty, ink);
        return t;
    }

    /// <summary>Text in an explicit brush, e.g. a status colour or a tinted ink.</summary>
    public static TextBlock Text(string s, FontFamily family, double size, Brush ink, FontWeight? weight = null,
                                 Thickness margin = default, bool wrap = false)
    {
        return new TextBlock
        {
            Text = s, FontFamily = family, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            Foreground = ink, Margin = margin, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
    }

    public static Border Divider(Thickness margin = default)
    {
        var b = new Border { Height = 1, Margin = margin, SnapsToDevicePixels = true };
        b.Bind(Border.BackgroundProperty, RL.Stroke.Hairline);
        return b;
    }

    public static Border VDivider(double height)
    {
        var b = new Border { Width = 1, Height = height, SnapsToDevicePixels = true, VerticalAlignment = VerticalAlignment.Top };
        b.Bind(Border.BackgroundProperty, RL.Stroke.Hairline);
        return b;
    }

    /// <summary>A vertical stack with a fixed gap between visible children (VStack(spacing:)).</summary>
    public static StackPanel VStack(double spacing, params UIElement?[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) Add(s, c, spacing);
        return s;
    }

    public static void Add(StackPanel s, UIElement? child, double spacing)
    {
        if (child is null) return;
        if (s.Children.Count > 0 && child is FrameworkElement fe)
        {
            var m = fe.Margin;
            fe.Margin = s.Orientation == Orientation.Vertical
                ? new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom)
                : new Thickness(m.Left + spacing, m.Top, m.Right, m.Bottom);
        }
        s.Children.Add(child);
    }

    public static StackPanel HStack(double spacing, params UIElement?[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) Add(s, c, spacing);
        return s;
    }

    /// <summary>A row whose trailing items sit on the right edge (HStack with a Spacer).</summary>
    /// <remarks>The last leading item takes the slack, so wrapping text there wraps at the trailing items.</remarks>
    public static Grid Row(IEnumerable<UIElement?> leading, IEnumerable<UIElement?> trailing, double spacing)
    {
        var g = new Grid();
        var lead = leading.OfType<UIElement>().ToList();
        var trail = trailing.OfType<UIElement>().ToList();
        if (lead.Count == 0) lead.Add(new Border());
        var col = 0;
        void Place(UIElement e, GridLength width, bool first)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            if (!first && e is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left + spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            Grid.SetColumn(e, col++);
            g.Children.Add(e);
        }
        for (int i = 0; i < lead.Count; i++)
        {
            var last = i == lead.Count - 1;
            Place(lead[i], last ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, i == 0);
        }
        foreach (var t in trail) Place(t, GridLength.Auto, false);
        return g;
    }

    public static TextBlock Icon(string glyph, double size, ColorToken ink)
    {
        var t = new TextBlock { Text = glyph, FontFamily = RL.Typography.Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.Bind(TextBlock.ForegroundProperty, ink);
        return t;
    }

    /// <summary>A borderless glyph button (buttonStyle(.borderless)): secondary ink, primary on hover.</summary>
    public static Button IconButton(string glyph, string help, Action action, double size = 13)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, glyph);
        text.SetValue(TextBlock.FontFamilyProperty, RL.Typography.Icons);
        text.SetValue(TextBlock.FontSizeProperty, size);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.ForegroundProperty, new System.Windows.TemplateBindingExtension(Control.ForegroundProperty));
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bg.SetValue(Border.PaddingProperty, new Thickness(3));
        bg.AppendChild(text);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(RL.Ink.Primary.BrushKey)));
        template.Triggers.Add(hover);
        var b = new Button { Template = template, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = help, VerticalAlignment = VerticalAlignment.Center };
        b.Bind(Control.ForegroundProperty, RL.Ink.Secondary);
        System.Windows.Automation.AutomationProperties.SetName(b, help);
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>A plain bordered push button, as a default macOS Button reads beside a row.</summary>
    public static Button PushButton(string title, string? help, bool enabled, Action action)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, title);
        text.SetValue(TextBlock.FontFamilyProperty, RL.Typography.UI);
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension(RL.Ink.Primary.BrushKey));
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(RL.Surface.Sunken.BrushKey));
        bg.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension(RL.Stroke.Border.BrushKey));
        bg.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(RL.Radius.Chip));
        bg.SetValue(Border.PaddingProperty, new Thickness(10, 3, 10, 4));
        bg.AppendChild(text);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(RL.Stroke.BorderStrong.BrushKey), "Bg"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        var b = new Button { Template = template, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
        if (help is not null)
        {
            b.ToolTip = help;
            ToolTipService.SetShowOnDisabled(b, true);
        }
        b.Click += (_, _) => action();
        return b;
    }

    public static System.Windows.Shapes.Ellipse Dot(double size, Brush fill) => new()
    {
        Width = size, Height = size, Fill = fill, VerticalAlignment = VerticalAlignment.Center,
    };

    static DateTime Local(DateTimeOffset d) => d.ToLocalTime().DateTime;
    static CultureInfo C => CultureInfo.CurrentCulture;

    /// <summary>formatted(date: .omitted, time: .shortened): "2:38 PM".</summary>
    public static string Time(DateTimeOffset d) => Local(d).ToString("t", C);
    /// <summary>formatted(date: .omitted, time: .standard): "2:38:42 PM".</summary>
    public static string TimeWithSeconds(DateTimeOffset d) => Local(d).ToString("T", C);
    /// <summary>formatted(date: .abbreviated, time: .shortened): "Aug 25, 2026 at 1:00 AM".</summary>
    public static string DateTime(DateTimeOffset d) => $"{Local(d).ToString("MMM d, yyyy", C)} at {Time(d)}";
    /// <summary>Weekday, month and day: "Mon, Aug 25".</summary>
    public static string Day(DateTimeOffset d) => Local(d).ToString("ddd, MMM d", C);
    /// <summary>Month and day for a daily axis: "Aug 25".</summary>
    public static string MonthDay(DateTimeOffset d) => Local(d).ToString("MMM d", C);

    static bool TwentyFour => !C.DateTimeFormat.ShortTimePattern.Contains('h');

    /// <summary>An hour for the hourly axis: "3 PM", or "15" where the clock is 24 hour.</summary>
    public static string Hour(DateTimeOffset d) => Local(d).ToString(TwentyFour ? "HH" : "h tt", C);

    /// <summary>formatted(.relative(presentation: .named)): "in 6 days", "tomorrow", "in 2 hours".</summary>
    public static string Relative(DateTimeOffset d, DateTimeOffset? now = null)
    {
        var span = d - (now ?? DateTimeOffset.UtcNow);
        var future = span >= TimeSpan.Zero;
        var a = span.Duration();
        string Phrase(double n, string unit) =>
            future ? $"in {n:0} {unit}{(Math.Round(n) == 1 ? "" : "s")}" : $"{n:0} {unit}{(Math.Round(n) == 1 ? "" : "s")} ago";
        if (a.TotalSeconds < 60) return "now";
        if (a.TotalMinutes < 60) return Phrase(Math.Floor(a.TotalMinutes), "minute");
        if (a.TotalHours < 24) return Phrase(Math.Floor(a.TotalHours), "hour");
        var days = Math.Floor(a.TotalDays);
        if (days == 1) return future ? "tomorrow" : "yesterday";
        if (days < 7) return Phrase(days, "day");
        return Phrase(Math.Floor(days / 7), "week");
    }

    /// <summary>ByteCountFormatter's file style: "12.3 MB", powers of 1000.</summary>
    public static string Bytes(long n)
    {
        var c = CultureInfo.InvariantCulture;
        if (n >= 1_000_000_000) return (n / 1e9).ToString("0.0", c) + " GB";
        if (n >= 1_000_000) return (n / 1e6).ToString("0.0", c) + " MB";
        if (n >= 1_000) return (n / 1e3).ToString("0", c) + " KB";
        return n + " bytes";
    }

    /// <summary>"12%" from a percentage, rounded half away from zero like Swift's rounded().</summary>
    public static string Pct(double v) => $"{(int)Math.Round(v, MidpointRounding.AwayFromZero)}%";
    public static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// <summary>ViewThatFits(in: .horizontal): the wide layout when it fits, otherwise the narrow one.</summary>
/// <remarks>Both are kept as children; the unused one is arranged at zero size and clipped away.</remarks>
internal sealed class ThatFits : Panel
{
    readonly FrameworkElement wide;
    readonly FrameworkElement narrow;
    bool useWide = true;

    public ThatFits(FrameworkElement wide, FrameworkElement narrow)
    {
        this.wide = new Border { Child = wide, ClipToBounds = true };
        this.narrow = new Border { Child = narrow, ClipToBounds = true };
        Children.Add(this.wide);
        Children.Add(this.narrow);
    }

    protected override Size MeasureOverride(Size available)
    {
        wide.Measure(new Size(double.PositiveInfinity, available.Height));
        useWide = wide.DesiredSize.Width <= available.Width + 0.5;
        var chosen = useWide ? wide : narrow;
        var other = useWide ? narrow : wide;
        chosen.Measure(available);
        other.Measure(new Size(0, 0));
        other.Visibility = Visibility.Visible;
        return new Size(Math.Min(chosen.DesiredSize.Width, available.Width), chosen.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var chosen = useWide ? wide : narrow;
        var other = useWide ? narrow : wide;
        chosen.Arrange(new Rect(final));
        other.Arrange(new Rect(0, 0, 0, 0));
        other.IsHitTestVisible = false;
        chosen.IsHitTestVisible = true;
        return final;
    }
}
