// The pieces every settings section is built from: a titled group, a toggle with its reason,
// an action with its reason, a labelled control and a stepper. Shared with the setup window.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Redline.App.Components;

namespace Redline.App.Settings;

internal static class SettingsUI
{
    static readonly Uri StylesUri = new("pack://application:,,,/RedLine;component/Settings/SettingsStyles.xaml");

    /// <summary>Merges the stock-control styles into a window's resources.</summary>
    public static void UseStyles(FrameworkElement root) =>
        root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });

    public static TextBlock Text(string s, TextStyle style, ColorToken ink, bool wrap = true, Thickness margin = default)
    {
        var t = new TextBlock { Text = s, Margin = margin, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        style.Apply(t);
        t.Bind(TextBlock.ForegroundProperty, ink);
        return t;
    }

    public static TextBlock Caption(string s, ColorToken? ink = null, Thickness margin = default) =>
        Text(s, RL.Typography.Caption, ink ?? RL.Ink.Muted, true, margin);

    public static Border Divider()
    {
        var b = new Border { Height = 1, SnapsToDevicePixels = true };
        b.Bind(Border.BackgroundProperty, RL.Stroke.Hairline);
        return b;
    }

    /// <summary>A titled group on the app's own card, so settings and the dashboard read as one product.</summary>
    /// <remarks>Children are spaced by RL.Space.Lg; a collapsed child takes its gap with it.</remarks>
    public static StackPanel Group(string title, string? note, params UIElement[] children)
    {
        var outer = new StackPanel();
        outer.Children.Add(new RLSectionHeader(title));
        if (note is not null) outer.Children.Add(Caption(note, margin: new Thickness(0, RL.Space.Md, 0, 0)));
        var inner = new StackPanel();
        foreach (var c in children) AddSpaced(inner, c);
        outer.Children.Add(new RLCard { Child = inner, Margin = new Thickness(0, RL.Space.Md, 0, 0) });
        return outer;
    }

    public static void AddSpaced(Panel panel, UIElement child, double gap = RL.Space.Lg)
    {
        if (panel.Children.Count > 0 && child is FrameworkElement fe)
            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
        panel.Children.Add(child);
    }

    /// <summary>Label then control, as SwiftUI's LabeledContent lays out outside a Form.</summary>
    public static FrameworkElement Labeled(string label, FrameworkElement content, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var l = Text(label, RL.Typography.Body, RL.Ink.Primary, wrap: false);
        l.VerticalAlignment = VerticalAlignment.Center;
        l.MinWidth = 100;
        content.VerticalAlignment = VerticalAlignment.Center;
        content.Margin = new Thickness(RL.Space.Lg, 0, 0, 0);
        if (help is not null) content.ToolTip = help;
        AutomationProperties.SetName(content, label);
        row.Children.Add(l);
        row.Children.Add(content);
        return row;
    }

    /// <summary>A picker over (value, label) pairs. Raises picked with the chosen value only on a user change.</summary>
    public static ComboBox Picker<T>(IEnumerable<(T Value, string Label)> options, double width, Action<T> picked, out Action<T> select)
    {
        var box = new ComboBox { Width = width };
        foreach (var (v, label) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        var syncing = false;
        box.SelectionChanged += (_, _) =>
        {
            if (syncing || box.SelectedItem is not ComboBoxItem { Tag: T v }) return;
            picked(v);
        };
        select = value =>
        {
            syncing = true;
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));
            syncing = false;
        };
        return box;
    }
}

/// <summary>A labelled toggle with its explanation underneath, so the reason sits with the preference rather than in a tooltip.</summary>
internal sealed class SettingRow : StackPanel
{
    readonly CheckBox box;
    readonly TextBlock explanationText;
    bool syncing;
    string explanation;

    public event Action<bool>? Toggled;

    public SettingRow(string title, string explanation)
    {
        this.explanation = explanation;
        box = new CheckBox { Content = SettingsUI.Text(title, RL.Typography.Body, RL.Ink.Primary, wrap: false), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(box, title);
        box.Checked += (_, _) => { if (!syncing) Toggled?.Invoke(true); };
        box.Unchecked += (_, _) => { if (!syncing) Toggled?.Invoke(false); };
        explanationText = SettingsUI.Caption(explanation, margin: new Thickness(22, RL.Space.Xxs, 0, 0));
        Children.Add(box);
        Children.Add(explanationText);
        Set(false);
    }

    /// <summary>Settles the control on the stored value. A disabled row shows why, when it has a note.</summary>
    public void Set(bool isOn, bool disabled = false, string? disabledNote = null, string? explanation = null)
    {
        if (explanation is not null) this.explanation = explanation;
        syncing = true;
        box.IsChecked = isOn;
        syncing = false;
        box.IsEnabled = !disabled;
        box.ToolTip = this.explanation;
        AutomationProperties.SetHelpText(box, this.explanation);
        explanationText.Text = disabled ? (disabledNote ?? this.explanation) : this.explanation;
    }
}

/// <summary>An action that opens something outside settings, visually quieter than a toggle.</summary>
internal sealed class SettingAction : StackPanel
{
    readonly Button button;
    readonly TextBlock explanationText;

    public SettingAction(string title, string explanation, Action action, bool destructive = false)
    {
        button = new Button();
        if (destructive) button.Bind(Control.ForegroundProperty, RL.State.Error);
        button.Click += (_, _) => action();
        explanationText = SettingsUI.Caption(explanation, margin: new Thickness(0, RL.Space.Xs, 0, 0));
        Children.Add(button);
        Children.Add(explanationText);
        Set(title, explanation);
    }

    public void Set(string title, string explanation)
    {
        button.Content = title;
        button.ToolTip = explanation;
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, explanation);
        explanationText.Text = explanation;
    }
}

/// <summary>A value in monospace with up and down arrows, SwiftUI's Stepper. Raises Changed only on a user step.</summary>
internal sealed class Stepper : StackPanel
{
    readonly TextBlock value;
    readonly RepeatButton up;
    readonly RepeatButton down;
    readonly double min, max, step;
    readonly Func<double, string> format;
    double current;

    public event Action<double>? Changed;

    public Stepper(double min, double max, double step, Func<double, string> format)
    {
        this.min = min; this.max = max; this.step = step; this.format = format;
        Orientation = Orientation.Horizontal;
        Focusable = true;
        value = SettingsUI.Text("", RL.Typography.MonoBody, RL.Ink.Primary, wrap: false);
        value.VerticalAlignment = VerticalAlignment.Center;
        var arrows = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(RL.Space.Md, 0, 0, 0), Padding = new Thickness(1) };
        arrows.Bind(Border.BorderBrushProperty, RL.Stroke.Border);
        arrows.Bind(Border.BackgroundProperty, RL.Surface.RaisedHover);
        var stack = new StackPanel();
        up = Arrow("", +1);
        down = Arrow("", -1);
        stack.Children.Add(up);
        stack.Children.Add(down);
        arrows.Child = stack;
        Children.Add(value);
        Children.Add(arrows);
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Up) { Step(+1); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Down) { Step(-1); e.Handled = true; }
        };
    }

    RepeatButton Arrow(string glyph, int dir)
    {
        var b = new RepeatButton { Content = glyph, Delay = 400, Interval = 120 };
        b.SetResourceReference(StyleProperty, "RL.Settings.StepButton");
        b.Click += (_, _) => Step(dir);
        return b;
    }

    void Step(int dir)
    {
        var next = Math.Clamp(current + dir * step, min, max);
        if (next == current) return;
        Set(next);
        Changed?.Invoke(next);
    }

    public void Set(double v)
    {
        current = v;
        value.Text = format(v);
        up.IsEnabled = v < max;
        down.IsEnabled = v > min;
        AutomationProperties.SetName(this, value.Text);
    }
}
