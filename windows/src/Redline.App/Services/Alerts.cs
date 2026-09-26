// Delivery for the events Alerting decides on. The decision is in the core and tested; this
// is the part that hands them to Windows, through whatever INotifier the tray supplies.
using Redline.Core;

namespace Redline.App.Services;

/// One notification. Sound is reserved for a limit actually reached.
public interface INotifier
{
    void Show(string title, string body, bool sound);
}

/// The tray's NotifyIcon as the notifier. Windows 10 and later turn balloons into toasts.
public sealed class NotifyIconNotifier : INotifier
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    public NotifyIconNotifier(System.Windows.Forms.NotifyIcon icon) => _icon = icon;

    // The shell truncates past these lengths anyway; cutting here keeps the end readable
    public const int MaxTitle = 63;
    public const int MaxBody = 255;

    public void Show(string title, string body, bool sound)
    {
        var t = title.Length > MaxTitle ? title[..(MaxTitle - 1)] + "…" : title;
        var b = body.Length > MaxBody ? body[..(MaxBody - 1)] + "…" : body;
        // Only a reached limit may make noise; the rest are information arriving while you work
        var icon = sound ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(10_000, t, b, icon);
        if (sound) System.Media.SystemSounds.Exclamation.Play();
    }
}

public sealed class AlertCenter
{
    private readonly object _lock = new();
    private readonly AlertState _state;
    private readonly CadenceState _cadenceState;
    private readonly string? _alertPath;
    private readonly string? _cadencePath;

    /// Null until the tray exists; events are still recorded, so nothing fires twice later.
    public INotifier? Notifier { get; set; }

    public AlertCenter(INotifier? notifier = null, string? alertStorePath = null, string? cadenceStorePath = null)
    {
        Notifier = notifier;
        _alertPath = alertStorePath;
        _cadencePath = cadenceStorePath;
        _state = AlertStore.Load(alertStorePath);
        _cadenceState = CadenceStore.Load(cadenceStorePath);
    }

    /// Posts whatever this poll's readings have newly earned; a stale reading updates the
    /// record but never fires. Returns the events, so the caller can log or test.
    public List<AlertEvent> Evaluate(IEnumerable<LimitWindow> windows, IEnumerable<Pace> paces, Config config,
                                     Func<LimitWindow, bool>? isStale = null, DateTimeOffset? now = null)
    {
        List<AlertEvent> events;
        lock (_lock)
        {
            events = Alerting.Evaluate(windows, config, _state, paces, now, isStale ?? (_ => false));
            AlertStore.Save(_state, _alertPath);
        }
        if (!config.Alerts || events.Count == 0) return events;
        foreach (var e in events) Post(e.Title, e.Body, e.Kind is AlertKind.LimitReached);
        return events;
    }

    /// The shape of the day. Same delivery path; a cue never makes a sound, because none of
    /// them should pull someone out of what they are doing.
    public List<CadenceCue> EvaluateCadence(IReadOnlyList<Entry> entries, Config config, DateTimeOffset? now = null)
    {
        List<CadenceCue> cues;
        lock (_lock)
        {
            cues = CadenceRules.Evaluate(entries, config, _cadenceState, now);
            CadenceStore.Save(_cadenceState, _cadencePath);
        }
        if (!config.MindfulCues || cues.Count == 0) return cues;
        foreach (var c in cues) Post(c.Title, c.Body, sound: false);
        return cues;
    }

    private void Post(string title, string body, bool sound)
    {
        if (Notifier is not { } n) return;
        try { n.Show(title, body, sound); }
        catch (Exception e)
        {
            Diag.Log.Error("alerts.deliver_failed", "could not show a notification", new() { ["error"] = e.Message });
        }
    }
}
