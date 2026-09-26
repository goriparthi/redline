// NSAlert's Windows counterpart: a title, an explanation, up to three buttons and an optional
// checkbox, themed like the rest of the app. The first button is the default.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Redline.App.Components;

namespace Redline.App.Tray;

public sealed class AlertDialog : Window
{
    public int Chosen { get; private set; } = -1;
    readonly CheckBox? check;
    public bool Checked => check?.IsChecked == true;

    AlertDialog(string title, string message, IReadOnlyList<string> buttons, string? checkbox)
    {
        Title = "RedLine";
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        this.Bind(BackgroundProperty, RL.Surface.Ground);
        var icon = new RedlineMarkAdaptive(40) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, RL.Space.Xl, 0) };
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, RL.Space.Md) };
        RL.Typography.Heading.Apply(heading);
        heading.Bind(TextBlock.ForegroundProperty, RL.Ink.Primary);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        RL.Typography.Body.Apply(body);
        body.Bind(TextBlock.ForegroundProperty, RL.Ink.Secondary);
        var text = new StackPanel();
        text.Children.Add(heading);
        text.Children.Add(body);
        if (checkbox is not null)
        {
            check = new CheckBox { Content = checkbox, Margin = new Thickness(0, RL.Space.Lg, 0, 0) };
            check.Bind(ForegroundProperty, RL.Ink.Primary);
            text.Children.Add(check);
        }
        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, RL.Space.Xl, 0, 0) };
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i], MinWidth = 88, Padding = new Thickness(RL.Space.Lg, RL.Space.Xs, RL.Space.Lg, RL.Space.Xs),
                Margin = new Thickness(RL.Space.Md, 0, 0, 0), IsDefault = i == 0,
                IsCancel = buttons.Count > 1 && i == buttons.Count - 1,
            };
            b.Click += (_, _) => { Chosen = index; Close(); };
            row.Children.Add(b);
        }
        var root = new StackPanel { Margin = new Thickness(RL.Space.Xxl) };
        root.Children.Add(top);
        root.Children.Add(row);
        Content = root;
        ThemeManager.ApplyTitleBar(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Shows the alert modally and returns the index of the button pressed, or -1 when closed.</summary>
    public static int Show(string title, string message, params string[] buttons) =>
        Run(title, message, buttons.Length == 0 ? new[] { "OK" } : buttons, null).Index;

    public static (int Index, bool Checked) ShowWithCheckbox(string title, string message, string checkbox, params string[] buttons) =>
        Run(title, message, buttons, checkbox);

    static (int Index, bool Checked) Run(string title, string message, IReadOnlyList<string> buttons, string? checkbox)
    {
        var d = new AlertDialog(title, message, buttons, checkbox);
        d.Loaded += (_, _) => d.Activate();
        d.ShowDialog();
        return (d.Chosen, d.Checked);
    }
}
