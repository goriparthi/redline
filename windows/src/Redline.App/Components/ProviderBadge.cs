// A provider mark inside a RedLine-owned chip or tile. The tint is on the chip, the mark stays
// monochrome, and the provider's name is always present beside it (port of Components.swift).
using System.Windows;
using Redline.Core;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Redline.App.Components;

/// <summary>Mark plus name in a tinted capsule. An unknown provider falls back to a tile with the RedLine mark.</summary>
public class ProviderBadge : ContentControl
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(string), typeof(ProviderBadge), new PropertyMetadata(null, (d, _) => ((ProviderBadge)d).Rebuild()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProviderBadge), new PropertyMetadata(15.0, (d, _) => ((ProviderBadge)d).Rebuild()));
    public static readonly DependencyProperty ShowsLabelProperty = DependencyProperty.Register(
        nameof(ShowsLabel), typeof(bool), typeof(ProviderBadge), new PropertyMetadata(true, (d, _) => ((ProviderBadge)d).Rebuild()));
    static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        "Accent", typeof(Brush), typeof(ProviderBadge), new PropertyMetadata(Brushes.Gray, (d, _) => ((ProviderBadge)d).Retint()));

    public string? Provider { get => (string?)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool ShowsLabel { get => (bool)GetValue(ShowsLabelProperty); set => SetValue(ShowsLabelProperty, value); }

    Border? chip;
    ProviderGlyph? glyph;

    public ProviderBadge() { Focusable = false; IsTabStop = false; Rebuild(); }
    public ProviderBadge(string? provider, double size = 15, bool showsLabel = true) : this()
    {
        Provider = provider; Size = size; ShowsLabel = showsLabel;
    }

    void Rebuild()
    {
        var identity = ProviderIdentity.Of(Provider);
        if (identity is null)
        {
            chip = null; glyph = null;
            Content = new ProviderTile(Provider, Size + 7);
            return;
        }
        this.Bind(AccentProperty, identity.Token());
        glyph = new ProviderGlyph(identity.Mark, Size, decorative: ShowsLabel) { VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(glyph);
        if (ShowsLabel)
        {
            var name = new TextBlock
            {
                Text = identity.Name, FontFamily = RL.Typography.UI, FontSize = Size * 0.82, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Sm, 0, 0, 0),
            };
            name.Bind(TextBlock.ForegroundProperty, RL.Ink.Primary);
            row.Children.Add(name);
        }
        chip = new Border
        {
            Child = row, Padding = new Thickness(RL.Space.Md, RL.Space.Xs, RL.Space.Md, RL.Space.Xs),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
        };
        chip.SizeChanged += (_, e) => chip.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        Content = chip;
        AutomationProperties.SetName(this, identity.Name);
        Retint();
    }

    void Retint()
    {
        var accent = (Brush)GetValue(AccentProperty);
        if (glyph is not null) glyph.Foreground = accent;
        if (chip is null) return;
        chip.Background = Themed.Tint(accent, 0.13);
        chip.BorderBrush = Themed.Tint(accent, 0.35);
    }
}

/// <summary>A provider mark in a small tinted tile, for a row with no room for a chip. No provider means RedLine's own mark.</summary>
public class ProviderTile : FrameworkElement
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(string), typeof(ProviderTile), new PropertyMetadata(null, (d, _) => ((ProviderTile)d).Rebuild()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProviderTile), new PropertyMetadata(22.0, (d, _) => ((ProviderTile)d).Rebuild()));
    static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        "Tint", typeof(Brush), typeof(ProviderTile),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ProviderTile)d).Retint()));

    public string? Provider { get => (string?)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    FrameworkElement? child;

    public ProviderTile() { Rebuild(); }
    public ProviderTile(string? provider, double size = 22) { Provider = provider; Size = size; Rebuild(); }

    double Inset => Size * 0.26;

    void Rebuild()
    {
        var identity = ProviderIdentity.Of(Provider);
        this.Bind(TintProperty, identity?.Token() ?? RL.Accent.Neutral);
        if (child is not null) RemoveVisualChild(child);
        var inner = Size - Inset * 2;
        child = identity is null ? new RedlineMark(inner) : new ProviderGlyph(identity.Mark, inner);
        AddVisualChild(child);
        Retint();
        InvalidateMeasure();
        InvalidateVisual();
    }

    void Retint()
    {
        if (child is ProviderGlyph g) g.Foreground = (Brush)GetValue(TintProperty);
    }

    protected override int VisualChildrenCount => child is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => child!;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size a)
    {
        child?.Measure(new System.Windows.Size(Size, Size));
        return new(Size, Size);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size s)
    {
        child?.Arrange(new Rect(Inset, Inset, Size - Inset * 2, Size - Inset * 2));
        return new(Size, Size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tint = (Brush)GetValue(TintProperty);
        var r = Size * 0.28;
        var lw = Math.Max(1, Size * 0.045);
        dc.DrawRoundedRectangle(Themed.Tint(tint, 0.15), null, new Rect(0, 0, Size, Size), r, r);
        dc.DrawRoundedRectangle(null, new Pen(Themed.Tint(tint, 0.4), lw), new Rect(lw / 2, lw / 2, Size - lw, Size - lw), r, r);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this, () => true);
}
