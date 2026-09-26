// One number with its label and, where there is something honest to add, a note under it.
// A tile never invents a figure: an absent value is drawn as a plain dash.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App.Components;

public class RLMetricTile : UserControl
{
    public static readonly DependencyProperty LabelProperty = Reg(nameof(Label), "");
    public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), "-");
    public static readonly DependencyProperty NoteProperty = Reg(nameof(Note), null);
    /// <summary>Tooltip, only when there is something to say, so an empty one never appears.</summary>
    public static readonly DependencyProperty HelpProperty = Reg(nameof(Help), null);
    /// <summary>Overrides the value's primary ink, e.g. money green or a status colour.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(RLMetricTile), new PropertyMetadata(null, (d, _) => ((RLMetricTile)d).Update()));

    static DependencyProperty Reg(string name, string? def) => DependencyProperty.Register(
        name, typeof(string), typeof(RLMetricTile), new PropertyMetadata(def, (d, _) => ((RLMetricTile)d).Update()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Note { get => (string?)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public string? Help { get => (string?)GetValue(HelpProperty); set => SetValue(HelpProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    readonly TrackedText label = new();
    readonly TextBlock value = new();
    readonly TextBlock note = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    public RLMetricTile()
    {
        label.TypeStyle = RL.Typography.Label;
        label.Bind(TrackedText.ForegroundProperty, RL.Ink.Muted);
        RL.Typography.Display.Apply(value);
        RL.Typography.MonoSmall.Apply(note);
        note.Bind(TextBlock.ForegroundProperty, RL.Ink.Muted);
        // Shrinks a long value rather than clipping it (minimumScaleFactor in Swift)
        var fit = new Viewbox { Child = value, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(new Border { Height = RL.Space.Xxs });
        stack.Children.Add(fit);
        stack.Children.Add(note);
        Content = stack;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
        IsTabStop = false;
        Update();
    }

    void Update()
    {
        label.Text = Label;
        value.Text = Value;
        if (Tint is null) value.Bind(TextBlock.ForegroundProperty, RL.Ink.Primary);
        else value.Foreground = Tint;
        note.Text = Note ?? "";
        note.Visibility = Note is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = string.IsNullOrEmpty(Help) ? null : Help;
        AutomationProperties.SetName(this, $"{Label}: {Value}" + (Note is null ? "" : $", {Note}"));
    }
}
