// A section header: small, tracked, quiet, with room for a note and a trailing element on the right.
// Used for every panel so no two sections announce themselves differently.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Redline.App.Components;

[ContentProperty(nameof(Trailing))]
public class RLSectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RLSectionHeader), new PropertyMetadata("", (d, _) => ((RLSectionHeader)d).Update()));
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(RLSectionHeader), new PropertyMetadata(null, (d, _) => ((RLSectionHeader)d).Update()));
    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing), typeof(object), typeof(RLSectionHeader), new PropertyMetadata(null, (d, _) => ((RLSectionHeader)d).Update()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Note { get => (string?)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    public object? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    readonly TrackedText title = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock note = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(RL.Space.Md, 0, 0, 0) };
    readonly ContentPresenter trailing = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(RL.Space.Md, 0, 0, 0) };

    public RLSectionHeader()
    {
        title.TypeStyle = RL.Typography.Label;
        title.Bind(TrackedText.ForegroundProperty, RL.Ink.Muted);
        RL.Typography.MonoSmall.Apply(note);
        note.Bind(TextBlock.ForegroundProperty, RL.Ink.Muted);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(note, 1);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(title);
        grid.Children.Add(note);
        grid.Children.Add(trailing);
        Content = grid;
        Focusable = false;
        IsTabStop = false;
    }

    public RLSectionHeader(string title, string? note = null, object? trailing = null) : this()
    {
        Title = title; Note = note; Trailing = trailing;
    }

    void Update()
    {
        title.Text = Title;
        note.Text = Note ?? "";
        note.Visibility = Note is null ? Visibility.Collapsed : Visibility.Visible;
        trailing.Content = Trailing;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHeadingLevel(this, AutomationHeadingLevel.Level2);
    }
}
