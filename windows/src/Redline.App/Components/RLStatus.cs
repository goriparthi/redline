// The status vocabulary: every status carries a colour, a shape and a word together.
// The data is Core's RLStatus; this adds its colour, its shape and RLStatusIndicator.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Redline.Core;

namespace Redline.App.Components;

/// <summary>The WPF half of Core's RLStatus: its colour token and its shape.</summary>
public static class RLStatusStyle
{
    public static ColorToken Color(this RLStatus status) => status.Kind switch
    {
        RLStatusKind.Healthy => RL.State.Success,
        RLStatusKind.Approaching => RL.State.Warning,
        RLStatusKind.AtLimit => RL.State.Error,
        RLStatusKind.Offline => RL.State.Offline,
        RLStatusKind.Unknown => RL.State.Unknown,
        _ => RL.State.Offline,
    };

    /// <summary>A distinct shape per state (Core's Symbol names the SF Symbol it stands in for).</summary>
    public static Geometry Shape(this RLStatus status) => StatusShapes.For(status.Kind);

    /// <summary>From a Core limit window, so every rail reads its status the same way.</summary>
    public static RLStatus ForWindow(LimitWindow window, double approaching = 60, double atLimit = 85, bool stale = false) =>
        RLStatus.ForUtilization(window.Utilization, approaching, atLimit, stale);
}

/// <summary>A status as a glyph, optionally with its words. Hide the label only where the words sit adjacent.</summary>
public class RLStatusIndicator : StackPanel
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(RLStatus), typeof(RLStatusIndicator),
        new PropertyMetadata(new RLStatus(RLStatusKind.Unknown), (d, _) => ((RLStatusIndicator)d).Update()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(RLStatusIndicator), new PropertyMetadata(13.0, (d, _) => ((RLStatusIndicator)d).Update()));
    public static readonly DependencyProperty ShowsLabelProperty = DependencyProperty.Register(
        nameof(ShowsLabel), typeof(bool), typeof(RLStatusIndicator), new PropertyMetadata(false, (d, _) => ((RLStatusIndicator)d).Update()));

    public RLStatus Status { get => (RLStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool ShowsLabel { get => (bool)GetValue(ShowsLabelProperty); set => SetValue(ShowsLabelProperty, value); }

    readonly ShapeIcon icon = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Sm, 0, 0, 0) };

    public RLStatusIndicator()
    {
        Orientation = Orientation.Horizontal;
        RL.Typography.Caption.Apply(label);
        label.Bind(TextBlock.ForegroundProperty, RL.Ink.Secondary);
        Children.Add(icon);
        Children.Add(label);
        Update();
    }

    public RLStatusIndicator(RLStatus status, double size = 13, bool showsLabel = false) : this()
    {
        Status = status; Size = size; ShowsLabel = showsLabel;
    }

    void Update()
    {
        icon.Shape = Status.Shape();
        icon.Size = Size;
        icon.Bind(ShapeIcon.ForegroundProperty, Status.Color());
        label.Text = Status.Phrase;
        label.Visibility = ShowsLabel ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, Status.Phrase);
    }
}
