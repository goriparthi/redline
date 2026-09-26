// DEBUG only: every component in both themes, side by side (`dotnet run -- --gallery`).
// `--snapshot <file.png>` renders the window to a PNG and exits, for checking layout without looking.
#if DEBUG
using System.IO;
using System.Windows;
using Redline.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Redline.App.Components;

namespace Redline.App.Gallery;

public sealed class GalleryWindow : Window
{
    readonly Grid columns = new();

    public GalleryWindow()
    {
        Title = "RedLine component gallery";
        Width = 1180;
        Height = 1000;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Bind(BackgroundProperty, RL.Surface.Ground);

        var mode = new RLSegmented(
            [new RLSegment(ThemeMode.Auto, "Auto", "Follow Windows"), new RLSegment(ThemeMode.Light, "Light"), new RLSegment(ThemeMode.Dark, "Dark")],
            ThemeManager.Mode, width: 56);
        mode.Selected += (_, v) => { if (v is ThemeMode m) ThemeManager.Mode = m; };

        var header = new DockPanel { Margin = new Thickness(RL.Space.Xl, RL.Space.Lg, RL.Space.Xl, RL.Space.Lg) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new RedlineMarkAdaptive(26) { VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Text("Component gallery", RL.Typography.Heading, RL.Ink.Primary, new Thickness(RL.Space.Md, 0, 0, 0)));
        DockPanel.SetDock(mode, Dock.Right);
        header.Children.Add(mode);
        header.Children.Add(brand);

        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var dark = Column(Theme.Dark);
        var light = Column(Theme.Light);
        Grid.SetColumn(light, 1);
        columns.Children.Add(dark);
        columns.Children.Add(light);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Renders the window to a PNG once laid out, then closes it.</summary>
    public void SaveSnapshotAndClose(string path)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            // The whole scroll content, not just the viewport, on the window's ground
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = columns.ActualWidth; var h = columns.ActualHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(columns), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
            Close();
        });
    }

    static FrameworkElement Column(Theme theme)
    {
        var panel = new StackPanel { Margin = new Thickness(RL.Space.Xl) };
        var ground = new Border { Child = panel, CornerRadius = new CornerRadius(RL.Radius.Window), Margin = new Thickness(RL.Space.Md) };
        ThemeManager.Pin(ground, theme);
        ground.Bind(Border.BackgroundProperty, RL.Surface.Ground);
        ground.Bind(Border.BorderBrushProperty, RL.Stroke.Border);
        ground.BorderThickness = new Thickness(1);

        panel.Children.Add(Text(theme == Theme.Dark ? "Dark" : "Light", RL.Typography.Title, RL.Ink.Primary));
        panel.Children.Add(Gap(RL.Space.Lg));

        Section(panel, "Palette", "tokens", Swatches());
        Section(panel, "Type", null, TypeScale());

        var statuses = new WrapPanel();
        foreach (var k in Enum.GetValues<RLStatusKind>())
            statuses.Children.Add(new RLStatusIndicator(new RLStatus(k), 14, showsLabel: true) { Margin = new Thickness(0, 0, RL.Space.Lg, RL.Space.Sm) });
        Section(panel, "Status", "shape, colour and words", statuses);

        var rails = new StackPanel();
        foreach (var (u, stale, el) in new (double, bool, double?)[] { (3, false, null), (42, false, 0.5), (71, false, 0.4), (96, false, null), (55, true, null) })
        {
            var st = RLStatus.ForUtilization(u, stale: stale);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, RL.Space.Md) };
            var pct = Text($"{u:0}%", RL.Typography.MonoSmall, RL.Ink.Secondary, new Thickness(RL.Space.Md, 0, 0, 0));
            pct.Width = 40;
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(pct);
            row.Children.Add(new RLUsageRail(u, st, elapsed: el) { VerticalAlignment = VerticalAlignment.Center });
            rails.Children.Add(row);
        }
        rails.Children.Add(new RLUsageRail(64, RLStatus.ForUtilization(64), tint: ProviderAccent.For("codex").Brush(theme)) { Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        var widget = new Border { Background = Themed.Solid(RL.BrandTone.Carbon), Padding = new Thickness(RL.Space.Md), CornerRadius = new CornerRadius(RL.Radius.Control) };
        var wstack = new StackPanel();
        foreach (var (u, stale) in new[] { (30.0, false), (70.0, false), (90.0, false), (50.0, true) })
            wstack.Children.Add(new LimitRail { Utilization = u, Stale = stale, Margin = new Thickness(0, 2, 0, 2) });
        widget.Child = wstack;
        rails.Children.Add(Text("LimitRail (widget, fixed tones)", RL.Typography.Caption, RL.Ink.Muted));
        rails.Children.Add(widget);
        Section(panel, "Rails", "RLUsageRail", rails);

        var marks = new WrapPanel();
        foreach (var p in new[] { "claude", "codex", "ollama", null })
            marks.Children.Add(new ProviderBadge(p) { Margin = new Thickness(0, 0, RL.Space.Md, RL.Space.Md) });
        foreach (var p in new[] { "anthropic", "codex", "ollama", null })
            marks.Children.Add(new TrackBadge(p) { Margin = new Thickness(0, 0, RL.Space.Md, RL.Space.Md), VerticalAlignment = VerticalAlignment.Center });
        foreach (var p in new[] { "claude", "codex", "ollama", null })
            marks.Children.Add(new TrackMark(p, ProviderAccent.For(p).Brush(theme), 14) { Margin = new Thickness(0, 0, RL.Space.Md, RL.Space.Md), VerticalAlignment = VerticalAlignment.Center });
        foreach (var m in Enum.GetValues<ProviderMark>())
        {
            var g = new ProviderGlyph(m, 20) { Margin = new Thickness(0, 0, RL.Space.Md, RL.Space.Md) };
            g.Bind(ProviderGlyph.ForegroundProperty, RL.Ink.Primary);
            marks.Children.Add(g);
        }
        marks.Children.Add(new RedlineMark(26) { Margin = new Thickness(0, 0, RL.Space.Md, 0) });
        marks.Children.Add(new RedlineMarkAdaptive(26));
        Section(panel, "Providers", "badge, tile, mark, glyph", marks);

        var tiles = new UniformGrid { Columns = 3 };
        tiles.Children.Add(new RLMetricTile { Label = "Tokens", Value = "1.24M", Note = "last 7 days" });
        tiles.Children.Add(new RLMetricTile { Label = "Cost", Value = "$18.40", Tint = RL.Brandmark.Money.Brush(theme), Help = "Priced models only" });
        tiles.Children.Add(new RLMetricTile { Label = "Resets", Value = "-", Note = "not reported" });
        Section(panel, "Metrics", null, tiles);

        var cards = new StackPanel();
        cards.Children.Add(new RLCard { Child = Text("A plain card", RL.Typography.Body, RL.Ink.Primary), Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        cards.Children.Add(new RLCard { Interactive = true, Child = Text("Interactive: hover me", RL.Typography.Body, RL.Ink.Primary), Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        cards.Children.Add(new RLCard { Selected = true, Accent = ProviderAccent.For("ollama").Brush(theme), Child = Text("Selected, Ollama accent", RL.Typography.Body, RL.Ink.Primary) });
        Section(panel, "Cards", null, cards);

        var states = new StackPanel();
        states.Children.Add(new RLStateBlock(RLStateKind.Loading, "Reading transcripts") { Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        states.Children.Add(new RLStateBlock(RLStateKind.Empty, "No sessions in the last 7 days") { Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        states.Children.Add(new RLStateBlock(RLStateKind.Error, "Could not read the usage database", "It may be locked by another process.") { Margin = new Thickness(0, 0, 0, RL.Space.Md) });
        states.Children.Add(new RLStateBlock(RLStateKind.Unavailable, "Ollama does not report limits"));
        Section(panel, "States", null, states);

        var parts = new WrapPanel();
        parts.Children.Add(new RLPill("transcript") { Margin = new Thickness(0, 0, RL.Space.Sm, RL.Space.Sm) });
        parts.Children.Add(new RLPill("estimate", RL.State.Warning.Brush(theme), "Derived, not reported") { Margin = new Thickness(0, 0, RL.Space.Sm, RL.Space.Sm) });
        parts.Children.Add(new RLPill("live", RL.State.Success.Brush(theme)) { Margin = new Thickness(0, 0, RL.Space.Lg, RL.Space.Sm) });
        parts.Children.Add(new RLSegmented([new RLSegment("24h", "24h"), new RLSegment("7d", "7d"), new RLSegment("30d", "30d", "Last 30 days")], "7d") { Margin = new Thickness(0, 0, RL.Space.Lg, RL.Space.Sm) });
        parts.Children.Add(new RLInlineButton("Refresh", "", "Read everything again"));
        parts.Children.Add(new RLInlineButton("Copy"));
        Section(panel, "Small parts", null, parts);
        return ground;
    }

    static void Section(Panel panel, string title, string? note, UIElement body)
    {
        panel.Children.Add(new RLSectionHeader(title, note, new RLInlineButton("More")) { Margin = new Thickness(0, RL.Space.Md, 0, RL.Space.Sm) });
        panel.Children.Add(body);
        panel.Children.Add(Gap(RL.Space.Lg));
    }

    static UIElement Swatches()
    {
        var wrap = new WrapPanel();
        foreach (var t in RL.AllColors)
        {
            var sw = new StackPanel { Width = 96, Margin = new Thickness(0, 0, RL.Space.Sm, RL.Space.Sm) };
            var chip = new Border { Height = 22, CornerRadius = new CornerRadius(RL.Radius.Chip), BorderThickness = new Thickness(1) };
            chip.Bind(Border.BackgroundProperty, t);
            chip.Bind(Border.BorderBrushProperty, RL.Stroke.Hairline);
            sw.Children.Add(chip);
            sw.Children.Add(Text(t.Key[3..], RL.Typography.MonoSmall, RL.Ink.Muted));
            wrap.Children.Add(sw);
        }
        return wrap;
    }

    static UIElement TypeScale()
    {
        var stack = new StackPanel();
        foreach (var s in RL.Typography.All)
            stack.Children.Add(Text($"{s.Key[8..]} {s.Size:0} 0123456789 Usage resets", s, RL.Ink.Primary));
        var label = new TrackedText { Text = "Tracked label" };
        label.TypeStyle = RL.Typography.Label;
        label.Bind(TrackedText.ForegroundProperty, RL.Ink.Muted);
        stack.Children.Add(label);
        return stack;
    }

    static TextBlock Text(string s, TextStyle style, ColorToken ink, Thickness margin = default)
    {
        var t = new TextBlock { Text = s, Margin = margin, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        style.Apply(t);
        t.Bind(TextBlock.ForegroundProperty, ink);
        return t;
    }

    static Border Gap(double h) => new() { Height = h };
}
#endif
