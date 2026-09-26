// A borderless action that still reads as a control: an icon, a word, and a hover tint.
// The look lives in Themes/Generic.xaml; Icon is a Segoe Fluent Icons glyph, e.g. "".
using System.Windows;
using System.Windows.Controls;

namespace Redline.App.Components;

public class RLInlineButton : Button
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RLInlineButton), new PropertyMetadata("", (d, e) => ((RLInlineButton)d).Describe()));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(RLInlineButton), new PropertyMetadata(null));
    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
        nameof(Help), typeof(string), typeof(RLInlineButton), new PropertyMetadata(null, (d, e) => ((RLInlineButton)d).Describe()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Help { get => (string?)GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    static RLInlineButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RLInlineButton), new FrameworkPropertyMetadata(typeof(RLInlineButton)));
    }

    public RLInlineButton() { }

    public RLInlineButton(string title, string? icon = null, string? help = null, Action? action = null)
    {
        Title = title; Icon = icon; Help = help;
        if (action is not null) Click += (_, _) => action();
    }

    void Describe()
    {
        ToolTip = string.IsNullOrEmpty(Help) ? null : Help;
        System.Windows.Automation.AutomationProperties.SetName(this, Title);
    }
}
