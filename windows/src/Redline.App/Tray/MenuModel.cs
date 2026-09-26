// What the tray flyout shows, as data: the controller builds it, FlyoutWindow draws it.
// Port of the NSMenu that AppDelegate.rebuildMenu filled, with MenuRowView's inert rows as Info.
using System.Windows;
using System.Windows.Media;
using Redline.Core;

namespace Redline.App.Tray;

/// <summary>One coloured stretch of a row. A glyph run draws an element instead of text.</summary>
public sealed record MenuRun(string Text, Color? Color = null, bool Bold = false, Func<FrameworkElement>? Glyph = null)
{
    public static MenuRun Of(Func<FrameworkElement> glyph) => new("", null, false, glyph);
}

public abstract record MenuEntry;

/// <summary>A row that reads as information, not a control: no hover, no click (MenuRowView).</summary>
public sealed record MenuInfo(IReadOnlyList<MenuRun> Runs, bool Mono = false) : MenuEntry;

/// <summary>A clickable row. Rich runs replace the plain title when present.</summary>
public sealed record MenuAction(string Title, Action? OnClick, string? Shortcut = null, string? Tooltip = null,
                                bool Enabled = true, IReadOnlyList<MenuRun>? Rich = null,
                                Func<FrameworkElement>? Icon = null, int Indent = 0) : MenuEntry;

/// <summary>A row that opens a nested list, built when it opens so it is never stale.</summary>
public sealed record MenuSubmenu(IReadOnlyList<MenuRun> Title, Func<IReadOnlyList<MenuEntry>> Items,
                                 string? Tooltip = null) : MenuEntry;

public sealed record MenuSeparator : MenuEntry
{
    public static readonly MenuSeparator Instance = new();
}

/// <summary>The menu's inks. The flyout always paints the brand's dark ground, so these are fixed.</summary>
public static class MenuInk
{
    public static readonly Color Primary = RL.Ink.Primary.Dark;
    public static readonly Color Secondary = RL.Ink.Secondary.Dark;
    public static readonly Color Tertiary = RL.Ink.Muted.Dark;

    /// <summary>Lightened a quarter toward white, as contrasted() does on a dark menu.</summary>
    public static Color Contrasted(Color c) => Blend(c, Colors.White, 0.25);

    public static Color Blend(Color a, Color b, double f) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * f), (byte)Math.Round(a.G + (b.G - a.G) * f),
        (byte)Math.Round(a.B + (b.B - a.B) * f));

    /// <summary>A provider's accent on the dark menu (Brandkit.nsColor(for:)).</summary>
    public static Color Provider(string provider) => ProviderAccent.For(provider).Dark;

    /// <summary>A service tone as the dark palette draws it (Brandkit.nsTone).</summary>
    public static Color Tone(ServiceGlyph.Tone tone) => tone switch
    {
        ServiceGlyph.Tone.Healthy => RL.State.Success.Dark,
        ServiceGlyph.Tone.Warning => RL.State.Warning.Dark,
        ServiceGlyph.Tone.Critical => RL.State.Error.Dark,
        _ => RL.State.Unknown.Dark,
    };

    public static Color Status(double pct, Config config) =>
        Contrasted(RL.BrandTone.Of(Brand.Status(pct, config.LimitYellowPct, config.LimitRedPct).Color()));
}
