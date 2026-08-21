using RedLine.Core;

namespace RedLine.Core.Tests;

public class SnapshotMonitorTests : IDisposable
{
    private readonly string home;
    private readonly string data;
    private readonly EnginePaths paths;

    public SnapshotMonitorTests()
    {
        home = Directory.CreateTempSubdirectory("redline-monitor").FullName;
        data = Path.Combine(home, ".local", "share", "redline");
        Directory.CreateDirectory(data);
        paths = new EnginePaths(new Dictionary<string, string> { ["REDLINE_HOME"] = home });
    }

    public void Dispose() => Directory.Delete(home, recursive: true);

    private void Publish(double utilization)
    {
        var json = "{\"updatedAt\":\"2026-08-21T12:00:00Z\",\"limits\":[{\"provider\":\"Claude\","
            + "\"key\":\"five_hour\",\"utilization\":" + utilization + "}]}";
        // Written beside and moved, the way the engine replaces it
        var temp = Path.Combine(data, "snapshot.json.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, Path.Combine(data, "snapshot.json"), overwrite: true);
    }

    private SnapshotMonitor Monitor() =>
        new(paths: paths, pollInterval: TimeSpan.FromMilliseconds(200),
            debounce: TimeSpan.FromMilliseconds(50));

    [Fact]
    public void TheFirstReadIsSynchronousSoAWindowHasSomethingToDraw()
    {
        Publish(42);
        using var monitor = Monitor();
        monitor.Start();
        Assert.NotNull(monitor.Current);
        Assert.Equal(42, monitor.Current.Worst!.Utilization);
    }

    [Fact]
    public void NoSnapshotYetIsNullRatherThanAThrow()
    {
        using var monitor = Monitor();
        monitor.Start();
        Assert.Null(monitor.Current);
    }

    [Fact]
    public void AReplacedSnapshotIsPickedUp()
    {
        Publish(10);
        using var monitor = Monitor();
        var seen = new List<double>();
        var landed = new ManualResetEventSlim(false);
        monitor.Updated += snapshot =>
        {
            if (snapshot?.Worst is { } w) { seen.Add(w.Utilization); }
            if (seen.Contains(77)) landed.Set();
        };
        monitor.Start();

        Publish(77);
        Assert.True(landed.Wait(TimeSpan.FromSeconds(10)), "the new reading never arrived");
        Assert.Equal(77, monitor.Current!.Worst!.Utilization);
    }

    /// <summary>
    /// A UI bound to this must not redraw for a file that was rewritten with the same content,
    /// which is what the engine does on every sweep when nothing has moved.
    /// </summary>
    [Fact]
    public void AnUnchangedReadingRaisesNothing()
    {
        Publish(42);
        using var monitor = Monitor();
        var raised = 0;
        monitor.Updated += _ => Interlocked.Increment(ref raised);
        monitor.Start();
        var afterStart = Volatile.Read(ref raised);

        for (var i = 0; i < 3; i++)
        {
            Publish(42);
            Thread.Sleep(150);
        }
        monitor.Reread();
        Assert.Equal(afterStart, Volatile.Read(ref raised));
    }

    /// <summary>
    /// The directory may not exist until something publishes into it, which is the state of a
    /// machine where the watcher has never run.
    /// </summary>
    [Fact]
    public void AMissingDirectoryIsNotAnError()
    {
        var empty = Directory.CreateTempSubdirectory("redline-none").FullName;
        try
        {
            var monitor = new SnapshotMonitor(
                paths: new EnginePaths(new Dictionary<string, string> { ["REDLINE_HOME"] = empty }),
                pollInterval: TimeSpan.FromMilliseconds(200));
            monitor.Start();
            Assert.Null(monitor.Current);
            monitor.Dispose();
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var monitor = Monitor();
        monitor.Start();
        monitor.Dispose();
        monitor.Dispose();
    }

    /// <summary>The poll is the floor: it has to find a change the watcher missed.</summary>
    [Fact]
    public void ThePollFindsWhatAWatcherMayHaveMissed()
    {
        using var monitor = new SnapshotMonitor(
            paths: paths, pollInterval: TimeSpan.FromMilliseconds(150),
            debounce: TimeSpan.FromMilliseconds(20));
        var landed = new ManualResetEventSlim(false);
        monitor.Updated += snapshot => { if (snapshot is not null) landed.Set(); };
        monitor.Start();
        Assert.Null(monitor.Current);

        // Published after Start, into a directory that existed, but the assertion only relies
        // on the poll eventually noticing
        Publish(33);
        Assert.True(landed.Wait(TimeSpan.FromSeconds(10)));
    }
}
