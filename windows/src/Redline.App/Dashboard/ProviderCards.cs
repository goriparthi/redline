// The overview: one card per provider plus the warnings worth reading before them (port of
// ProviderCards.swift). Card content is derived in Core's ProviderOverview; this draws it.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Dashboard;

/// <summary>A card that is also a control: the whole surface opens the provider's detail.</summary>
/// <remarks>Focus is drawn explicitly, because a bare template otherwise leaves keyboard focus invisible.</remarks>
internal sealed class CardButton : Button
{
    public CardButton(RLCard card, string help, string accessibilityLabel, Action action)
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Template = template;
        Content = card;
        Cursor = Cursors.Hand;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ToolTip = help;
        AutomationProperties.SetName(this, accessibilityLabel);
        AutomationProperties.SetHelpText(this, "Opens this provider's detail");
        FocusVisualStyle = FocusRing();
        Click += (_, _) => action();
    }

    static Style FocusRing()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.MarginProperty, new Thickness(-1));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(RL.Radius.Card + 1));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2.5));
        ring.SetValue(Border.BorderBrushProperty, SystemParameters.WindowGlassBrush ?? SystemColors.HighlightBrush);
        var style = new Style();
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = ring }));
        return style;
    }
}

/// <summary>One provider's overview card. Siblings share this body; the accent and mark tell them apart.</summary>
internal static class ProviderCardView
{
    public static FrameworkElement Build(ProviderCard card, double yellow, double red, string periodLabel,
                                         DateTimeOffset? scannedAt, bool selected, Theme theme, Action onOpen)
    {
        var accentToken = card.Identity?.Token() ?? RL.Accent.Neutral;
        var accent = accentToken.Brush(theme);
        var status = card.Status(yellow, red);

        var body = new StackPanel();
        // The accent as a rule across the top: what makes each card recognisable before it is read
        Ui.Add(body, new Border
        {
            Height = 2.5, CornerRadius = new CornerRadius(1.25),
            Background = accentToken.Brush(theme, card.Connection == ProviderConnection.NotInstalled ? 0.3 : 0.9),
        }, 0);
        Ui.Add(body, Header(card, status), RL.Space.Md);
        Ui.Add(body, Ui.Divider(), RL.Space.Md);
        if (card.Connection.HasFigures()) Ui.Add(body, Figures(card, status, periodLabel, accentToken, theme), RL.Space.Md);
        else Ui.Add(body, new RLStateBlock(Placeholder(card), card.Connection.Phrase(), Hint(card)) { MinHeight = 62 }, RL.Space.Md);
        Ui.Add(body, Footer(card, scannedAt), RL.Space.Md);

        var rlCard = new RLCard
        {
            Interactive = true, Selected = selected, Accent = accent, Padding = new Thickness(RL.Space.Lg),
            Child = body, VerticalAlignment = VerticalAlignment.Stretch,
        };
        return new CardButton(rlCard, Help(card, status), AccessibilityLabel(card, status, periodLabel), onOpen)
        {
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    static FrameworkElement Header(ProviderCard card, RLStatus status)
    {
        var word = Ui.Text(StatusWord(card, status), RL.Typography.Caption, card.IsStale ? RL.State.Warning : RL.Ink.Secondary);
        // Status is a shape plus a word, never the colour on its own
        return Ui.Row([new ProviderBadge(card.Provider, 14)],
                      [new RLStatusIndicator(status, 12), word], RL.Space.Md);
    }

    /// <summary>The short form of what the header glyph reports, so the word never contradicts the shape.</summary>
    static string StatusWord(ProviderCard card, RLStatus status)
    {
        if (card.IsStale && card.AsOf is { } asOf) return $"as of {Ui.Time(asOf)}";
        if (card.WorstWindow is not null)
            return status.Kind switch
            {
                RLStatusKind.AtLimit => "at limit",
                RLStatusKind.Approaching => "approaching",
                _ => "healthy",
            };
        return card.Connection switch
        {
            ProviderConnection.Active => "live",
            ProviderConnection.Idle => "idle",
            ProviderConnection.Unreachable => "stopped",
            ProviderConnection.NotRead => "off",
            _ => "absent",
        };
    }

    static FrameworkElement Figures(ProviderCard card, RLStatus status, string periodLabel, ColorToken accent, Theme theme)
    {
        var stack = new StackPanel();
        if (card.Utilization is { } utilization && card.WorstWindow is { } window)
        {
            var big = Ui.Text(Ui.Pct(utilization), RL.Typography.Mono, 27, status.Color().Brush(theme), FontWeights.SemiBold);
            var used = Ui.Text("used", RL.Typography.Caption, RL.Ink.Muted, new Thickness(0, 0, 0, 4));
            used.VerticalAlignment = VerticalAlignment.Bottom;
            big.VerticalAlignment = VerticalAlignment.Bottom;
            var trailing = new List<UIElement?>();
            if (card.RemainingPercent is { } remaining)
            {
                var left = Ui.Text($"{Ui.Round(remaining)}% left", RL.Typography.MonoSmall, RL.Ink.Secondary, new Thickness(0, 0, 0, 5));
                left.VerticalAlignment = VerticalAlignment.Bottom;
                left.ToolTip = $"Capacity remaining in {window.DisplayName}";
                trailing.Add(left);
            }
            var head = Ui.Row([big, used], trailing, RL.Space.Sm);
            Ui.Add(stack, head, 0);
            Ui.Add(stack, new RLUsageRail(utilization, status, 7, elapsed: card.Pace?.ElapsedFraction), RL.Space.Sm);
            Ui.Add(stack, Ui.Text(WindowLine(card, window), RL.Typography.MonoSmall, RL.Ink.Muted), RL.Space.Sm);
        }
        else
        {
            // No limit to draw: say why, and let the volume figures carry the card
            Ui.Add(stack, Ui.Text(Usage.FmtTokens(card.Tokens), RL.Typography.Mono, 27, RL.Ink.Primary.Brush(theme), FontWeights.SemiBold), 0);
            Ui.Add(stack, Ui.Text($"tokens {periodLabel}", RL.Typography.MonoSmall, RL.Ink.Muted), RL.Space.Xs);
            if (card.LimitNote is { } note) Ui.Add(stack, Ui.Text(note, RL.Typography.Caption, RL.Ink.Muted, wrap: true), RL.Space.Xs);
        }
        if (card.Trend.Any(v => v > 0))
        {
            var trend = new StackPanel();
            Ui.Add(trend, new MiniTrend(card.Trend, accent.Brush(theme, 0.8), RL.Ink.Muted.Brush(theme), 24, $"{card.Provider} tokens per day"), 0);
            var cost = Ui.Text(Usage.FmtCost(card.Cost) + (card.HasUnpriced ? "+" : "") + " est", RL.Typography.MonoSmall, RL.Brandmark.Money);
            cost.ToolTip = card.HasUnpriced
                ? "Some models in this window have no pricing entry, so they are counted in tokens only and the total is marked with a plus"
                : "Estimated from your pricing table, never a bill";
            Ui.Add(trend, Ui.Row(
                [Ui.Text(Usage.FmtTokens(card.Tokens), RL.Typography.MonoSmall, RL.Ink.Secondary),
                 Ui.Text("·", RL.Typography.MonoSmall, RL.Ink.Muted), cost],
                [Ui.Text(periodLabel, RL.Typography.MonoSmall, RL.Ink.Muted)], RL.Space.Sm), RL.Space.Xs);
            Ui.Add(stack, trend, RL.Space.Md);
        }
        return stack;
    }

    /// <summary>The window and its reset; the pace joins only when the cap arrives first.</summary>
    static string WindowLine(ProviderCard card, LimitWindow window)
    {
        var parts = new List<string> { window.DisplayName };
        if (window.ResetsAt is { } resets) parts.Add($"resets {Ui.Time(resets)}");
        if (card.Pace is { HitsLimitBeforeReset: true } pace && pace.Compact() is { } summary) parts.Add(summary);
        return string.Join(" · ", parts);
    }

    static FrameworkElement Footer(ProviderCard card, DateTimeOffset? scannedAt)
    {
        var leading = new List<UIElement?>();
        if (card.ServiceTone is { } tone && card.ServicePhrase is { } phrase)
        {
            leading.Add(new RLStatusIndicator(RLStatus.ForTone(tone, phrase), 10));
            leading.Add(Ui.Text(phrase, RL.Typography.MonoSmall, RL.Ink.Muted));
        }
        else if (scannedAt is { } at)
        {
            leading.Add(Ui.Text($"updated {Ui.TimeWithSeconds(at)}", RL.Typography.MonoSmall, RL.Ink.Muted));
        }
        var chevron = Ui.Icon("", 9, RL.Ink.Muted);
        chevron.FontWeight = FontWeights.SemiBold;
        return Ui.Row(leading, [chevron], RL.Space.Sm);
    }

    static RLStateKind Placeholder(ProviderCard card) => card.Connection switch
    {
        ProviderConnection.NotInstalled or ProviderConnection.NotRead => RLStateKind.Unavailable,
        ProviderConnection.Unreachable => RLStateKind.Error,
        _ => RLStateKind.Empty,
    };

    static string? Hint(ProviderCard card) => card.Connection switch
    {
        ProviderConnection.NotInstalled => card.Identity?.Blurb,
        ProviderConnection.NotRead => "Switch it on in Settings, under Providers",
        ProviderConnection.Unreachable => card.Identity?.IsLocal == true ? "Start it with: ollama serve" : null,
        ProviderConnection.Idle => card.LimitNote,
        _ => null,
    };

    static string Help(ProviderCard card, RLStatus status)
    {
        var parts = new List<string> { $"{card.Provider}: {status.Phrase}" };
        if (card.Identity is { } identity) parts.Add(identity.Blurb);
        return string.Join(". ", parts);
    }

    static string AccessibilityLabel(ProviderCard card, RLStatus status, string periodLabel)
    {
        var parts = new List<string> { card.Provider, status.Phrase };
        if (card.Utilization is { } u && card.WorstWindow is { } window)
        {
            parts.Add($"{Ui.Round(u)} percent of {window.DisplayName} used");
            if (window.ResetsAt is { } resets) parts.Add($"resets {Ui.Time(resets)}");
        }
        if (card.Connection.HasFigures()) parts.Add($"{Usage.FmtTokens(card.Tokens)} tokens {periodLabel}");
        return string.Join(", ", parts);
    }
}

/// <summary>The card grid: column count from the space available, capped at the number of cards.</summary>
/// <remarks>Cards in a row share its height, so a taller sibling never leaves the others floating.</remarks>
internal sealed class ProviderCardGrid : Panel
{
    /// <summary>The width at which a card's own content stops fitting, not a screen size.</summary>
    public double MinimumCardWidth { get; set; } = 268;
    public double Spacing { get; set; } = RL.Space.Lg;

    int Columns(double available)
    {
        var count = InternalChildren.Count;
        if (double.IsInfinity(available) || available <= 0) return Math.Min(count, 3);
        var fits = (int)((available + Spacing) / (MinimumCardWidth + Spacing));
        return Math.Max(1, Math.Min(count, fits));
    }

    double[] rowHeights = [];

    protected override Size MeasureOverride(Size available)
    {
        var count = InternalChildren.Count;
        if (count == 0) return new Size(0, 0);
        var cols = Columns(available.Width);
        var width = double.IsInfinity(available.Width) ? MinimumCardWidth : (available.Width - Spacing * (cols - 1)) / cols;
        var rows = (count + cols - 1) / cols;
        rowHeights = new double[rows];
        for (int i = 0; i < count; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(width, double.PositiveInfinity));
            rowHeights[i / cols] = Math.Max(rowHeights[i / cols], c.DesiredSize.Height);
        }
        var total = rowHeights.Sum() + Spacing * (rows - 1);
        return new Size(double.IsInfinity(available.Width) ? width * cols + Spacing * (cols - 1) : available.Width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var count = InternalChildren.Count;
        if (count == 0) return final;
        var cols = Columns(final.Width);
        var width = (final.Width - Spacing * (cols - 1)) / cols;
        double y = 0;
        for (int i = 0; i < count; i++)
        {
            var row = i / cols;
            if (i % cols == 0 && row > 0) y += rowHeights[row - 1] + Spacing;
            var h = row < rowHeights.Length ? rowHeights[row] : InternalChildren[i].DesiredSize.Height;
            InternalChildren[i].Arrange(new Rect((width + Spacing) * (i % cols), y, width, h));
        }
        return final;
    }
}

/// <summary>The warnings above the cards. Nothing is shown when nothing is true.</summary>
internal static class OverviewWarnings
{
    /// <summary>How many fit before the overview stops being a summary; the rest are counted.</summary>
    const int Shown = 3;

    public static FrameworkElement Build(IReadOnlyList<ProviderOverview.Warning> warnings, Theme theme, Action<string> onOpen)
    {
        var stack = new StackPanel();
        foreach (var warning in warnings.Take(Shown))
        {
            var status = warning.Kind.Status();
            var color = status.Color();
            var text = Ui.Text(warning.Text, RL.Typography.Body, RL.Ink.Primary, wrap: true);
            var trailing = new List<UIElement?>();
            if (warning.Window.ResetsAt is { } resets)
                trailing.Add(Ui.Text($"resets {Ui.Time(resets)}", RL.Typography.MonoSmall, RL.Ink.Secondary));
            var row = Ui.Row([new RLStatusIndicator(status, 14), text], trailing, RL.Space.Md);
            var box = new Border
            {
                Child = row, Padding = new Thickness(RL.Space.Lg, RL.Space.Md, RL.Space.Lg, RL.Space.Md),
                CornerRadius = new CornerRadius(RL.Radius.Control), BorderThickness = new Thickness(1),
                Background = color.Brush(theme, 0.1), BorderBrush = color.Brush(theme, 0.4),
            };
            var button = new Button
            {
                Content = box, Cursor = Cursors.Hand, ToolTip = $"Opens {warning.Provider}",
                Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(button, $"{status.Phrase}. {warning.Text}");
            var provider = warning.Provider;
            button.Click += (_, _) => onOpen(provider);
            Ui.Add(stack, button, RL.Space.Md);
        }
        if (warnings.Count > Shown)
            Ui.Add(stack, Ui.Text($"{warnings.Count - Shown} more in Limits below", RL.Typography.Caption, RL.Ink.Muted,
                                  new Thickness(RL.Space.Lg, 0, 0, 0)), RL.Space.Md);
        return stack;
    }
}
