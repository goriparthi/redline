// DEBUG only: the dropdown built from invented state, for `--flyout-sample`. The controller is
// never started, so nothing here polls, watches, or writes a snapshot.
#if DEBUG
using Redline.App.Dashboard;
using Redline.Core;

namespace Redline.App.Tray;

public sealed partial class AppController
{
    internal static List<MenuEntry> SampleMenu()
    {
        var c = new AppController();
        var now = DateTimeOffset.UtcNow;
        var entries = SampleData.Entries();
        c.config = new Config { StatusChecks = true };
        c.today = Usage.Aggregate(entries, now.AddHours(-12), c.config);
        c.block5h = Usage.Aggregate(entries, now.AddHours(-5), c.config);
        c.week = Usage.Aggregate(entries, now.AddDays(-7), c.config);
        c.claudeLimits = new() { SampleData.Window("Claude", "five_hour", 45, 2 * 3600), SampleData.Window("Claude", "seven_day", 5, 4 * 86400) };
        c.codexLimits = new() { SampleData.Window("Codex", "seven_day", 15, 3 * 86400) };
        c.claudeLimitsAt = now.AddMinutes(-1);
        c.claudeLimitsSource = ClaudeLimitsSource.Feed;
        c.claudeService = new ServiceStatus.Report("minor", "Elevated errors");
        c.codexService = new ServiceStatus.Report("none", "All systems operational");
        c.availability = new ProviderAvailability(new[] { "Claude", "Codex", "Ollama" });
        c.lastRefresh = now;
        c.updateStatus = "Up to date (0.6.0)";
        c.paces = PaceEstimator.Paces(c.InformativeLimits);
        c.fleet = new FleetSnapshot(new[]
        {
            new FleetSession(4242, @"C:\src\redline") { Status = "waiting", WaitingFor = "input needed", StatusUpdatedAt = now.AddMinutes(-14), Version = "2.1.0", Entrypoint = "cli" },
            new FleetSession(4343, @"C:\src\site") { Name = "docs pass", Status = "busy", StatusUpdatedAt = now.AddMinutes(-3) },
        });
        return c.BuildMenu();
    }
}
#endif
