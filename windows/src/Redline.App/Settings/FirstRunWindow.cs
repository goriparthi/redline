// Shown once, on the first launch, so the user chooses what RedLine reads. Reopened later it shows
// the current choices rather than resetting them. Port of FirstRun.swift; nothing here reaches the network.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Redline.App.Components;
using Redline.Core;

namespace Redline.App.Settings;

/// <summary>Start calls onDone with the sorted providers, the limits choice and the client id, then closes.</summary>
/// <remarks>A browser sign-in the user asked for is started by the caller after that.</remarks>
public sealed class FirstRunWindow : Window
{
    public delegate void DoneHandler(List<string> providers, ClaudeLimitsChoice choice, string clientId);

    readonly ProviderAvailability availability;
    readonly DoneHandler onDone;
    readonly HashSet<string> selection;
    ClaudeLimitsChoice limitsChoice;
    string clientId;

    readonly StackPanel body = new();
    readonly Border frame;
    readonly StackPanel limits = new();
    readonly Border limitsRule;
    readonly TextBlock pickOne;
    readonly Button start;
    readonly TextBlock choiceNote;
    readonly Grid clientField = new();
    readonly TextBox clientBox;

    static readonly Brush Chalk = Themed.Solid(RL.BrandTone.Chalk);
    static readonly Brush Steel = Themed.Solid(RL.BrandTone.Steel);
    static readonly Brush Signal = Themed.Solid(RL.BrandTone.Signal);

    public FirstRunWindow(ProviderAvailability availability, DoneHandler onDone,
                          IEnumerable<string>? currentProviders = null, bool useCLIToken = false,
                          string oauthClientId = "", bool feedInstalled = false, bool signedIn = false)
    {
        this.availability = availability;
        this.onDone = onDone;
        // First run pre-selects everything found ("read what I have"); reopened later it reflects
        // the config, so Start never silently changes an existing choice
        var current = (currentProviders ?? []).Where(availability.Has).ToList();
        selection = new HashSet<string>(current.Count == 0 ? availability.Installed : current, StringComparer.OrdinalIgnoreCase);
        // Reflects the choice the user made, not whichever artifact is on disk: Start writes
        // useCLIToken from this, so an under-reporting preselection silently reverted it
        limitsChoice = ClaudeLimitsChoiceRule.Current(feedInstalled, useCLIToken, signedIn);
        clientId = oauthClientId;

        Title = "Welcome to RedLine";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/RedLine;component/Assets/RedLine.ico")); } catch { }
        // This window paints Carbon in every theme, so its controls resolve as dark
        Background = Themed.Solid(RL.BrandTone.Carbon);
        ThemeManager.Pin(body, Theme.Dark);
        SettingsUI.UseStyles(body);
        ThemeManager.ApplyTitleBar(this, Theme.Dark);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new RedlineMark(34) { VerticalAlignment = VerticalAlignment.Center });
        var brand = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Words("RedLine", 22, FontWeights.Bold, Chalk));
        var tag = Words("Know your limit.", 12, FontWeights.Normal, Steel, mono: true);
        tag.Margin = new Thickness(0, 2, 0, 0);
        brand.Children.Add(tag);
        header.Children.Add(brand);
        Add(header, 0);
        Add(new Border { Width = 120, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Signal, HorizontalAlignment = HorizontalAlignment.Left });

        limitsRule = new Border { Height = 1, Background = Themed.Tint(Steel, 0.3) };
        choiceNote = Words("", 11, FontWeights.Normal, Steel, wrap: true);
        clientBox = new TextBox { Text = clientId };
        pickOne = Words("Pick at least one", 11, FontWeights.Normal, Signal);
        start = new Button { Content = availability.IsEmpty ? "Close" : "Start", IsDefault = true, MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right };

        if (availability.IsEmpty)
        {
            Add(Words("No supported tool found", 16, FontWeights.SemiBold, Chalk));
            Add(Words("RedLine reads Claude Code, Codex, and Ollama. None of them appear to be installed for this user, so there is nothing to report yet. Install one and reopen RedLine.",
                13, FontWeights.Normal, Steel, wrap: true));
        }
        else
        {
            Add(Words("What should RedLine read?", 16, FontWeights.SemiBold, Chalk));
            Add(Words("Everything is read from files already on this PC. You can change this later from the tray menu.",
                13, FontWeights.Normal, Steel, wrap: true));
            var list = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var provider in availability.Installed) list.Children.Add(ProviderToggle(provider, list.Children.Count > 0));
            Add(list);
            Add(limitsRule);
            BuildLimits();
            Add(limits);
        }

        // Starting with nothing ticked would silently keep the old choice, since an empty list
        // means "read everything" further down
        var footer = new Grid();
        footer.Children.Add(pickOne);
        pickOne.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(start);
        start.Click += (_, _) => Finish();
        Add(footer);

        frame = new Border { Child = body, Padding = new Thickness(RL.Space.Xxl), Background = Background };
        Content = frame;
        Refresh();
    }

    void Add(UIElement e, double gap = 18)
    {
        if (e is FrameworkElement fe && body.Children.Count > 0) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
        body.Children.Add(e);
    }

    static TextBlock Words(string s, double size, FontWeight weight, Brush ink, bool wrap = false, bool mono = false) => new()
    {
        Text = s, FontSize = size, FontWeight = weight, Foreground = ink,
        FontFamily = mono ? RL.Typography.Mono : RL.Typography.UI,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    UIElement ProviderToggle(string provider, bool spaced)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TrackBadge(provider, 20) { VerticalAlignment = VerticalAlignment.Center });
        var words = new StackPanel { Margin = new Thickness(RL.Space.Md, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Words(provider, 13, FontWeights.Medium, Chalk));
        var note = Words(Note(provider), 11, FontWeights.Normal, Steel);
        note.Margin = new Thickness(0, 1, 0, 0);
        words.Children.Add(note);
        row.Children.Add(words);
        var box = new CheckBox { Content = row, IsChecked = selection.Contains(provider), Margin = new Thickness(0, spaced ? 10 : 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(box, provider);
        box.Checked += (_, _) => { selection.Add(provider); Refresh(); };
        box.Unchecked += (_, _) => { selection.Remove(provider); Refresh(); };
        return box;
    }

    // Where the percentages come from is a decision, not a default, and each route carries its own
    // honest cost. The feed is recommended because it has none: no credentials at all.
    void BuildLimits()
    {
        limits.Children.Add(Words("Claude rate-limit percentages", 13, FontWeights.Medium, Chalk));
        var sub = Words("The same session and week percentages /usage shows. Everything else works with this off.", 11, FontWeights.Normal, Steel, wrap: true);
        sub.Margin = new Thickness(0, RL.Space.Md, 0, 0);
        limits.Children.Add(sub);
        var group = "limits-" + Guid.NewGuid().ToString("N");
        var radios = new StackPanel { Margin = new Thickness(0, RL.Space.Md, 0, 0) };
        foreach (var (choice, label) in new[]
                 {
                     (ClaudeLimitsChoice.Feed, "Read the statusline usage feed (recommended)"),
                     (ClaudeLimitsChoice.Browser, "Sign in with your Claude account"),
                     (ClaudeLimitsChoice.CliToken, "Use the Claude Code CLI's token"),
                     (ClaudeLimitsChoice.Off, "Don't show them"),
                 })
        {
            var r = new RadioButton
            {
                GroupName = group, IsChecked = choice == limitsChoice, Margin = new Thickness(0, radios.Children.Count > 0 ? RL.Space.Sm : 0, 0, 0),
                Content = Words(label, 13, FontWeights.Normal, Chalk), HorizontalAlignment = HorizontalAlignment.Left,
            };
            r.Checked += (_, _) => { limitsChoice = choice; Refresh(); };
            radios.Children.Add(r);
        }
        limits.Children.Add(radios);
        choiceNote.Margin = new Thickness(0, RL.Space.Md, 0, 0);
        limits.Children.Add(choiceNote);

        clientField.Margin = new Thickness(0, RL.Space.Md, 0, 0);
        var hint = Words("OAuth client id", 11, FontWeights.Normal, Steel, mono: true);
        hint.Margin = new Thickness(8, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        AutomationProperties.SetName(clientBox, "OAuth client id");
        clientBox.TextChanged += (_, _) =>
        {
            clientId = clientBox.Text;
            hint.Visibility = clientId.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
        hint.Visibility = clientId.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        clientField.Children.Add(clientBox);
        clientField.Children.Add(hint);
        limits.Children.Add(clientField);
    }

    bool ClaudeFound => availability.Has("Claude");
    string TrimmedClientId => clientId.Trim();

    bool StartDisabled
    {
        get
        {
            if (availability.IsEmpty) return false;
            if (selection.Count == 0) return true;
            // A browser sign-in with no client id is a dead end, so refuse it here
            return limitsChoice == ClaudeLimitsChoice.Browser && TrimmedClientId.Length == 0;
        }
    }

    void Refresh()
    {
        var showLimits = ClaudeFound && selection.Contains("Claude");
        limits.Visibility = limitsRule.Visibility = showLimits ? Visibility.Visible : Visibility.Collapsed;
        choiceNote.Text = limitsChoice switch
        {
            ClaudeLimitsChoice.Feed => "Claude Code hands its statusline command the rate-limit windows; RedLine installs a small wrapper that writes them to disk. A statusline you already have is kept and still draws the line. No sign-in, no Credential Manager, no network. The figures update while Claude Code runs and are shown greyed with their age in between.",
            ClaudeLimitsChoice.CliToken => "Needs Claude Code installed and signed in. Reads its token from its credentials file or Credential Manager and only ever reads it, never refreshes it, so it cannot sign the CLI out. The token is used against an undocumented endpoint, which may fall outside Anthropic's terms; that call is yours.",
            ClaudeLimitsChoice.Browser => "RedLine's own sign-in, separate from Claude Code's, so the percentages stay live between sessions and it works for claude.ai users with no CLI at all. Needs an OAuth client id, which Anthropic does not issue to third-party apps; the endpoint is undocumented and using it may fall outside their terms, which is your call to make.",
            _ => "",
        };
        choiceNote.Visibility = limitsChoice == ClaudeLimitsChoice.Off ? Visibility.Collapsed : Visibility.Visible;
        clientField.Visibility = limitsChoice == ClaudeLimitsChoice.Browser ? Visibility.Visible : Visibility.Collapsed;
        pickOne.Visibility = !availability.IsEmpty && selection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = !StartDisabled;
    }

    void Finish()
    {
        if (StartDisabled) return;
        var providers = selection.Select(p => availability.Installed.FirstOrDefault(i => string.Equals(i, p, StringComparison.OrdinalIgnoreCase)) ?? p)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        onDone(providers, limitsChoice, limitsChoice == ClaudeLimitsChoice.Browser ? TrimmedClientId : clientId);
        Close();
    }

    static string Note(string provider) => provider switch
    {
        "Claude" => "Tokens and cost from transcripts on disk",
        "Codex" => "Limits and tokens, read entirely from disk",
        "Ollama" => "Local models, plus token counts once tracking is set up",
        _ => "",
    };

#if DEBUG
    internal void SaveSnapshot(string path) =>
        SettingsSnapshot.Render(this, frame.ActualWidth, frame.ActualHeight, path, dc => SettingsSnapshot.Draw(dc, frame, 0, 0));
#endif
}
