// The tray dropdown: a borderless window on the brand's dark ground, anchored to the taskbar
// corner the icon sits in, closing when it loses focus as a menu does.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Redline.App.Tray;

public sealed class FlyoutWindow : Window
{
    static readonly Color Ground = RL.Surface.Ground.Dark;
    static readonly Color Edge = RL.Stroke.Border.Dark;
    static readonly Color Hover = RL.Stroke.Border.Dark;
    static readonly Color Hairline = RL.Stroke.Hairline.Dark;

    readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly List<Popup> openSubmenus = new();
    IReadOnlyList<MenuEntry> entries = Array.Empty<MenuEntry>();
    readonly List<(Key Key, ModifierKeys Mods, MenuAction Action)> shortcuts = new();
    DispatcherTimer? submenuDelay;

    /// <summary>When the flyout last hid, so the click that dismissed it does not reopen it.</summary>
    public DateTime LastHidden { get; private set; } = DateTime.MinValue;

    /// <summary>True while a nested list is showing; rebuilding then would collapse it.</summary>
    public bool HasOpenSubmenu => openSubmenus.Any(p => p.IsOpen);

    public event EventHandler? Dismissed;

    public FlyoutWindow()
    {
        Title = "RedLine";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = new Border
        {
            Background = new SolidColorBrush(Ground),
            BorderBrush = new SolidColorBrush(Edge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RL.Radius.Control + 1),
            Padding = new Thickness(0, RL.Space.Sm, 0, RL.Space.Sm),
            Child = scroller,
            MinWidth = 300,
        };
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnKey;
    }

    /// <summary>Shows the entries anchored at a screen point in physical pixels (the click).</summary>
    public void ShowAt(IReadOnlyList<MenuEntry> items, System.Drawing.Point anchor)
    {
        Replace(items);
        Left = -32000;
        Top = -32000;
        Opacity = 0;
        Show();
        UpdateLayout();
        Place(anchor);
        Opacity = 1;
        Activate();
        Focus();
    }

    /// <summary>Swaps the content in place. Callers defer this while a submenu is open.</summary>
    public void Replace(IReadOnlyList<MenuEntry> items)
    {
        entries = items;
        CloseSubmenus();
        shortcuts.Clear();
        scroller.Content = BuildPanel(items, top: true);
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        CloseSubmenus();
        Hide();
        LastHidden = DateTime.UtcNow;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    void Place(System.Drawing.Point anchor)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        const double gap = 8;
        // Never taller than the work area; the scroller takes over past that
        MaxHeight = Math.Max(200, wa.Height / sy - gap * 2);
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        double waL = wa.Left / sx, waT = wa.Top / sy, waR = wa.Right / sx, waB = wa.Bottom / sy;
        double ax = anchor.X / sx, ay = anchor.Y / sy;
        double left, top;
        // The taskbar is whichever edge the work area gave up
        if (wa.Top > bounds.Top) { top = waT + gap; left = ax - w / 2; }
        else if (wa.Left > bounds.Left) { left = waL + gap; top = ay - h / 2; }
        else if (wa.Right < bounds.Right) { left = waR - w - gap; top = ay - h / 2; }
        else { top = waB - h - gap; left = ax - w / 2; }
        Left = Math.Clamp(left, waL + gap, Math.Max(waL + gap, waR - w - gap));
        Top = Math.Clamp(top, waT + gap, Math.Max(waT + gap, waB - h - gap));
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        foreach (var (k, mods, action) in shortcuts)
        {
            if (k != key || Keyboard.Modifiers != mods || !action.Enabled || action.OnClick is null) continue;
            e.Handled = true;
            Invoke(action);
            return;
        }
    }

    void Invoke(MenuAction action)
    {
        Dismiss();
        // After the flyout is gone, so a dialog the action opens owns the foreground
        if (action.OnClick is { } run) Dispatcher.BeginInvoke(DispatcherPriority.Background, run);
    }

    void CloseSubmenus()
    {
        submenuDelay?.Stop();
        foreach (var p in openSubmenus) p.IsOpen = false;
        openSubmenus.Clear();
        submenuOf.Clear();
    }

    StackPanel BuildPanel(IReadOnlyList<MenuEntry> items, bool top)
    {
        var panel = new StackPanel();
        foreach (var entry in items)
        {
            FrameworkElement row = entry switch
            {
                MenuSeparator => new Border
                {
                    Height = 1, Background = new SolidColorBrush(Hairline),
                    Margin = new Thickness(RL.Space.Md, RL.Space.Xs, RL.Space.Md, RL.Space.Xs),
                },
                MenuInfo info => InfoRow(info),
                MenuAction action => ActionRow(action, top),
                MenuSubmenu sub => SubmenuRow(sub, panel),
                _ => new Border(),
            };
            if (entry is not MenuSubmenu)
                row.MouseEnter += (_, _) => CloseSubmenusOf(panel);
            panel.Children.Add(row);
        }
        return panel;
    }

    // A submenu belongs to the panel it was opened from; hovering a sibling row closes it
    readonly Dictionary<Panel, Popup> submenuOf = new();

    void CloseSubmenusOf(Panel panel)
    {
        submenuDelay?.Stop();
        if (submenuOf.TryGetValue(panel, out var p)) { p.IsOpen = false; submenuOf.Remove(panel); }
    }

    static FrameworkElement InfoRow(MenuInfo info) => new Border
    {
        Padding = new Thickness(RL.Space.Xl, 3, RL.Space.Xl, 3),
        // Same inset as the action rows, so text lines up as it does in MenuRowView
        Margin = new Thickness(RL.Space.Xs, 0, RL.Space.Xs, 0),
        Background = Brushes.Transparent,
        Child = Text(info.Runs, info.Mono, MenuInk.Primary),
    };

    FrameworkElement ActionRow(MenuAction action, bool top)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ink = action.Enabled ? MenuInk.Primary : MenuInk.Tertiary;
        if (action.Icon is { } icon)
        {
            var i = icon();
            i.Margin = new Thickness(0, 0, RL.Space.Sm, 0);
            i.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(i);
        }
        var text = Text(action.Rich ?? new[] { new MenuRun(action.Title, ink) }, false, ink);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (action.Shortcut is { } sc)
        {
            var hint = new TextBlock
            {
                Text = sc, Foreground = new SolidColorBrush(MenuInk.Tertiary), FontFamily = RL.Typography.UI,
                FontSize = 12, Margin = new Thickness(RL.Space.Xxl, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
            if (top && ParseShortcut(sc) is { } parsed) shortcuts.Add((parsed.Key, parsed.Mods, action));
        }
        var row = new Border
        {
            Padding = new Thickness(RL.Space.Xl + action.Indent * RL.Space.Xl, 4, RL.Space.Xl, 4),
            Margin = new Thickness(RL.Space.Xs, 0, RL.Space.Xs, 0),
            CornerRadius = new CornerRadius(RL.Radius.Chip),
            Background = Brushes.Transparent,
            Child = grid,
            Cursor = action.Enabled ? Cursors.Hand : Cursors.Arrow,
        };
        if (action.Tooltip is { } tip) row.ToolTip = Tip(tip);
        if (action.Enabled && action.OnClick is not null)
        {
            var hover = new SolidColorBrush(Hover);
            row.MouseEnter += (_, _) => row.Background = hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(action); };
        }
        return row;
    }

    FrameworkElement SubmenuRow(MenuSubmenu sub, Panel parent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Text(sub.Title, false, MenuInk.Primary));
        var chevron = new TextBlock
        {
            Text = "", FontFamily = RL.Typography.Icons, FontSize = 10,
            Foreground = new SolidColorBrush(MenuInk.Secondary), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(RL.Space.Xl, 0, 0, 0),
        };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var row = new Border
        {
            Padding = new Thickness(RL.Space.Xl, 4, RL.Space.Md, 4),
            Margin = new Thickness(RL.Space.Xs, 0, RL.Space.Xs, 0),
            CornerRadius = new CornerRadius(RL.Radius.Chip),
            Background = Brushes.Transparent,
            Child = grid,
        };
        if (sub.Tooltip is { } tip) row.ToolTip = Tip(tip);
        var hover = new SolidColorBrush(Hover);
        void Open()
        {
            if (submenuOf.TryGetValue(parent, out var existing))
            {
                if (existing.PlacementTarget == row && existing.IsOpen) return;
                existing.IsOpen = false;
            }
            var content = BuildPanel(sub.Items(), top: false);
            var popup = new Popup
            {
                PlacementTarget = row, Placement = PlacementMode.Right, HorizontalOffset = 2, VerticalOffset = -RL.Space.Sm,
                AllowsTransparency = true, StaysOpen = true,
                Child = new Border
                {
                    Background = new SolidColorBrush(Ground), BorderBrush = new SolidColorBrush(Edge),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(RL.Radius.Control + 1),
                    Padding = new Thickness(0, RL.Space.Sm, 0, RL.Space.Sm), Child = content, MinWidth = 200,
                },
            };
            TextOptions.SetTextFormattingMode(popup.Child, TextFormattingMode.Display);
            popup.Closed += (_, _) => row.Background = Brushes.Transparent;
            submenuOf[parent] = popup;
            openSubmenus.Add(popup);
            popup.IsOpen = true;
            row.Background = hover;
        }
        row.MouseEnter += (_, _) =>
        {
            row.Background = hover;
            submenuDelay?.Stop();
            submenuDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Input, (_, _) =>
            {
                submenuDelay?.Stop();
                if (row.IsMouseOver) Open();
            }, Dispatcher);
            submenuDelay.Start();
        };
        row.MouseLeave += (_, _) =>
        {
            if (!(submenuOf.TryGetValue(parent, out var p) && p.PlacementTarget == row && p.IsOpen))
                row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(); };
        return row;
    }

    static TextBlock Text(IReadOnlyList<MenuRun> runs, bool mono, Color fallback)
    {
        var t = new TextBlock
        {
            FontFamily = mono ? RL.Typography.Mono : RL.Typography.UI,
            FontSize = mono ? 12 : 13,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(fallback),
        };
        foreach (var run in runs)
        {
            if (run.Glyph is { } glyph)
            {
                var g = glyph();
                g.VerticalAlignment = VerticalAlignment.Center;
                t.Inlines.Add(new InlineUIContainer(g) { BaselineAlignment = BaselineAlignment.Center });
                continue;
            }
            var r = new Run(run.Text);
            if (run.Color is { } c) r.Foreground = new SolidColorBrush(c);
            if (run.Bold) r.FontWeight = FontWeights.SemiBold;
            t.Inlines.Add(r);
        }
        return t;
    }

    static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 },
        Background = new SolidColorBrush(RL.Surface.Overlay.Dark),
        Foreground = new SolidColorBrush(MenuInk.Primary),
        BorderBrush = new SolidColorBrush(Edge),
    };

    /// <summary>"Ctrl+D" and friends, the way the rows print them.</summary>
    static (Key Key, ModifierKeys Mods)? ParseShortcut(string text)
    {
        var mods = ModifierKeys.None;
        var parts = text.Split('+');
        foreach (var p in parts[..^1])
            mods |= p switch { "Ctrl" => ModifierKeys.Control, "Alt" => ModifierKeys.Alt, "Shift" => ModifierKeys.Shift, _ => ModifierKeys.None };
        var last = parts[^1];
        Key? key = last switch { "," => Key.OemComma, _ => Enum.TryParse<Key>(last, true, out var k) ? k : null };
        return key is { } kk ? (kk, mods) : null;
    }
}
