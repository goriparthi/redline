// Small helpers every component shares: theme-bound brushes, tints, and a decorative automation peer.
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Redline.App.Components;

public static class Themed
{
    /// <summary>Binds a Brush property to a token's theme brush, so a theme swap repaints it.</summary>
    public static void Bind(this FrameworkElement e, DependencyProperty p, ColorToken token) =>
        e.SetResourceReference(p, token.BrushKey);

    /// <summary>A brush's colour at an opacity, as SwiftUI's colour.opacity(x).</summary>
    public static Brush Tint(Brush? source, double opacity)
    {
        var c = source is SolidColorBrush s ? s.Color : Colors.Gray;
        var b = new SolidColorBrush(c) { Opacity = opacity * (source?.Opacity ?? 1) };
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Pen Pen(Brush brush, double width, PenLineCap cap = PenLineCap.Round)
    {
        var p = new Pen(brush, width) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    /// <summary>A capsule (fully rounded rectangle) in a rect, the SwiftUI Capsule shape.</summary>
    public static void DrawCapsule(DrawingContext dc, Brush fill, Rect r)
    {
        var radius = Math.Min(r.Width, r.Height) / 2;
        dc.DrawRoundedRectangle(fill, null, r, radius, radius);
    }

    public static double PixelsPerDip(Visual v) => VisualTreeHelper.GetDpi(v).PixelsPerDip;
}

/// <summary>Hides an element from UI Automation when adjacent text already says what it means.</summary>
public sealed class DecorativePeer(FrameworkElement owner, Func<bool> hidden, string? label = null)
    : FrameworkElementAutomationPeer(owner)
{
    protected override bool IsControlElementCore() => !hidden();
    protected override bool IsContentElementCore() => !hidden();
    protected override string GetNameCore() => label ?? base.GetNameCore();
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
}
