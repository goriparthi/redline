// A second launch cannot add a second tray icon, so it asks the running copy to show its
// dashboard (the distributed notification in main.swift) and exits. A named event carries it.
using System.Threading;
using Redline.Core;

namespace Redline.App.Tray;

public sealed class InstanceSignal : IDisposable
{
    readonly EventWaitHandle handle;
    readonly Thread listener;
    volatile bool stopping;

    /// <summary>Keyed on the instance lock, so each REDLINE_HOME profile signals its own copy.</summary>
    public static string EventName => SingleInstance.MutexName(SingleInstance.LockUrl) + "-show-dashboard";

    InstanceSignal(EventWaitHandle handle, Action onSignal)
    {
        this.handle = handle;
        listener = new Thread(() =>
        {
            while (!stopping)
            {
                try { handle.WaitOne(); } catch { return; }
                if (!stopping) onSignal();
            }
        }) { IsBackground = true, Name = "instance-signal" };
        listener.Start();
    }

    /// <summary>Held by the instance that owns the tray; `onSignal` runs on a background thread.</summary>
    public static InstanceSignal? Listen(Action onSignal)
    {
        try { return new InstanceSignal(new EventWaitHandle(false, EventResetMode.AutoReset, EventName), onSignal); }
        catch (Exception e)
        {
            Diag.Log.Error("instance.signal_failed", "could not create the show-dashboard event",
                new() { ["error"] = e.Message });
            return null;
        }
    }

    /// <summary>From the duplicate launch. False when no running copy is listening.</summary>
    public static bool Post()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out var h)) return false;
            using (h) h.Set();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        stopping = true;
        try { handle.Set(); } catch { }
        handle.Dispose();
    }
}
