// Vector stand-ins for the SF Symbols the Swift UI uses, drawn in a 16x16 box.
// Filled shapes have their inner mark knocked out, as the ".fill" symbols do.
using System.Windows;
using System.Windows.Media;
using Redline.Core;

namespace Redline.App.Components;

public static class StatusShapes
{
    public const double Box = 16;

    static Geometry Stroke(string data, double width)
    {
        var pen = new Pen(Brushes.Black, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        return Geometry.Parse(data).GetWidenedPathGeometry(pen);
    }

    static Geometry Dot(double x, double y, double r) => new EllipseGeometry(new Point(x, y), r, r);
    static Geometry Circle(double r = 7.25) => new EllipseGeometry(new Point(8, 8), r, r);
    static Geometry Cut(Geometry a, Geometry b) => new CombinedGeometry(GeometryCombineMode.Exclude, a, b).GetFlattenedPathGeometry();
    static Geometry Join(Geometry a, Geometry b) => new CombinedGeometry(GeometryCombineMode.Union, a, b).GetFlattenedPathGeometry();
    static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

    static Geometry Exclamation(double top, double bottom, double dotY) =>
        Join(Stroke($"M8,{top} L8,{bottom}", 1.8), Dot(8, dotY, 1.05));

    static Geometry RoundedTriangle() =>
        Join(Geometry.Parse("M8,2.2 L14.3,13.4 L1.7,13.4 Z"), Stroke("M8,2.2 L14.3,13.4 L1.7,13.4 Z", 1.8));

    static Geometry Octagon()
    {
        var pts = Enumerable.Range(0, 8)
            .Select(i => Math.PI / 8 + i * Math.PI / 4)
            .Select(a => new Point(8 + 7.1 * Math.Cos(a), 8 + 7.1 * Math.Sin(a))).ToList();
        var fig = new PathFigure(pts[0], pts.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), true);
        var g = new PathGeometry([fig]);
        var pen = new Pen(Brushes.Black, 1) { LineJoin = PenLineJoin.Round };
        return Join(g, g.GetWidenedPathGeometry(pen));
    }

    /// <summary>checkmark.circle.fill</summary>
    public static readonly Geometry Healthy = Frozen(Cut(Circle(), Stroke("M4.9,8.3 L7.1,10.4 L11.2,5.9", 1.7)));

    /// <summary>exclamationmark.triangle.fill</summary>
    public static readonly Geometry Approaching = Frozen(Cut(RoundedTriangle(), Exclamation(6.3, 9.2, 11.5)));

    /// <summary>exclamationmark.octagon.fill</summary>
    public static readonly Geometry AtLimit = Frozen(Cut(Octagon(), Exclamation(4.6, 8.6, 11.3)));

    /// <summary>bolt.slash.circle.fill: a bolt cut by a slash.</summary>
    public static readonly Geometry Offline = Frozen(
        Join(Cut(Cut(Circle(), Geometry.Parse("M9,3.2 L5,9 L7.9,9 L7,12.8 L11,7 L8.1,7 Z")), Stroke("M3.8,3.8 L12.2,12.2", 2.8)),
             new CombinedGeometry(GeometryCombineMode.Intersect, Stroke("M3.8,3.8 L12.2,12.2", 1.1), Circle(6.2))));

    /// <summary>questionmark.circle.fill</summary>
    public static readonly Geometry Unknown = Frozen(Cut(Circle(),
        Join(Stroke("M5.9,6.2 C5.9,4.9 6.8,4.2 8,4.2 C9.2,4.2 10.1,4.9 10.1,6 C10.1,7.4 8,7.5 8,9.2", 1.6), Dot(8, 11.6, 1.0))));

    /// <summary>clock.badge.exclamationmark.fill, drawn as a clock face.</summary>
    public static readonly Geometry Stale = Frozen(Cut(Circle(), Stroke("M8,4.4 L8,8.3 L10.7,9.9", 1.6)));

    /// <summary>tray: nothing to show, and that is a real answer.</summary>
    public static readonly Geometry Tray = Frozen(Stroke("M2.5,9 L4.3,3.8 L11.7,3.8 L13.5,9 L13.5,12.8 L2.5,12.8 Z M2.5,9 L5.6,9 L6.6,10.5 L9.4,10.5 L10.4,9 L13.5,9", 1.3));

    /// <summary>exclamationmark.triangle (outline)</summary>
    public static readonly Geometry TriangleOutline = Frozen(Join(Stroke("M8,2.2 L14.3,13.4 L1.7,13.4 Z", 1.3), Exclamation(6.4, 9.2, 11.4)));

    /// <summary>minus.circle (outline)</summary>
    public static readonly Geometry MinusCircle = Frozen(Join(Stroke("M8,1.4 A6.6,6.6 0 1 1 7.99,1.4 Z", 1.3), Stroke("M5.2,8 L10.8,8", 1.4)));

    public static Geometry For(RLStatusKind kind) => kind switch
    {
        RLStatusKind.Healthy => Healthy,
        RLStatusKind.Approaching => Approaching,
        RLStatusKind.AtLimit => AtLimit,
        RLStatusKind.Offline => Offline,
        RLStatusKind.Unknown => Unknown,
        _ => Stale,
    };

    /// <summary>Draws a 16-unit shape scaled to a size at a point.</summary>
    public static void Draw(DrawingContext dc, Geometry shape, Brush fill, double size, Point origin = default)
    {
        var k = size / Box;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, origin.X, origin.Y));
        dc.DrawGeometry(fill, null, shape);
        dc.Pop();
    }
}

/// <summary>A 16-unit shape as an element, filled with Foreground.</summary>
public class ShapeIcon : FrameworkElement
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(Geometry), typeof(ShapeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ShapeIcon),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
        typeof(ShapeIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Shape { get => (Geometry?)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size _) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Shape is not null) StatusShapes.Draw(dc, Shape, Foreground, Size);
    }
}
