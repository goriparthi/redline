// A serial background queue, the DispatchQueue(label:qos:.utility) the Swift app scans on.
// One thread per queue, so work never overlaps and the Warehouse sees one caller at a time.
using System.Collections.Concurrent;
using System.Threading;
using Redline.Core;

namespace Redline.App.Tray;

public sealed class SerialQueue : IDisposable
{
    readonly BlockingCollection<Action> work = new();
    readonly Thread thread;

    public SerialQueue(string name)
    {
        thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public void Post(Action action)
    {
        try { work.Add(action); } catch (InvalidOperationException) { }
    }

    void Run()
    {
        foreach (var action in work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception e)
            {
                // A throw here would kill the thread and every scan after it
                Diag.Log.Error("queue.work_failed", "background work failed",
                    new() { ["queue"] = thread.Name ?? "", ["error"] = e.Message });
            }
        }
    }

    public void Dispose() => work.CompleteAdding();
}
