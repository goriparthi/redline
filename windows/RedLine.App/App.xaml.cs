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

        // Waits for the chain to complete rather than for a fixed delay: the transcript has
        // to be found by the watcher this app started, published, noticed, and drawn. Reports
        // either way after the deadline, so a stall says what it managed rather than hanging.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = true;
        timer.Tick += (t, _) =>
        {
            var main = window as MainWindow;
            var done = main?.HasRenderedData == true;
            if (!done && DateTimeOffset.UtcNow < deadline) return;

            t.Stop();
            // Built here rather than at startup: it runs the engine three times, and the
            // chain being timed above is the thing this test is actually about.
            main?.ProbeSettings();
            var summary = main?.SelfTestSummary ?? "no window";
            // Stopped explicitly: Environment.Exit runs no finalizers, and a watcher left
            // behind by a test run would hold the lock against the next one.
            main?.ShutDown();
            Report((done ? "ok: " : "timeout: ") + summary);
            Environment.Exit(0);
        };
        timer.Start();
    }
}
