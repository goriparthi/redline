// The desktop widget's face, ported from RedlineWidget.swift. It renders the snapshot the app
// publishes and parses nothing; each layout is tried full, trimmed, then lean until one fits.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Tray;

/// <summary>Which provider a widget follows. All means whichever is nearest its limit.</summary>
public enum TrackChoice { All, Claude, Codex, Ollama }

public enum WidgetFamily { Small, Medium, Large }

public static class TrackChoiceExt
{
    public static string? Provider(this TrackChoice t) => t switch
    {
        TrackChoice.Claude => "Claude",
        TrackChoice.Codex => "Codex",
        TrackChoice.Ollama => "Ollama",
        _ => null,
    };

    public static string Title(this TrackChoice t) => t == TrackChoice.All ? "All providers" : t.Provider()!;

    public static string RawValue(this TrackChoice t) => t.ToString().ToLowerInvariant();
    public static string RawValue(this WidgetFamily f) => f.ToString().ToLowerInvariant();

    /// <summary>The widget sizes macOS uses, in DIPs, so the three layouts keep their proportions.</summary>
    public static Size Size(this WidgetFamily f) => f switch
    {
        WidgetFamily.Small => new Size(170, 170),
        WidgetFamily.Large => new Size(364, 382),
        _ => new Size(364, 170),
    };
}

public static class WidgetView
{
    const double Pad = 16;

    /// <summary>One type scale for the whole widget, growing with the family.</summary>
    sealed record Metrics(double Title, double Hero, double Label, double Detail, double Totals, double Rail,
                          double MarkSize, double Spacing)
    {
        public static Metrics Of(WidgetFamily f) => f switch
        {
            WidgetFamily.Small => new(13, 42, 13, 11, 12, 8, 16, 4),
            WidgetFamily.Large => new(17, 52, 15, 13, 15, 11, 21, 7),
            _ => new(15, 46, 14, 12, 13, 9, 18, 5),
        };
    }

    enum Detail { Full, Trimmed, Lean }

    static readonly Brush Chalk = Themed.Solid(RL.BrandTone.Chalk);
    static readonly Brush Steel = Themed.Solid(RL.BrandTone.Steel);
    static readonly Brush Amber = Themed.Solid(RL.BrandTone.Amber);
    static readonly FontFamily Rounded = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily Sans = RL.Typography.UI;
    static readonly FontFamily Mono = RL.Typography.Mono;

    /// <summary>The whole widget: the carbon card with the track's wash, and the body that fits.</summary>
    public static FrameworkElement Build(Snapshot? snapshot, TrackChoice track, WidgetFamily family, DateTimeOffset? now = null)
    {
        var size = family.Size();
        var t = now ?? DateTimeOffset.UtcNow;
        var inner = new Size(size.Width - Pad * 2, size.Height - Pad * 2);
        FrameworkElement body;
        if (snapshot is null)
        {
            body = Unavailable(Metrics.Of(family));
        }
        else
        {
            Func<Detail, FrameworkElement> layout = track == TrackChoice.Ollama
                ? d => OllamaLayout(snapshot, family, d, t)
                : d => UsageLayout(snapshot, track, family, d, t);
            body = Fit(layout, inner);
        }
        var tint = track.Provider() is { } p ? ProviderAccent.For(p).Dark : RL.BrandTone.Steel;
        var wash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1), EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(RL.BrandTone.Carbon, 0),
                new GradientStop(MenuInk.Blend(RL.BrandTone.Carbon, tint, 0.16), 1),
            },
        };
        wash.Freeze();
        var card = new Border
        {
            Width = size.Width, Height = size.Height,
            CornerRadius = new CornerRadius(22),
            Background = wash,
            BorderBrush = Themed.Solid(Color.FromArgb(0x55, 0x81, 0x87, 0x92)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Pad),
            Child = body,
            SnapsToDevicePixels = true,
        };
        // The widget paints Carbon whatever the app theme, so its tokens resolve as dark
        ThemeManager.Pin(card, Theme.Dark);
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Ideal);
        return card;
    }

    /// <summary>ViewThatFits: the first layout whose natural height fits the card.</summary>
    static FrameworkElement Fit(Func<Detail, FrameworkElement> layout, Size inner)
    {
        FrameworkElement? last = null;
        foreach (var d in new[] { Detail.Full, Detail.Trimmed, Detail.Lean })
        {
            var candidate = layout(d);
            candidate.Measure(new Size(inner.Width, double.PositiveInfinity));
            last = candidate;
            if (candidate.DesiredSize.Height <= inner.Height + 0.5) break;
        }
        return last!;
    }

    // Pieces

    static TextBlock Text(string s, double size, Brush ink, FontWeight? weight = null, FontFamily? family = null) => new()
    {
        Text = s, FontSize = size, Foreground = ink, FontWeight = weight ?? FontWeights.Normal,
        FontFamily = family ?? Sans, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
        // SF's line box is tighter than Segoe's; matching it keeps the layouts the same height
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = Math.Ceiling(size * 1.18),
    };

    /// <summary>minimumScaleFactor: shrinks to fit the width it is given, never grows.</summary>
    static FrameworkElement Shrink(FrameworkElement e, HorizontalAlignment align = HorizontalAlignment.Left) =>
        new Viewbox { Child = e, StretchDirection = StretchDirection.DownOnly, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = align };

    static string ResetText(DateTimeOffset d)
    {
        var local = d.ToLocalTime();
        var today = local.Date == DateTime.Now.Date;
        var hour = local.ToString("h:mm", CultureInfo.InvariantCulture) + (local.Hour < 12 ? "a" : "p");
        return today ? hour : local.ToString("ddd ", CultureInfo.InvariantCulture) + hour;
    }

    static Color StatusShade(RLStatus s) => s.Color().Dark;

    static FrameworkElement Glyph(RLStatus s, double size) => new ShapeIcon
    {
        Shape = s.Shape(), Size = size, Foreground = Themed.Solid(StatusShade(s)),
        VerticalAlignment = VerticalAlignment.Center, ToolTip = s.Phrase,
    };

    static FrameworkElement Header(string title, Metrics m, string? provider, string? trailing, RLStatus? status)
    {
        var dock = new DockPanel { LastChildFill = true };
        var badge = new TrackBadge(provider, m.MarkSize + 8) { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        var signature = new RedlineMark(m.MarkSize - 2) { Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        DockPanel.SetDock(signature, Dock.Right);
        dock.Children.Add(signature);
        if (trailing is not null)
        {
            var tr = Text(trailing, m.Detail, Steel, family: Mono);
            tr.VerticalAlignment = VerticalAlignment.Center;
            tr.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(tr, Dock.Right);
            dock.Children.Add(tr);
        }
        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleText = Text(title, m.Title, Chalk, FontWeights.SemiBold);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        name.Children.Add(titleText);
        if (status is not null)
        {
            var g = Glyph(status, m.Detail);
            g.Margin = new Thickness(7, 0, 0, 0);
            name.Children.Add(g);
        }
        dock.Children.Add(Shrink(name));
        return dock;
    }

    static FrameworkElement WindowBlock(string label, Snapshot.Window? w, Metrics m, bool showsReset, bool stale, double? hero = null)
    {
        var s = new StackPanel();
        s.Children.Add(Text(label, m.Label, Steel, FontWeights.Medium));
        var heroSize = hero ?? m.Hero;
        var number = w is { } win
            ? Text($"{(int)Math.Round(win.Utilization, MidpointRounding.AwayFromZero)}%", heroSize,
                   stale ? Steel : Themed.Solid(RL.BrandTone.StatusColor(win.Utilization)), FontWeights.Bold, Rounded)
            // A dash, never a zero that would read as plenty left
            : Text("n/a", heroSize, Steel, FontWeights.Bold, Rounded);
        number.Margin = new Thickness(0, 2, 0, 2);
        s.Children.Add(Shrink(number));
        s.Children.Add(new LimitRail
        {
            Utilization = w?.Utilization ?? 0, RailHeight = m.Rail, ShowsLimit = w is not null, Stale = stale,
            Margin = new Thickness(0, 4, 0, 0),
        });
        if (showsReset && w?.ResetsAt is { } r)
        {
            var reset = Text($"resets {ResetText(r)}", m.Detail, Steel, family: Mono);
            reset.Margin = new Thickness(0, 4, 0, 0);
            s.Children.Add(Shrink(reset));
        }
        return s;
    }

    static FrameworkElement WindowLine(string label, Snapshot.Window? w, Metrics m, bool stale)
    {
        var dock = new DockPanel();
        var value = w is { } win
            ? Text($"{(int)Math.Round(win.Utilization, MidpointRounding.AwayFromZero)}%", m.Label + 5,
                   stale ? Steel : Themed.Solid(RL.BrandTone.StatusColor(win.Utilization)), FontWeights.Bold, Rounded)
            : Text("n/a", m.Label + 5, Steel, FontWeights.Bold);
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(value);
        var l = Text(label, m.Label, Steel, FontWeights.Medium);
        l.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(l);
        return dock;
    }

    static FrameworkElement TotalsRow(string label, Snapshot.Totals totals, Metrics m)
    {
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Text(Usage.FmtTokens(totals.Io), m.Totals, Chalk, FontWeights.Medium, Mono));
        var cost = Text($"{Usage.FmtCost(totals.Cost)}{(totals.HasUnpriced ? "+" : "")}", m.Totals, Steel, family: Mono);
        cost.Margin = new Thickness(6, 0, 0, 0);
        right.Children.Add(cost);
        var shrunk = Shrink(right, HorizontalAlignment.Right);
        DockPanel.SetDock(shrunk, Dock.Right);
        dock.Children.Add(shrunk);
        dock.Children.Add(Text(label, m.Totals, Steel));
        return dock;
    }

    static FrameworkElement Divider() => new Border
    {
        Height = 1, Background = Themed.Solid(Color.FromArgb(0x40, 0x81, 0x87, 0x92)), Margin = new Thickness(0, 2, 0, 2),
    };

    static FrameworkElement? StaleNote(Snapshot snap, Metrics m, DateTimeOffset now)
    {
        string? text = null;
        if (snap.IsStale(now)) text = $"Last updated {ResetText(snap.UpdatedAt)}";
        // The snapshot is current but Claude's windows are not: the feed writes only while Claude Code runs
        else if (snap.ClaudeLimitsAreStale(now) && snap.ClaudeLimitsAsOf is { } at) text = $"Limits as of {ResetText(at)}";
        return text is null ? null : Shrink(Text(text, m.Detail, Amber, family: Mono));
    }

    static FrameworkElement? ServiceLine(Snapshot snap, string? provider, Metrics m)
    {
        if (snap.Services is not { Count: > 0 } services) return null;
        Snapshot.Service? r = provider is not null
            ? services.FirstOrDefault(s => s.Provider == provider)
            // The all-providers track reports the worst news anyone has
            : services.OrderByDescending(s => Rank(s.Indicator)).First();
        if (r is null) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var g = Glyph(IndicatorStatus(r.Indicator, r.Phrase), m.Detail);
        g.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(g);
        row.Children.Add(Text(r.Phrase, m.Detail, Steel, family: Mono));
        return Shrink(row);
    }

    static int Rank(string indicator) => ServiceGlyph.ToneFor(indicator) switch
    {
        ServiceGlyph.Tone.Critical => 2,
        ServiceGlyph.Tone.Warning => 1,
        _ => 0,
    };

    /// <summary>The shared health vocabulary as a status: the same glyph the dropdown draws.</summary>
    public static RLStatus IndicatorStatus(string indicator, string? phrase = null) => indicator switch
    {
        "local-down" => new RLStatus(RLStatusKind.Offline, phrase),
        ServiceGlyph.Checking => new RLStatus(RLStatusKind.Unknown, phrase),
        _ => RLStatus.ForTone(ServiceGlyph.ToneFor(indicator), phrase),
    };

    static FrameworkElement Footer(Snapshot snap, string? provider, Metrics m, DateTimeOffset now)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (ServiceLine(snap, provider, m) is { } s) { s.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(s); }
        if (StaleNote(snap, m, now) is { } n) row.Children.Add(n);
        return Shrink(row);
    }

    static FrameworkElement Unavailable(Metrics m)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(new RedlineMark(m.MarkSize + 10) { HorizontalAlignment = HorizontalAlignment.Center });
        var a = Text("Usage unavailable", m.Title + 1, Chalk, FontWeights.SemiBold);
        a.HorizontalAlignment = HorizontalAlignment.Center;
        a.Margin = new Thickness(0, 7, 0, 7);
        s.Children.Add(a);
        var b = Text("Open RedLine to refresh", m.Label, Steel);
        b.HorizontalAlignment = HorizontalAlignment.Center;
        b.TextAlignment = TextAlignment.Center;
        b.TextWrapping = TextWrapping.Wrap;
        s.Children.Add(b);
        return s;
    }

    /// <summary>Content stacked at the top, the health line pinned to the bottom (Spacer(minLength: 0)).</summary>
    static Grid Column(IEnumerable<FrameworkElement> top, FrameworkElement? footer, double spacing)
    {
        var grid = new Grid();
        int row = 0;
        foreach (var e in top)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (row > 0) e.Margin = new Thickness(e.Margin.Left, e.Margin.Top + spacing, e.Margin.Right, e.Margin.Bottom);
            Grid.SetRow(e, row++);
            grid.Children.Add(e);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        row++;
        if (footer is not null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.Margin = new Thickness(0, spacing, 0, 0);
            Grid.SetRow(footer, row);
            grid.Children.Add(footer);
        }
        return grid;
    }

    // Usage

    static FrameworkElement UsageLayout(Snapshot snap, TrackChoice track, WidgetFamily family, Detail detail, DateTimeOffset now)
    {
        var m = Metrics.Of(family);
        var provider = track.Provider();
        var title = provider ?? "Usage";
        var session = snap.Worst("five_hour", provider);
        var week = snap.Worst("seven_day", provider);
        var windows = provider is null ? snap.Limits.ToList() : snap.Windows(provider);
        // Only Claude's windows age separately: they come from the statusline feed
        bool Stale(Snapshot.Window? w) => w?.Provider == "Claude" && snap.ClaudeLimitsAreStale(now);

        var items = new List<FrameworkElement>
        {
            Header(title, m, provider, detail == Detail.Full && provider is null && snap.Limits.Count > 0 ? "nearest" : null,
                   HeaderStatus(snap, provider)),
        };

        if (session is null && week is null)
        {
            var none = Text(provider is null ? "No limits reported" : $"No limits reported for {title}", m.Label, Steel);
            none.Margin = new Thickness(0, 10, 0, 10);
            items.Add(none);
        }
        else if (family == WidgetFamily.Small)
        {
            items.Add(WindowBlock("Session · 5h", session, m, detail != Detail.Lean, Stale(session)));
            if (detail != Detail.Lean) items.Add(WindowLine("Week", week, m, Stale(week)));
        }
        else
        {
            // A provider with one window gets the full width for it
            var grid = new Grid();
            var blocks = new List<FrameworkElement>();
            if (session is not null || week is null) blocks.Add(WindowBlock("Session · 5h", session, m, detail != Detail.Lean, Stale(session)));
            if (week is not null) blocks.Add(WindowBlock("Week", week, m, detail != Detail.Lean, Stale(week)));
            for (int i = 0; i < blocks.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                blocks[i].Margin = new Thickness(i > 0 ? 8 : 0, 0, i < blocks.Count - 1 ? 8 : 0, 0);
                Grid.SetColumn(blocks[i], i);
                grid.Children.Add(blocks[i]);
            }
            items.Add(grid);
        }

        if (family != WidgetFamily.Small && detail != Detail.Lean)
        {
            items.Add(Divider());
            items.Add(TotalsRow("Today", snap.TodayFor(provider), m));
            if (family == WidgetFamily.Large && detail == Detail.Full)
                items.Add(TotalsRow("Last 7 days", snap.WeekFor(provider), m));
        }

        // Only the all-providers track learns anything from the per-window rows
        if (family == WidgetFamily.Large && detail == Detail.Full && provider is null && windows.Count > 1)
        {
            items.Add(Divider());
            var list = new StackPanel();
            foreach (var w in windows)
            {
                var row = new DockPanel { Margin = new Thickness(0, list.Children.Count > 0 ? 7 : 0, 0, 0) };
                var badge = new TrackBadge(w.Provider, m.Totals + 7) { VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Left);
                row.Children.Add(badge);
                var pct = Text($"{(int)Math.Round(w.Utilization, MidpointRounding.AwayFromZero)}%", m.Totals + 2,
                               Stale(w) ? Steel : Themed.Solid(RL.BrandTone.StatusColor(w.Utilization)), FontWeights.Bold, Rounded);
                pct.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(pct, Dock.Right);
                row.Children.Add(pct);
                var name = Text($"{w.Provider} · {w.DisplayName}", m.Totals, Chalk);
                name.VerticalAlignment = VerticalAlignment.Center;
                var shrunk = Shrink(name);
                shrunk.Margin = new Thickness(7, 0, 4, 0);
                row.Children.Add(shrunk);
                list.Children.Add(row);
            }
            items.Add(list);
        }

        // Health and staleness are the last thing worth dropping
        return Column(items, detail != Detail.Lean ? Footer(snap, provider, m, now) : null, m.Spacing);
    }

    static RLStatus? HeaderStatus(Snapshot snap, string? provider)
    {
        if (snap.Services is not { Count: > 0 } services) return null;
        var relevant = provider is null ? services : services.Where(s => s.Provider == provider).ToList();
        if (relevant.Count == 0) return null;
        var worst = relevant.Select(s => ServiceGlyph.ToneFor(s.Indicator)).OrderByDescending(Severity).First();
        return RLStatus.ForTone(worst, relevant.Count == 1 ? relevant[0].Phrase : null);
    }

    static int Severity(ServiceGlyph.Tone tone) => tone switch
    {
        ServiceGlyph.Tone.Critical => 3,
        ServiceGlyph.Tone.Warning => 2,
        ServiceGlyph.Tone.Unknown => 1,
        _ => 0,
    };

    // Ollama

    static int ModelLimit(WidgetFamily family, Detail detail) => (family, detail) switch
    {
        (WidgetFamily.Small, _) => detail == Detail.Full ? 1 : 0,
        (WidgetFamily.Large, Detail.Full) => 3,
        (_, Detail.Lean) => 0,
        _ => 2,
    };

    static FrameworkElement OllamaLayout(Snapshot snap, WidgetFamily family, Detail detail, DateTimeOffset now)
    {
        var m = Metrics.Of(family);
        var o = snap.Ollama;
        var reachable = o?.Reachable ?? false;
        var items = new List<FrameworkElement>
        {
            Header("Ollama", m, "Ollama", detail == Detail.Full && o?.Version is { } v ? $"v{v}" : null,
                   reachable ? new RLStatus(RLStatusKind.Healthy, "local server running")
                             : new RLStatus(RLStatusKind.Offline, "local server not reachable")),
        };
        if (o is not null && o.Reachable)
        {
            // Local and cloud are separate counts on purpose: which machine a model runs on is a fact
            var counts = new StackPanel { Orientation = Orientation.Horizontal };
            void Count(string value, string label, Brush tint, double scale = 0.78)
            {
                var block = new StackPanel { Margin = new Thickness(counts.Children.Count > 0 ? 14 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
                block.Children.Add(Text(value, m.Hero * scale, tint, FontWeights.Bold, Rounded));
                block.Children.Add(Text(label, m.Label, Steel));
                counts.Children.Add(block);
            }
            Count($"{o.Running.Count}", "loaded", o.Running.Count == 0 ? Steel : Themed.Solid(RL.BrandTone.Clear));
            if (family == WidgetFamily.Small || (o.CloudCount ?? 0) == 0) Count($"{o.DownloadedCount}", "on disk", Chalk);
            else
            {
                Count($"{o.LocalCount}", "local", Chalk);
                Count($"{o.CloudCount ?? 0}", "☁ cloud", Steel);
            }
            if (family != WidgetFamily.Small) Count(Ollama.FmtBytes(o.DownloadedBytes), "size", Chalk, 0.5);
            items.Add(Shrink(counts));

            var shown = ModelLimit(family, detail);
            if (o.Running.Count == 0)
            {
                if (detail != Detail.Lean) items.Add(Text("No model in memory", m.Label, Steel));
            }
            else if (shown > 0)
            {
                var list = new StackPanel();
                foreach (var model in o.Running.Take(shown))
                {
                    var row = new DockPanel();
                    var badge = new TrackBadge("Ollama", m.Label + 4) { VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(badge, Dock.Left);
                    row.Children.Add(badge);
                    var gpu = Text($"{(int)Math.Round(model.VramShare * 100, MidpointRounding.AwayFromZero)}% GPU", m.Detail, Steel, family: Mono);
                    gpu.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(gpu, Dock.Right);
                    row.Children.Add(gpu);
                    var name = Shrink(Text(OllamaLocality.Marked(model.Name), m.Label, Chalk, FontWeights.Medium, Mono));
                    name.Margin = new Thickness(6, 0, 3, 0);
                    row.Children.Add(name);
                    var block = new StackPanel { Margin = new Thickness(0, list.Children.Count > 0 ? 6 : 0, 0, 0) };
                    block.Children.Add(row);
                    // Weights resident on the GPU, not a usage limit
                    block.Children.Add(new LimitRail { Utilization = model.VramShare * 100, RailHeight = Math.Max(4, m.Rail - 3), ShowsLimit = false, Margin = new Thickness(0, 3, 0, 0) });
                    list.Children.Add(block);
                }
                if (o.Running.Count > shown)
                    list.Children.Add(Text($"+{o.Running.Count - shown} more loaded", m.Detail, Steel, family: Mono));
                items.Add(list);
            }
            if (family == WidgetFamily.Large && detail == Detail.Full)
            {
                items.Add(Divider());
                items.Add(TotalsRow("Tokens today", snap.TodayFor("Ollama"), m));
            }
        }
        else
        {
            var down = Shrink(Text("Ollama is not running", m.Title, Chalk, FontWeights.SemiBold));
            down.Margin = new Thickness(0, 8, 0, 0);
            items.Add(down);
            if (detail != Detail.Lean) items.Add(Shrink(Text("start it with: ollama serve", m.Detail, Steel, family: Mono)));
        }
        return Column(items, detail != Detail.Lean ? Footer(snap, "Ollama", m, now) : null, m.Spacing);
    }
}
