// A segmented choice drawn with plain buttons, because a system control takes the system accent
// and this app's selection colour is its own. Segment look lives in Themes/Generic.xaml.
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Redline.App.Components;

/// <summary>One option: the value it selects, its short label, and an optional tooltip.</summary>
public sealed record RLSegment(object Value, string Label, string? Help = null);

public class RLSegmented : StackPanel
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IList), typeof(RLSegmented), new PropertyMetadata(null, (d, _) => ((RLSegmented)d).Rebuild()));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(object), typeof(RLSegmented),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((RLSegmented)d).Sync()));
    public static readonly DependencyProperty SegmentWidthProperty = DependencyProperty.Register(
        nameof(SegmentWidth), typeof(double), typeof(RLSegmented), new PropertyMetadata(40.0, (d, _) => ((RLSegmented)d).Rebuild()));

    /// <summary>A list of RLSegment.</summary>
    public IList? Options { get => (IList?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public object? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public double SegmentWidth { get => (double)GetValue(SegmentWidthProperty); set => SetValue(SegmentWidthProperty, value); }

    /// <summary>Raised when the user picks a segment (onSelect in Swift), with the new value.</summary>
    public event EventHandler<object?>? Selected;

    readonly string group = "rlseg-" + Guid.NewGuid().ToString("N");
    bool syncing;

    public RLSegmented()
    {
        Orientation = Orientation.Horizontal;
    }

    public RLSegmented(IEnumerable<RLSegment> options, object? selection, double width = 40) : this()
    {
        SegmentWidth = width;
        Options = options.ToList();
        Selection = selection;
    }

    void Rebuild()
    {
        Children.Clear();
        if (Options is null) return;
        var first = true;
        foreach (var o in Options.OfType<RLSegment>())
        {
            var b = new RLSegmentButton
            {
                Content = o.Label, Tag = o.Value, GroupName = group, Width = SegmentWidth,
                ToolTip = string.IsNullOrEmpty(o.Help) ? null : o.Help,
                Margin = new Thickness(first ? 0 : RL.Space.Xs, 0, 0, 0),
            };
            AutomationProperties.SetName(b, o.Help ?? o.Label);
            b.Checked += (_, _) =>
            {
                if (syncing) return;
                Selection = b.Tag;
                Selected?.Invoke(this, b.Tag);
            };
            Children.Add(b);
            first = false;
        }
        Sync();
    }

    void Sync()
    {
        syncing = true;
        foreach (var b in Children.OfType<RLSegmentButton>()) b.IsChecked = Equals(b.Tag, Selection);
        syncing = false;
    }
}

/// <summary>One segment. A RadioButton, so assistive technology reads it as a selectable item.</summary>
public class RLSegmentButton : RadioButton
{
    static RLSegmentButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RLSegmentButton), new FrameworkPropertyMetadata(typeof(RLSegmentButton)));
    }
}
