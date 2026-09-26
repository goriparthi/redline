// Swaps Themes/Dark.xaml and Themes/Light.xaml in the app's resources, following Windows or forced.
// Components read colours through DynamicResource, so a swap repaints everything already on screen.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Redline.Core;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Redline.App;

/// <summary>What the user chose: follow Windows, or force one appearance.</summary>
public enum ThemeMode { Auto, Light, Dark }

public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static readonly Uri SharedUri = new("pack://application:,,,/RedLine;component/Themes/Shared.xaml");
    static readonly Uri DarkUri = new("pack://application:,,,/RedLine;component/Themes/Dark.xaml");
    static readonly Uri LightUri = new("pack://application:,,,/RedLine;component/Themes/Light.xaml");

    static ThemeMode mode = ThemeMode.Auto;
    static bool listening;

    /// <summary>The appearance currently applied to the application's resources.</summary>
    public static Theme Current { get; private set; } = Theme.Dark;

    /// <summary>Raised on the UI thread after the resolved theme changes.</summary>
    public static event EventHandler<Theme>? ThemeChanged;

    public static ThemeMode Mode
    {
        get => mode;
        set { mode = value; Apply(); }
    }

    /// <summary>Merges Shared.xaml and the resolved palette into Application.Resources and starts following Windows.</summary>
    public static void Initialize(ThemeMode initial = ThemeMode.Auto)
    {
        var res = Application.Current.Resources.MergedDictionaries;
        if (!res.Any(d => d.Source == SharedUri)) res.Insert(0, new ResourceDictionary { Source = SharedUri });
        if (!listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            listening = true;
        }
        mode = initial;
        Apply(force: true);
    }

    /// <summary>Stops listening to Windows. Call from Application.Exit; SystemEvents holds a static reference.</summary>
    public static void Shutdown()
    {
        if (!listening) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        listening = false;
    }

    /// <summary>True when Windows apps are set to light. A missing key means light, as Windows treats it.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Theme Resolve(ThemeMode m) => m switch
    {
        ThemeMode.Light => Theme.Light,
        ThemeMode.Dark => Theme.Dark,
        _ => SystemUsesLightTheme() ? Theme.Light : Theme.Dark,
    };

    /// <summary>A fresh palette dictionary, for pinning a subtree to one theme (the widget, the gallery).</summary>
    public static ResourceDictionary Load(Theme theme)
    {
        var d = new ResourceDictionary { Source = theme == Theme.Dark ? DarkUri : LightUri };
        VerifyAgainstTokens(d, theme);
        return d;
    }

    /// <summary>Pins an element and its children to one theme regardless of the app setting.</summary>
    public static void Pin(FrameworkElement element, Theme theme)
    {
        var merged = element.Resources.MergedDictionaries;
        foreach (var old in merged.Where(IsPalette).ToList()) merged.Remove(old);
        merged.Add(Load(theme));
    }

    /// <summary>Matches a window's title bar to the theme (Windows 10 20H1 and later; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window, Theme? theme = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window, theme);
            return;
        }
        int dark = (theme ?? Current) == Theme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    static void Apply(bool force = false)
    {
        var next = Resolve(mode);
        if (!force && next == Current) return;
        var merged = Application.Current.Resources.MergedDictionaries;
        foreach (var old in merged.Where(IsPalette).ToList()) merged.Remove(old);
        merged.Add(Load(next));
        Current = next;
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w, next);
        ThemeChanged?.Invoke(null, next);
    }

    static bool IsPalette(ResourceDictionary d) => d.Source == DarkUri || d.Source == LightUri;

    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows reports an app theme switch as a General category change
        if (mode != ThemeMode.Auto || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply());
    }

    [Conditional("DEBUG")]
    static void VerifyAgainstTokens(ResourceDictionary d, Theme theme)
    {
        foreach (var t in RL.AllColors)
        {
            Debug.Assert(d[t.Key] is Color c && c == t.Resolve(theme), $"{theme}.xaml {t.Key} differs from DesignSystem.cs");
            Debug.Assert(d[t.BrushKey] is SolidColorBrush b && b.Color == t.Resolve(theme), $"{theme}.xaml {t.BrushKey} differs");
        }
        foreach (var (token, core) in new[] { (RL.Accent.Codex, ProviderIdentity.CodexAccent), (RL.Accent.Anthropic, ProviderIdentity.AnthropicAccent),
                                              (RL.Accent.Ollama, ProviderIdentity.OllamaAccent), (RL.Accent.Neutral, ProviderIdentity.NeutralAccent) })
            Debug.Assert(token.Dark == RL.BrandTone.Of(core.Dark) && token.Light == RL.BrandTone.Of(core.Light), $"{token.Key} differs from Core");
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
