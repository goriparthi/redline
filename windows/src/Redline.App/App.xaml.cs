// Application entry: DEBUG sample renderers first, then the tray host (Tray/TrayHost.cs),
// which owns the command line, the single-instance guard and the tray icon.
using System.Windows;

namespace Redline.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize(ThemeMode.Auto);

        // ---- SETTINGS SAMPLES (DEBUG, owned by the settings port): --settings-sample, --firstrun-sample ----
#if DEBUG
        if (Settings.SettingsSamples.TryRun(e.Args)) return;
#endif
        // ---- END SETTINGS SAMPLES ----

        // ---- DASHBOARD SAMPLES (DEBUG, owned by the dashboard port): --dashboard-sample ----
#if DEBUG
        if (Dashboard.DashboardSamples.TryRun(e.Args)) return;
#endif
        // ---- END DASHBOARD SAMPLES ----

        // ---- TRAY SAMPLES (DEBUG, owned by the tray port): --flyout-sample, --widget-sample, --tray-sample ----
#if DEBUG
        if (Tray.TraySamples.TryRun(e.Args)) return;
        if (e.Args.Contains("--gallery"))
        {
            var snapshot = Array.IndexOf(e.Args, "--snapshot");
            var gallery = new Gallery.GalleryWindow();
            MainWindow = gallery;
            gallery.Show();
            if (snapshot >= 0 && snapshot + 1 < e.Args.Length) gallery.SaveSnapshotAndClose(e.Args[snapshot + 1]);
            return;
        }
#endif
        // ---- END TRAY SAMPLES ----

        // The tray host: command line flags, the instance guard, then the tray icon. It switches
        // ShutdownMode to OnExplicitShutdown, since a tray app has no main window to close.
        Tray.TrayHost.Run(this, e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}
