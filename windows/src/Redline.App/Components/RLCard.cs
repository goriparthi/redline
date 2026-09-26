// The one card in the system: a raised fill, a hairline edge, and a hover step.
// Depth is carried by the border rather than a shadow, which keeps a wall of cards calm.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Redline.App.Components;

public class RLCard : Border
{
    static readonly DependencyProperty RaisedProperty = Reg("RaisedBrush");
    static readonly DependencyProperty RaisedHoverProperty = Reg("RaisedHoverBrush");
    static readonly DependencyProperty HairlineProperty = Reg("HairlineBrush");
    static readonly DependencyProperty BorderStrongProperty = Reg("BorderStrongBrush");

    /// <summary>Tints the edge when selected. A provider's accent goes here; the mark stays monochrome.</summary>
    public static readonly DependencyProperty AccentProperty = Reg(nameof(Accent));
    /// <summary>Adds the hover step. Only for a card that actually does something.</summary>
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(RLCard), new PropertyMetadata(false, (d, _) => ((RLCard)d).Update()));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(bool), typeof(RLCard), new PropertyMetadata(false, (d, _) => ((RLCard)d).Update(true)));

    static DependencyProperty Reg(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(RLCard), new PropertyMetadata(null, (d, _) => ((RLCard)d).Update()));

    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    readonly SolidColorBrush fill = new();
    readonly SolidColorBrush edge = new();

    static RLCard()
    {
        PaddingProperty.OverrideMetadata(typeof(RLCard), new FrameworkPropertyMetadata(new Thickness(RL.Space.Xl)));
        CornerRadiusProperty.OverrideMetadata(typeof(RLCard), new FrameworkPropertyMetadata(new CornerRadius(RL.Radius.Card)));
    }

    public RLCard()
    {
        Background = fill;
        BorderBrush = edge;
        this.Bind(RaisedProperty, RL.Surface.Raised);
        this.Bind(RaisedHoverProperty, RL.Surface.RaisedHover);
        this.Bind(HairlineProperty, RL.Stroke.Hairline);
        this.Bind(BorderStrongProperty, RL.Stroke.BorderStrong);
        MouseEnter += (_, _) => Update(true);
        MouseLeave += (_, _) => Update(true);
        SnapsToDevicePixels = true;
    }

    static Color C(object? b) => b is SolidColorBrush s ? s.Color : Colors.Transparent;

    void Update(bool animate = false)
    {
        var hovering = IsMouseOver && Interactive;
        var e = Selected ? C(Accent ?? GetValue(BorderStrongProperty))
              : hovering ? C(GetValue(BorderStrongProperty))
              : C(GetValue(HairlineProperty));
        var f = hovering ? C(GetValue(RaisedHoverProperty)) : C(GetValue(RaisedProperty));
        BorderThickness = new Thickness(Selected ? 1.5 : 1);
        // A theme swap or first paint lands at once; only hover and selection animate
        var dur = animate ? RL.Motion.For(RL.Motion.Hover) : new Duration(TimeSpan.Zero);
        fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(f, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        edge.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(e, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
}
