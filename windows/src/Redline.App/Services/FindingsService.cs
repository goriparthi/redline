// Runs the findings checks off the UI thread, rarely: once at launch and every few hours,
// because setup changes at the speed of someone editing a config file.
using Redline.Core;

namespace Redline.App.Services;

public sealed class FindingsService
{
    /// How long a report stands before another scan is worth the disk.
    public const double Interval = 6 * 3600;
    /// Long enough that a fortnight-old habit shows up, short enough that a dropped one leaves.
    public const int WindowDays = 14;

    private readonly TranscriptScanner _scanner;
    private readonly string? _home;
    private readonly SynchronizationContext? _context;
    private readonly object _lock = new();

    public FindingsReport? Report { get; private set; }
    public DateTimeOffset? LastRun { get; private set; }

    /// Raised on the context captured at construction (the UI thread) when a report lands.
    public event Action<FindingsReport>? Updated;

    /// True from the moment a scan is accepted until its report lands.
    public bool IsRunning { get; private set; }

    public FindingsService(string? projectsRoot = null, string? home = null, SynchronizationContext? context = null)
    {
        _scanner = new TranscriptScanner(projectsRoot);
        _home = home;
        _context = context ?? SynchronizationContext.Current;
    }

    public bool RefreshIfDue(Config config, DateTimeOffset? now = null)
    {
        if (!config.FindingsScans) return false;
        var t = now ?? DateTimeOffset.UtcNow;
        if (LastRun is { } last && (t - last).TotalSeconds < Interval) return false;
        return Refresh(config, t);
    }

    public bool Refresh(Config config, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (IsRunning) return false;
            IsRunning = true;
            LastRun = t;
        }
        Task.Run(() =>
        {
            FindingsReport report;
            try
            {
                // Nothing here touches the UI: the first scan can take seconds on a busy machine
                var sessions = _scanner.Scan(WindowDays, t);
                report = sessions.Count == 0
                    ? new FindingsReport(t, WindowDays, 0, new List<Finding>())
                    : Findings.Report(ClaudeSetup.FindingsInput(sessions, WindowDays, t, _home), config);
            }
            catch (Exception e)
            {
                Diag.Log.Error("findings.scan_failed", "findings scan failed", new() { ["error"] = e.Message });
                report = new FindingsReport(t, WindowDays, 0, new List<Finding>());
            }
            Deliver(report);
        });
        return true;
    }

    private void Deliver(FindingsReport report)
    {
        void Land()
        {
            lock (_lock) IsRunning = false;
            Report = report;
            Updated?.Invoke(report);
        }
        if (_context is { } c) c.Post(_ => Land(), null); else Land();
    }
}
