// DEBUG only: `--flyout-sample`, `--widget-sample` and `--tray-sample`, each with an optional
// `--snapshot out.png`, render the tray surfaces over invented data for review without looking.
#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Redline.App.Dashboard;
using Redline.Core;

namespace Redline.App.Tray;

public static class TraySamples
{
    public static bool TryRun(string[] args)
    {
        string? Arg(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var snapshot = Arg("--snapshot");
        var app = Application.Current;
        if (args.Contains("--flyout-sample"))
        {
            var menu = AppController.SampleMenu();
            var flyout = new FlyoutWindow();
            app.MainWindow = flyout;
            flyout.ShowAt(menu, System.Windows.Forms.Cursor.Position);
            if (snapshot is not null) SaveAndExit(flyout, (FrameworkElement)flyout.Content, snapshot);
            return true;
        }
        if (args.Contains("--widget-sample"))
        {
            var snap = SampleSnapshot();
            var grid = new StackPanel { Margin = new Thickness(16) };
            foreach (var track in Enum.GetValues<TrackChoice>())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var family in Enum.GetValues<WidgetFamily>())
                {
                    var card = WidgetView.Build(snap, track, family);
                    card.Margin = new Thickness(8);
                    card.VerticalAlignment = VerticalAlignment.Top;
                    row.Children.Add(card);
                }
                if (track == TrackChoice.All)
                {
                    var none = WidgetView.Build(null, TrackChoice.All, WidgetFamily.Small);
                    none.Margin = new Thickness(8);
                    none.VerticalAlignment = VerticalAlignment.Top;
                    row.Children.Add(none);
                }
                grid.Children.Add(row);
            }
            var window = new Window
            {
                Title = "RedLine widget sample", Content = grid, SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3A)),
            };
            app.MainWindow = window;
            window.Show();
            if (snapshot is not null) SaveAndExit(window, grid, snapshot);
            return true;
        }
        if (args.Contains("--tray-sample"))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)) };
            var cases = new[]
            {
                new TrayReadout("34", RL.BrandTone.Clear, null, 1, false, false, "", "41", RL.BrandTone.Clear),
                new TrayReadout("88", RL.BrandTone.Signal, null, 1, true, false, "", "100", RL.BrandTone.Signal),
                new TrayReadout("34", RL.BrandTone.Clear, null, 1, false, false, "", Suffix: "S", FitAs: "88"),
                new TrayReadout("41", RL.BrandTone.Clear, null, 1, false, false, "", Suffix: "W", FitAs: "88"),
                new TrayReadout("7", RL.BrandTone.Clear, null, 1, false, false, ""),
                new TrayReadout("72", RL.BrandTone.Amber, null, 1, true, false, ""),
                new TrayReadout("100", RL.BrandTone.Signal, null, 1, false, false, ""),
                new TrayReadout("45", RL.BrandTone.Steel, null, 1, false, false, ""),
                new TrayReadout(null, default, RL.BrandTone.Clear, 1, false, false, ""),
                new TrayReadout(null, default, RL.BrandTone.Amber, 0.55, true, false, ""),
                new TrayReadout(null, default, null, 1, false, true, ""),
            };
            // `--tray-icons <dir>` writes each case at each size as its own PNG, for docs and the site
            if (Arg("--tray-icons") is { } dir)
            {
                Directory.CreateDirectory(dir);
                foreach (var size in new[] { 16, 20, 24, 32 })
                    for (var i = 0; i < cases.Length; i++)
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(TrayIcon.RenderBitmap(cases[i], size, false)));
                        using var fs = File.Create(Path.Combine(dir, $"tray-{i}-{size}.png"));
                        enc.Save(fs);
                    }
                app.Shutdown(0);
                return true;
            }
            foreach (var size in new[] { 16, 20, 24, 32 })
                foreach (var c in cases)
                    row.Children.Add(new Image { Source = TrayIcon.RenderBitmap(c, size, false), Width = size * 3, Height = size * 3, Margin = new Thickness(4), SnapsToDevicePixels = true, Stretch = System.Windows.Media.Stretch.Uniform });
            RenderOptions.SetBitmapScalingMode(row, BitmapScalingMode.NearestNeighbor);
            var window = new Window { Title = "RedLine tray sample", Content = row, SizeToContent = SizeToContent.WidthAndHeight };
            app.MainWindow = window;
            window.Show();
            if (snapshot is not null) SaveAndExit(window, row, snapshot);
            return true;
        }
        return false;
    }

    static void SaveAndExit(Window window, FrameworkElement element, string path)
    {
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var w = element.ActualWidth;
            var h = element.ActualHeight;
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, w, h));
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
            Application.Current.Shutdown();
        });
    }

    public static Snapshot SampleSnapshot()
    {
        var entries = SampleData.Entries();
        var cfg = new Config();
        var now = DateTimeOffset.UtcNow;
        var today = Usage.Aggregate(entries, now.AddHours(-12), cfg);
        var week = Usage.Aggregate(entries, now.AddDays(-7), cfg);
        var limits = new List<LimitWindow>
        {
            SampleData.Window("Claude", "five_hour", 45, 2 * 3600),
            SampleData.Window("Claude", "seven_day", 5, 4 * 86400),
            SampleData.Window("Codex", "seven_day", 72, 3 * 86400),
        };
        var ollama = new Snapshot.OllamaSection(true, "0.32.13",
            new List<Snapshot.OllamaSection.RunningModel> { new("qwen3-coder:30b", 18_000_000_000, 0.82) }, 6, 53_000_000_000, 2);
        var services = new List<Snapshot.Service>
        {
            new("Claude", "minor", "Elevated errors"), new("Codex", "none", "All systems operational"),
            new("Ollama", "local", "checked directly"),
        };
        return new Snapshot(now, limits, today, week, ollama, services, now.AddMinutes(-2));
    }
}
#endif
