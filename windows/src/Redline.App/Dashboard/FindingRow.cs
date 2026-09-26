// One finding: a labelled header, a sentence held to a readable measure, and the evidence in two
// columns. A finding without a figure shows no figure; inventing one is what this avoids.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Dashboard;

internal static class FindingRow
{
    /// <summary>Past about this, a line is measured in inches and read in guesses.</summary>
    const double Measure = 760;

    public static FrameworkElement Build(Finding finding, Theme theme, Action? onDismiss)
    {
        var kind = finding.Kind switch
        {
            FindingKind.FixNow => RL.State.Warning,
            FindingKind.Habit => RL.State.Success,
            _ => RL.Ink.Muted,
        };
        var visible = finding.Evidence.Take(6).ToList();

        var column = new StackPanel();
        Ui.Add(column, Header(finding, kind, theme, onDismiss), 0);
        Ui.Add(column, new TextBlock
        {
            Text = finding.Detail, FontFamily = RL.Typography.UI, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = RL.Ink.Muted.Brush(theme), MaxWidth = Measure, HorizontalAlignment = HorizontalAlignment.Left,
        }, 8);
        if (visible.Count > 0) Ui.Add(column, Evidence(finding, visible, theme), 8);
        if (finding.Fix is { } fix)
        {
            var arrow = Ui.Text("", RL.Typography.Icons, 10, kind.Brush(theme, 0.8), FontWeights.SemiBold, new Thickness(0, 2, 0, 0));
            arrow.VerticalAlignment = VerticalAlignment.Top;
            var text = new TextBlock
            {
                Text = fix, FontFamily = RL.Typography.UI, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = RL.Ink.Primary.Brush(theme, 0.9),
            };
            var row = Ui.Row([arrow, text], [], 6);
            row.MaxWidth = Measure;
            row.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Add(column, row, 8);
        }

        // The kind, as a rule down the side rather than another word to read
        var rule = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = kind.Brush(theme, 0.55) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        column.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(column, 1);
        grid.Children.Add(rule);
        grid.Children.Add(column);
        return grid;
    }

    static FrameworkElement Header(Finding finding, ColorToken kind, Theme theme, Action? onDismiss)
    {
        var title = Ui.Text(finding.Title, RL.Typography.UI, 14, RL.Ink.Primary.Brush(theme), FontWeights.Medium, wrap: true);
        var trailing = new List<UIElement?>();
        // The label rides with the number, so a figure is never read without its basis
        var basis = Ui.Text(finding.Basis == FindingBasis.Measured ? "counted" : "estimated", RL.Typography.Mono, 10, RL.Ink.Muted.Brush(theme));
        basis.ToolTip = finding.Basis == FindingBasis.Measured ? "counted from your transcripts" : "estimated; the assumptions are in the sentence below";
        trailing.Add(basis);
        if (finding.EstimatedUSD is { } usd)
        {
            var cost = Ui.Text("~" + Usage.FmtCost(usd), RL.Typography.Mono, 13, RL.Brandmark.Money.Brush(theme), FontWeights.Medium);
            cost.ToolTip = "estimated over measured counts, never a bill";
            trailing.Add(cost);
        }
        if (onDismiss is not null)
        {
            var dismiss = Ui.IconButton("", "Read it. Hides this finding for a while; it returns if it is still true then.", onDismiss, 12);
            AutomationProperties.SetName(dismiss, "Mark as read");
            trailing.Add(dismiss);
        }
        return Ui.Row([new RLPill(finding.Kind.Label(), kind.Brush(theme)), title], trailing, 10);
    }

    static FrameworkElement Evidence(Finding finding, IReadOnlyList<FindingEvidence> visible, Theme theme)
    {
        var stack = new StackPanel();
        foreach (var e in visible)
        {
            var trailing = new List<UIElement?>();
            if (e.Value is { } value) trailing.Add(Ui.Text(value, RL.Typography.Mono, 12, RL.Ink.Muted.Brush(theme)));
            Ui.Add(stack, Ui.Row([Ui.Text(e.Label, RL.Typography.Mono, 12, RL.Ink.Primary.Brush(theme, 0.8))], trailing, 12), 3);
        }
        if (finding.Evidence.Count > visible.Count)
            Ui.Add(stack, Ui.Text($"+{finding.Evidence.Count - visible.Count} more", RL.Typography.Mono, 11, RL.Ink.Muted.Brush(theme, 0.8)), 3);
        return new Border
        {
            Child = stack, MaxWidth = Measure, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(7),
            Background = RL.Surface.Ground.Brush(theme, 0.45),
        };
    }
}
