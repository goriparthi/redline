// The design system: every colour, space, radius and text style the UI is allowed to use.
// Port of DesignSystem.swift; Themes/Dark.xaml and Themes/Light.xaml carry the same values.
using System.Windows;
using System.Windows.Media;
using Redline.Core;

namespace Redline.App;

/// <summary>The two resolved appearances. Dark is the design's home; light is a real equivalent.</summary>
public enum Theme { Dark, Light }

/// <summary>One colour token with its per-appearance values and its resource keys.</summary>
public sealed class ColorToken
{
    public string Key { get; }
    public Color Dark { get; }
    public Color Light { get; }

    public ColorToken(string key, uint dark, uint light)
    {
        Key = key;
        Dark = RL.Hex(dark);
        Light = RL.Hex(light);
    }

    /// <summary>Resource key of the frozen SolidColorBrush in the theme dictionaries.</summary>
    public string BrushKey => Key + ".Brush";

    public Color Resolve(Theme theme) => theme == Theme.Dark ? Dark : Light;

    /// <summary>The value for the app's current theme. Prefer DynamicResource for anything that must follow a switch.</summary>
    public Color Current => Resolve(ThemeManager.Current);

    public SolidColorBrush Brush(Theme theme, double opacity = 1)
    {
        var b = new SolidColorBrush(Resolve(theme)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    public override string ToString() => Key;
}

/// <summary>A text style: family, size and weight. Numbers are monospaced everywhere.</summary>
public sealed record TextStyle(string Key, FontFamily Family, double Size, FontWeight Weight, bool Mono)
{
    public void Apply(System.Windows.Controls.TextBlock t)
    {
        t.FontFamily = Family;
        t.FontSize = Size;
        t.FontWeight = Weight;
    }
}

/// <summary>Namespace for the tokens. Short on purpose, because it is read at every call site.</summary>
public static class RL
{
    public static Color Hex(uint rgb) =>
        Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    /// <summary>Background layers. Depth comes from these plus a hairline border, never a heavy shadow.</summary>
    public static class Surface
    {
        /// <summary>The window's own ground.</summary>
        public static readonly ColorToken Ground = new("RL.Surface.Ground", 0x0B0D10, 0xF4F1EA);
        /// <summary>Cards and panels sitting on the ground.</summary>
        public static readonly ColorToken Raised = new("RL.Surface.Raised", 0x171A1F, 0xFFFFFF);
        /// <summary>Wells inside a card: chart grounds, evidence blocks, progress tracks.</summary>
        public static readonly ColorToken Sunken = new("RL.Surface.Sunken", 0x07090B, 0xE8E4DB);
        /// <summary>Popovers and hover readouts, which sit above everything else.</summary>
        public static readonly ColorToken Overlay = new("RL.Surface.Overlay", 0x1D2128, 0xFFFFFF);
        /// <summary>A card the pointer is over. One step, not a colour change.</summary>
        public static readonly ColorToken RaisedHover = new("RL.Surface.RaisedHover", 0x1D2128, 0xFAF8F3);
    }

    /// <summary>Text. Three weights of emphasis is the whole vocabulary.</summary>
    public static class Ink
    {
        public static readonly ColorToken Primary = new("RL.Ink.Primary", 0xF4F1EA, 0x14171C);
        public static readonly ColorToken Secondary = new("RL.Ink.Secondary", 0xA8AEBA, 0x4E5462);
        public static readonly ColorToken Muted = new("RL.Ink.Muted", 0x848A96, 0x6B7280);
        /// <summary>On top of a filled brand or state colour.</summary>
        public static readonly ColorToken OnAccent = new("RL.Ink.OnAccent", 0x0B0D10, 0xFFFFFF);
    }

    /// <summary>Borders and separators. A card is a fill plus one of these.</summary>
    public static class Stroke
    {
        /// <summary>Separators inside a card.</summary>
        public static readonly ColorToken Hairline = new("RL.Stroke.Hairline", 0x262A32, 0xDDD8CE);
        /// <summary>A card's own edge.</summary>
        public static readonly ColorToken Border = new("RL.Stroke.Border", 0x323843, 0xC7C2B7);
        /// <summary>A card under the pointer, or one carrying a selection.</summary>
        public static readonly ColorToken BorderStrong = new("RL.Stroke.BorderStrong", 0x4A515F, 0xA9A398);
    }

    /// <summary>Status colour, always paired with a shape and a word (see RLStatus).</summary>
    public static class State
    {
        public static readonly ColorToken Success = new("RL.State.Success", 0x32D74B, 0x1E8E3E);
        public static readonly ColorToken Warning = new("RL.State.Warning", 0xFF9F0A, 0xA85C00);
        public static readonly ColorToken Error = new("RL.State.Error", 0xFF3B30, 0xC9271D);
        /// <summary>A provider that is installed but not answering.</summary>
        public static readonly ColorToken Offline = new("RL.State.Offline", 0x848A96, 0x6B7280);
        /// <summary>Nothing has been checked, so nothing is claimed.</summary>
        public static readonly ColorToken Unknown = new("RL.State.Unknown", 0x6E7480, 0x7A8090);
    }

    /// <summary>RedLine's own identity. The red rule stays the strongest colour on any screen.</summary>
    public static class Brandmark
    {
        public static readonly ColorToken Signal = new("RL.Brandmark.Signal", 0xFF3B30, 0xC9271D);
        /// <summary>Money reads green by convention, tuned per appearance.</summary>
        public static readonly ColorToken Money = new("RL.Brandmark.Money", 0x32D74B, 0x1E8E3E);
    }

    /// <summary>A provider's accent, used around a mark and never on the mark itself.</summary>
    public static class Accent
    {
        public static readonly ColorToken Codex = new("RL.Accent.Codex", 0x45C4D4, 0x0C6D7C);
        public static readonly ColorToken Anthropic = new("RL.Accent.Anthropic", 0xD9A05B, 0x8A6118);
        public static readonly ColorToken Ollama = new("RL.Accent.Ollama", 0x9888D4, 0x5F44A6);
        /// <summary>Every provider at once, or none named: the product's own neutral.</summary>
        public static readonly ColorToken Neutral = new("RL.Accent.Neutral", 0xA8AEBA, 0x4E5462);
    }

    /// <summary>Fixed brand tones (Core Brand, BrandUI.swift) for surfaces that paint Carbon in every theme.</summary>
    public static class BrandTone
    {
        public static Color Of(BrandColor c) => Color.FromRgb(c.R, c.G, c.B);

        public static readonly Color Carbon = Of(Brand.Carbon);
        public static readonly Color Graphite = Of(Brand.Graphite);
        public static readonly Color Steel = Of(Brand.Steel);
        public static readonly Color Chalk = Of(Brand.Chalk);
        public static readonly Color Signal = Of(Brand.Signal);
        public static readonly Color Amber = Of(Brand.Amber);
        public static readonly Color Clear = Of(Brand.Clear);

        /// <summary>Healthy, approaching or at the limit, from a utilization percentage.</summary>
        public static Color StatusColor(double utilization, double approaching = 60, double atLimit = 85) =>
            Of(Brand.Status(utilization, approaching, atLimit).Color());
    }

    /// <summary>Every colour token, for the theme dictionaries and their consistency check.</summary>
    public static readonly IReadOnlyList<ColorToken> AllColors =
    [
        Surface.Ground, Surface.Raised, Surface.Sunken, Surface.Overlay, Surface.RaisedHover,
        Ink.Primary, Ink.Secondary, Ink.Muted, Ink.OnAccent,
        Stroke.Hairline, Stroke.Border, Stroke.BorderStrong,
        State.Success, State.Warning, State.Error, State.Offline, State.Unknown,
        Brandmark.Signal, Brandmark.Money,
        Accent.Codex, Accent.Anthropic, Accent.Ollama, Accent.Neutral,
    ];

    /// <summary>A 2px grid. Anything not on it is a one-off.</summary>
    public static class Space
    {
        public const double Xxs = 2;
        public const double Xs = 4;
        public const double Sm = 6;
        public const double Md = 8;
        public const double Lg = 12;
        public const double Xl = 16;
        public const double Xxl = 22;
        public const double Xxxl = 32;
    }

    public static class Radius
    {
        public const double Chip = 5;
        public const double Control = 7;
        public const double Card = 12;
        public const double Window = 16;
    }

    /// <summary>One typographic scale, sizes in DIPs taken 1:1 from the SwiftUI points.</summary>
    public static class Typography
    {
        public static readonly FontFamily UI = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily UIDisplay = new("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
        /// <summary>Segoe Fluent Icons on Windows 11, MDL2 Assets on Windows 10.</summary>
        public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static readonly TextStyle Display = new("RL.Text.Display", Mono, 25, FontWeights.SemiBold, true);
        public static readonly TextStyle Title = new("RL.Text.Title", UIDisplay, 24, FontWeights.Bold, false);
        public static readonly TextStyle Heading = new("RL.Text.Heading", UI, 16, FontWeights.SemiBold, false);
        public static readonly TextStyle Subheading = new("RL.Text.Subheading", UI, 14, FontWeights.Medium, false);
        public static readonly TextStyle Body = new("RL.Text.Body", UI, 13, FontWeights.Normal, false);
        public static readonly TextStyle Caption = new("RL.Text.Caption", UI, 11, FontWeights.Normal, false);
        /// <summary>Section headers: small, tracked, monospaced, upper case.</summary>
        public static readonly TextStyle Label = new("RL.Text.Label", Mono, 12, FontWeights.Medium, true);
        public static readonly TextStyle MonoBody = new("RL.Text.Mono", Mono, 13, FontWeights.Normal, true);
        public static readonly TextStyle MonoSmall = new("RL.Text.MonoSmall", Mono, 11, FontWeights.Normal, true);
        public static readonly TextStyle MonoStrong = new("RL.Text.MonoStrong", Mono, 13, FontWeights.SemiBold, true);
        /// <summary>The tracking that goes with Label, in DIPs. Upper case without it reads as shouting.</summary>
        public const double LabelTracking = 1.4;

        public static readonly IReadOnlyList<TextStyle> All =
            [Display, Title, Heading, Subheading, Body, Caption, Label, MonoBody, MonoSmall, MonoStrong];
    }

    /// <summary>Named durations in seconds, switched off wholesale when animations are off.</summary>
    public static class Motion
    {
        /// <summary>Hover and selection feedback.</summary>
        public const double Hover = 0.12;
        /// <summary>A rail or a number moving to a new value.</summary>
        public const double Value = 0.45;
        /// <summary>Content appearing or being replaced.</summary>
        public const double Content = 0.22;

        /// <summary>Windows' "Animation effects" switch is the Reduce Motion equivalent.</summary>
        public static bool Reduced => !SystemParameters.ClientAreaAnimation;

        public static Duration For(double seconds) => new(TimeSpan.FromSeconds(Reduced ? 0 : seconds));
    }
}

/// <summary>WPF colour tokens for Core's ProviderIdentity. The accent surrounds a mark, never tints it.</summary>
public static class ProviderAccent
{
    public static ColorToken Token(this ProviderIdentity identity) => identity.Mark switch
    {
        ProviderMark.Codex => RL.Accent.Codex,
        ProviderMark.Anthropic or ProviderMark.Claude => RL.Accent.Anthropic,
        _ => RL.Accent.Ollama,
    };

    /// <summary>The accent for a provider name, falling back to the product's neutral.</summary>
    public static ColorToken For(string? provider) => ProviderIdentity.Of(provider)?.Token() ?? RL.Accent.Neutral;
}
