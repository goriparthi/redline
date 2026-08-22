using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using RedLine.Core;

namespace RedLine.App;

/// <summary>
/// Delivery for the events the engine decided on. Nothing here judges whether a reading is
/// worth interrupting someone for, what to call it, or whether it should make a noise: all
/// three arrive with the event, so a toast and a macOS notification cannot disagree.
/// </summary>
internal sealed class Toasts : IDisposable
{
    private bool registered;
    private bool disposed;

    /// <summary>What the self test reports. Registration is the part that can fail.</summary>
    public string State { get; private set; } = "off";

    /// <summary>How many have been posted this run.</summary>
    public int Posted { get; private set; }

    /// <summary>
    /// Registers with Windows, which an unpackaged app has to do explicitly. A failure is
    /// recorded rather than thrown: an app that cannot toast is still an app that works.
    /// </summary>
    public void Start()
    {
        try
        {
            AppNotificationManager.Default.Register();
            registered = true;
            State = "registered";
        }
        catch (Exception error)
        {
            State = "failed";
            Note = error.Message;
        }
    }

    /// <summary>Why toasts are off, when they are off.</summary>
    public string Note { get; private set; } = "";

    public void Post(IReadOnlyList<AlertEvent> events)
    {
        if (!registered) return;
        foreach (var alert in events) Post(alert);
    }

    public void Post(AlertEvent alert)
    {
        if (!registered || disposed) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(alert.Title)
                .AddText(alert.Body)
                // Tagged with the engine's own id, so the same event landing twice replaces
                // the notification rather than stacking a second copy of it
                .SetTag(alert.Id)
                .SetGroup("redline");

            // A limit reached is the only one that makes a noise, and the engine says which
            if (!alert.Sound) builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            Posted++;
        }
        catch (Exception error)
        {
            // A toast that will not post must not take the app down with it
            Note = error.Message;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { /* going away anyway */ }
    }
}
