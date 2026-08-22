using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedLine.Core;
using Windows.Graphics;
using Windows.UI;

namespace RedLine.App;

/// <summary>
/// The settings page, built from what the engine publishes rather than from a list kept here.
/// Nothing in this file knows a setting by name, what it defaults to or what it will accept:
/// it reads a kind and renders the control for it, and the engine refuses anything it would
/// not load. The two exceptions are autostart and the usage feed, which are commands rather
/// than config, and so are asked for by name.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // Named for the window rather than as Width and Height: Window may grow properties by
    // those names, and a const that hides one is a warning nobody needs to read twice.
    private const int WindowWidth = 460;
    private const int WindowHeight = 660;

    private readonly SettingsStore settings;
    private readonly ToggleStore toggles;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>Raised after any change, so the rest of the app can re-read what it depends on.</summary>
    public event Action? Changed;

    /// <summary>
    /// How many controls the last load built. The self test reports it, because a settings
    /// page that renders nothing looks exactly like one that rendered fine.
    /// </summary>
    public int ControlCount { get; private set; }

    /// <summary>
    /// True while the controls are being filled in. Setting a value raises the same event as
    /// someone changing it, and without this every load would write every setting straight
    /// back to the engine.
    /// </summary>
    private bool building;

    public SettingsWindow(SettingsStore? settings = null, ToggleStore? toggles = null)
    {
        InitializeComponent();
        this.settings = settings ?? new SettingsStore();
        this.toggles = toggles ?? new ToggleStore();
        Title = "RedLine Settings";
        Shape();
        Load();
    }

    private static SolidColorBrush Chalk => new(Color.FromArgb(255, 0xF4, 0xF1, 0xEA));
    private static SolidColorBrush Muted => new(Color.FromArgb(255, 0x84, 0x8A, 0x96));
    private static SolidColorBrush Signal => new(Color.FromArgb(255, 0xFF, 0x3B, 0x30));

    private void Shape()
    {
        var window = AppWindow;
        if (window is null) return;

        window.Title = "RedLine Settings";
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }
        window.Resize(new SizeInt32(WindowWidth, WindowHeight));

        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            var work = area.WorkArea;
            window.Move(new PointInt32(work.X + ((work.Width - WindowWidth) / 2),
                                       work.Y + ((work.Height - WindowHeight) / 2)));
        }
    }

    /// <summary>
    /// Asks the engine for everything and builds a control per answer. Synchronous on purpose:
    /// it is three short runs of a local binary, and a page that populates after it appears
    /// would let someone change a control that is about to be replaced.
    /// </summary>
    public void Load()
    {
        building = true;
        try
        {
            Rows.Children.Clear();
            ControlCount = 0;

            var catalog = settings.Read();
            if (!catalog.Available)
            {
                Rows.Children.Add(Note(catalog.Problem, Signal));
            }
            foreach (var setting in catalog.Settings)
            {
                Rows.Children.Add(Row(setting));
            }

            Rows.Children.Add(Heading("On this computer"));
            Rows.Children.Add(AutostartRow());
            Rows.Children.Add(UsageFeedRow());
        }
        finally
        {
            building = false;
        }
    }

    // One row per setting, and the control it needs

    private FrameworkElement Row(SettingDefinition setting) => setting.Kind switch
    {
        SettingKind.Bool => BoolRow(setting),
        SettingKind.Number => Stacked(setting, NumberControl(setting)),
        SettingKind.Choice => Stacked(setting, ChoiceControl(setting)),
        SettingKind.List => Stacked(setting, ListControl(setting)),
        // A kind this build has never seen is shown as what it is rather than hidden, so a
        // newer engine gains a setting here instead of losing one silently
        _ => Stacked(setting, Note($"{setting.Value} (this build has no control for this)",
                                   Muted)),
    };

    private FrameworkElement BoolRow(SettingDefinition setting)
    {
        var row = TwoColumns();
        var text = Describe(setting);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = setting.IsOn,
            OnContent = "",
            OffContent = "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Toggled += (_, _) =>
        {
            if (building) return;
            // Read here, not in the lambda: that runs off the UI thread, and touching a
            // control from one is a crash rather than a wrong answer.
            var wanted = toggle.IsOn;
            Apply(() => Said(settings.Write(setting.Key, wanted)), toggle);
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        ControlCount++;
        return row;
    }

    private FrameworkElement NumberControl(SettingDefinition setting)
    {
        var box = new NumberBox
        {
            Value = setting.Number ?? 0,
            Minimum = setting.Min ?? 0,
            Maximum = setting.Max ?? double.MaxValue,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.ValueChanged += (_, args) =>
        {
            // Emptying the box is not a value, and writing NaN would be a refusal for
            // something nobody asked for
            if (building || double.IsNaN(args.NewValue)) return;
            Apply(() => Said(settings.Write(setting.Key, args.NewValue)), box);
        };
        ControlCount++;
        return box;
    }

    private FrameworkElement ChoiceControl(SettingDefinition setting)
    {
        var combo = new ComboBox
        {
            ItemsSource = setting.Allowed.ToList(),
            SelectedItem = setting.Value,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (building || combo.SelectedItem is not string picked) return;
            Apply(() => Said(settings.Write(setting.Key, picked)), combo);
        };
        ControlCount++;
        return combo;
    }

    private FrameworkElement ListControl(SettingDefinition setting)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var on = new HashSet<string>(setting.Selected, StringComparer.OrdinalIgnoreCase);
        var boxes = new List<CheckBox>();

        void Write()
        {
            if (building) return;
            var picked = boxes.Where(b => b.IsChecked == true)
                              .Select(b => (string)b.Content)
                              .ToList();
            // Turning the last one off is the engine's call to refuse, not this window's
            Apply(() => Said(settings.Write(setting.Key, picked)), boxes.ToArray());
        }

        foreach (var option in setting.Allowed)
        {
            var box = new CheckBox
            {
                Content = option,
                IsChecked = on.Contains(option),
                MinWidth = 0,
                Foreground = Chalk,
            };
            box.Checked += (_, _) => Write();
            box.Unchecked += (_, _) => Write();
            boxes.Add(box);
            row.Children.Add(box);
            ControlCount++;
        }
        return row;
    }

    private FrameworkElement AutostartRow()
    {
        var state = toggles.Autostart();
        return CommandRow(
            "Start RedLine when you sign in",
            state.Known && state.Name.Length > 0 ? state.Name : ToggleStore.AutostartKey,
            state,
            on => toggles.SetAutostart(on));
    }

    private FrameworkElement UsageFeedRow()
    {
        var state = toggles.UsageFeed();
        return CommandRow(
            "Let Claude Code report your limits",
            "wires its statusline, and keeps any statusline you already have",
            state,
            toggles.SetUsageFeed);
    }

    /// <summary>
    /// A setting that is a command rather than a value. The switch is dead when the engine
    /// could not be asked, because a switch that moves without changing anything is a lie.
    /// </summary>
    private FrameworkElement CommandRow(string label, string detail, ToggleOutcome state,
                                        Func<bool, ToggleOutcome> write)
    {
        var row = TwoColumns();
        var text = Describe(label, state.Known ? detail : state.Message,
                            state.Known ? Muted : Signal);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = state.Known && state.On,
            IsEnabled = state.Known,
            OnContent = "",
            OffContent = "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Toggled += (_, _) =>
        {
            if (building) return;
            var wanted = toggle.IsOn;
            Apply(() => Said(write(wanted)), toggle);
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        ControlCount++;
        return row;
    }

    // Writing a change back

    /// <summary>What a write is reduced to: whether it stuck, and what to say about it.</summary>
    private readonly record struct Applied(bool Accepted, string Message);

    private static Applied Said(SettingsOutcome outcome) => new(
        outcome.Accepted,
        outcome.Kind == SettingsOutcomeKind.Changed
            ? $"{outcome.Key} is now {outcome.Value}"
            : outcome.Message);

    private static Applied Said(ToggleOutcome outcome) => new(
        outcome.Accepted,
        outcome.Message.Length > 0 ? outcome.Message : outcome.Detail);

    /// <summary>
    /// Runs the change off the UI thread, because it starts a process, and puts the controls
    /// back the way the engine has them if it refused. The engine is the only thing that knows
    /// what is stored, so a refusal is answered by asking it again rather than by guessing.
    /// </summary>
    private void Apply(Func<Applied> write, params Control[] controls)
    {
        foreach (var control in controls) control.IsEnabled = false;

        Task.Run(() =>
        {
            var applied = write();
            dispatcher.TryEnqueue(() =>
            {
                foreach (var control in controls) control.IsEnabled = true;
                FooterText.Text = applied.Message;
                if (!applied.Accepted) Load();
                Changed?.Invoke();
            });
        });
    }

    // The pieces every row is made of

    private static Grid TwoColumns()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    /// <summary>
    /// The engine's own summary is the label. Turning "limitYellowPct" into a phrase would be
    /// this window inventing a name for something it is not supposed to know about.
    /// </summary>
    private static StackPanel Describe(SettingDefinition setting) =>
        Describe(Sentence(setting.Summary), setting.Key, Muted);

    private static StackPanel Describe(string label, string detail, Brush detailColour)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Chalk,
            TextWrapping = TextWrapping.Wrap,
        });
        if (detail.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Foreground = detailColour,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return stack;
    }

    private FrameworkElement Stacked(SettingDefinition setting, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Describe(setting));
        stack.Children.Add(control);
        return stack;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = Muted,
        CharacterSpacing = 180,
        Margin = new Thickness(0, 12, 0, 0),
    };

    private static TextBlock Note(string text, Brush colour) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = colour,
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
