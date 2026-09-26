// DEBUG only: `RedLine.exe --dashboard-sample [state] [--theme dark|light] [--width N] [--snapshot out.png]`
// opens the dashboard over SampleData, or renders it to a PNG and exits.
#if DEBUG
using System.Windows;

namespace Redline.App.Dashboard;

public static class DashboardSamples
{
    public static bool TryRun(string[] args)
    {
        var at = Array.IndexOf(args, "--dashboard-sample");
        if (at < 0) return false;
        string? Arg(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var state = at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : "normal";
        var data = SampleData.Named(state);
        if (Arg("--theme") is { } theme) data.Theme = theme;
        var model = SampleData.Model(data);
        var window = new DashboardWindow(model, onReload: _ => { }, onOpenSettings: () => { });
        if (double.TryParse(Arg("--width"), out var width)) window.Width = width;
        var app = Application.Current;
        app.MainWindow = window;
        window.Show();
        if (Arg("--snapshot") is { } path)
        {
            window.SaveSnapshotAndClose(path);
            window.Closed += (_, _) => app.Shutdown();
        }
        return true;
    }
}
#endif
