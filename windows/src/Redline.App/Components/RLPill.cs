// A tracked upper-case tag: a finding's kind, a provenance label, a data source.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App.Components;

public class RLPill : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RLPill), new PropertyMetadata("", (d, _) => ((RLPill)d).Update()));
    /// <summary>Defaults to muted ink; set to a state or accent brush to colour the tag.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(RLPill), new PropertyMetadata(null, (d, _) => ((RLPill)d).Update()));
    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
        nameof(Help), typeof(string), typeof(RLPill), new PropertyMetadata(null, (d, _) => ((RLPill)d).Update()));
    static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        "Muted", typeof(Brush), typeof(RLPill), new PropertyMetadata(null, (d, _) => ((RLPill)d).Update()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public string? Help { get => (string?)GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    readonly TrackedText text = new() { FontFamily = RL.Typography.Mono, FontSize = 9, FontWeight = FontWeights.Bold, Tracking = 1.0 };

    public RLPill()
    {
        Child = text;
        Padding = new Thickness(RL.Space.Sm, RL.Space.Xxs, RL.Space.Sm, RL.Space.Xxs);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SizeChanged += (_, e) => CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        this.Bind(MutedProperty, RL.Ink.Muted);
    }

    public RLPill(string text, Brush? tint = null, string? help = null) : this()
    {
        Text = text; Tint = tint; Help = help;
    }

    void Update()
    {
        var tint = Tint ?? (Brush?)GetValue(MutedProperty) ?? Brushes.Gray;
        text.Text = Text;
        text.Foreground = tint;
        Background = Themed.Tint(tint, 0.14);
        ToolTip = string.IsNullOrEmpty(Help) ? null : Help;
    }
}
