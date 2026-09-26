// DEBUG only: `--settings-sample [--section <name>] [--theme dark|light] --snapshot out.png` and
// `--firstrun-sample [--choice feed|browser|cli|off] --snapshot out.png`, against a throwaway config.
#if DEBUG
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Redline.Core;

namespace Redline.App.Settings;

internal static class SettingsSnapshot
{
    /// <summary>Draws onto a w by h canvas at the screen's DPI and writes it to a PNG.</summary>
    public static void Render(Visual dpiSource, double w, double h, string path, Action<DrawingContext> draw)
    {
        var dpi = VisualTreeHelper.GetDpi(dpiSource);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>An element as a brush at its own full arranged size, unclipped by any scroll viewer above it.</summary>
    public static void Draw(DrawingContext dc, FrameworkElement e, double x, double y)
    {
        // The brush draws the visual with its own offset, so the viewbox starts there
        var o = VisualTreeHelper.GetOffset(e);
        dc.DrawRectangle(new VisualBrush(e) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(o.X, o.Y, e.ActualWidth, e.ActualHeight) },
            null, new Rect(x, y, e.ActualWidth, e.ActualHeight));
    }
}

internal static class SettingsSamples
{
    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Handles the two sample flags. True when one was present, so startup stops there.</summary>
    public static bool TryRun(string[] args)
    {
        var settings = args.Contains("--settings-sample");
        var firstRun = args.Contains("--firstrun-sample");
        if (!settings && !firstRun) return false;
        if (Arg(args, "--theme") is { } t)
            ThemeManager.Mode = t.ToLowerInvariant() switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.Auto };
        var snapshot = Arg(args, "--snapshot");
        // --edge: one provider left on, one missing, clashing thresholds, signed in and set up
        var edge = args.Contains("--edge");
        var available = new ProviderAvailability(edge ? ["Claude", "Codex"] : ["Claude", "Codex", "Ollama"]);
        if (Arg(args, "--choice") == "none") available = new ProviderAvailability([]);

        Window window;
        Action<string> save;
        if (settings)
        {
            var model = SampleModel(available, edge);
            if (SettingsSectionInfo.Parse(Arg(args, "--section")) is { } section) model.Section = section;
            var w = new SettingsWindow(model);
            window = w;
            save = w.SaveSnapshot;
        }
        else
        {
            var choice = Arg(args, "--choice");
            var w = new FirstRunWindow(available, (_, _, _) => { },
                useCLIToken: choice == "cli", signedIn: choice == "browser");
            window = w;
            save = w.SaveSnapshot;
            if (choice == "off") w.Loaded += (_, _) => SelectOff(w);
        }
        Application.Current.MainWindow = window;
        window.Show();
        if (snapshot is not null)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                save(Path.GetFullPath(snapshot));
                window.Close();
                Application.Current.Shutdown();
            });
        return true;
    }

    static void SelectOff(FirstRunWindow w)
    {
        foreach (var r in Descendants(w).OfType<System.Windows.Controls.RadioButton>())
            if (r.Content is System.Windows.Controls.TextBlock { Text: "Don't show them" }) r.IsChecked = true;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    /// <summary>A model over a temp config, with the app-owned setters writing that same file.</summary>
    static SettingsModel SampleModel(ProviderAvailability available, bool edge)
    {
        var dir = Path.Combine(Path.GetTempPath(), "redline-settings-sample");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        if (File.Exists(path)) File.Delete(path);
        if (edge)
        {
            Config.WriteDefault(path);
            Config.Write(new() { ["providers"] = new JsonArray("Claude"), ["limitYellowPct"] = 90, ["limitRedPct"] = 85, ["mindfulCues"] = false, ["findingsScans"] = false }, path);
        }
        var model = new SettingsModel(Config.Load(path), path);
        void W(string key, JsonNode? v) => Config.Write(new() { [key] = v }, path);
        var launch = false;
        model.Actions = new SettingsActions
        {
            SetProviders = p => W("providers", new JsonArray(p.Select(x => (JsonNode)x).ToArray())),
            SetCLIToken = v => W("useCLIToken", v),
            SetAlerts = v => W("alerts", v),
            SetCues = v => W("mindfulCues", v),
            SetHistory = v => W("recordHistory", v),
            SetSidecar = v => W("publishSidecar", v),
            SetStatusChecks = v => W("statusChecks", v),
            SetAutoUpdates = v => W("autoCheckUpdates", v),
            SetUpdateChannel = v => W("updateChannel", v),
            SetAgentFleet = v => W("agentFleet", v),
            SetMenuIcon = v => W("showMenuIcon", v),
            SetResetTimes = v => W("showResetTimes", v),
            SetLimitWindows = v => W("limitWindows", v),
            SetMenuBarProvider = v => W("menuBarProvider", v),
            SetTheme = v =>
            {
                W("dashboardTheme", v);
                ThemeManager.Mode = v switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.Auto };
            },
            ToggleLaunchAtLogin = () => { launch = !launch; model.State = model.State with { LaunchAtLogin = launch }; },
            OpenNotificationSettings = SettingsShell.OpenNotificationSettings,
            OpenNotice = SettingsShell.OpenNotice,
        };
        model.State = new SettingsEnvironmentState
        {
            Availability = available,
            SignedIn = edge, ClaudeFeedInstalled = edge, OllamaShimInstalled = edge, LaunchAtLogin = edge,
            AppVersion = typeof(SettingsSamples).Assembly.GetName().Version?.ToString(3) ?? "",
        };
        return model;
    }
}
#endif
