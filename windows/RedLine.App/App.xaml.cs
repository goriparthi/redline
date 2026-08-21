using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RedLine.App;

public partial class App : Application
{
    /// <summary>
    /// Start, build everything, write what happened, and leave. A GUI app cannot be exercised
    /// by a test runner, but it can be asked to prove it starts, and "it compiles" is a much
    /// weaker claim than "it ran once".
    /// </summary>
    public const string SelfTestFlag = "--selftest";

    private Window? window;

    public App()
    {
        InitializeComponent();
        // A crash during startup is the failure this exists to catch, so it has to be
        // recorded rather than silently ending the process
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report("failed: " + e.Exception);
            Environment.Exit(2);
        };
    }

    private static bool IsSelfTest =>
        Environment.GetCommandLineArgs().Any(
            a => string.Equals(a, SelfTestFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where the self test says what happened. A WinExe has no console to say it on.</summary>
    private static string ReportPath =>
        Environment.GetEnvironmentVariable("REDLINE_SELFTEST_LOG")
        ?? Path.Combine(Path.GetTempPath(), "redline-selftest.log");

    private static void Report(string text)
    {
        try { File.WriteAllText(ReportPath, text); } catch { /* nothing left to try */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Created but not activated: this belongs in the tray, and a window that appears at
        // login is not what a status app is for.
        window = new MainWindow();

        if (!IsSelfTest) return;

        // Long enough for the tray icon and the first render to have happened, short enough
        // that a hung run is obviously hung
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            var summary = window is MainWindow main ? main.SelfTestSummary : "no window";
            // Stopped explicitly: Environment.Exit runs no finalizers, and a watcher left
            // behind by a test run would hold the lock against the next one.
            (window as MainWindow)?.ShutDown();
            Report("ok: " + summary);
            Environment.Exit(0);
        };
        timer.Start();
    }
}
