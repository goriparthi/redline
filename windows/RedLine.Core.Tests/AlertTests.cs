using RedLine.Core;

namespace RedLine.Core.Tests;

public class AlertTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("redline-alerts").FullName;

    public void Dispose() => Directory.Delete(home, recursive: true);

    private EnginePaths Paths => new(new Dictionary<string, string> { ["REDLINE_HOME"] = home });

    private void Write(long seq, params string[] events)
    {
        var path = Paths.AlertFeedPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = $$"""
            {"seq":{{seq}},"at":"2026-08-20T12:00:00Z","events":[{{string.Join(",", events)}}]}
            """;
        File.WriteAllText(path, body);
    }

    private static string Event(string id, string kind = "threshold", bool sound = false) =>
        $$"""
        {"id":"{{id}}","kind":"{{kind}}","provider":"Claude","key":"five_hour",
         "title":"Claude · Session","body":"80% used","sound":{{(sound ? "true" : "false")}}}
        """;

    /// <summary>
    /// Whatever was on disk before anyone was listening is not news. Opening the app to a
    /// backlog of yesterday's limits would be the fastest way to have someone turn alerts off.
    /// </summary>
    [Fact]
    public void WhatWasAlreadyThereIsNotDelivered()
    {
        Write(7, Event("a"));
        var raised = new List<AlertEvent>();
        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        monitor.Raised += events => raised.AddRange(events);

        monitor.Start();
        monitor.Check();

        Assert.Empty(raised);
        Assert.Equal(7, monitor.Delivered);
    }

    [Fact]
    public void ANewerBatchIsDeliveredOnce()
    {
        Write(1, Event("a"));
        var raised = new List<AlertEvent>();
        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        monitor.Raised += events => raised.AddRange(events);
        monitor.Start();

        Write(2, Event("b", "limit_reached", sound: true));
        monitor.Check();
        monitor.Check();

        var only = Assert.Single(raised);
        Assert.Equal("b", only.Id);
        Assert.Equal("limit_reached", only.Kind);
        Assert.True(only.Sound);
        Assert.Equal(2, monitor.Delivered);
    }

    /// <summary>
    /// The sequence climbs across restarts of the watcher, so a batch that arrives with the
    /// same number is the same batch, not a new one that happens to look like it.
    /// </summary>
    [Fact]
    public void ARepeatedSequenceIsNotDeliveredAgain()
    {
        Write(1, Event("a"));
        var raised = new List<AlertEvent>();
        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        monitor.Raised += events => raised.AddRange(events);
        monitor.Start();

        Write(2, Event("b"));
        monitor.Check();
        Write(2, Event("c"));
        monitor.Check();

        Assert.Equal(["b"], raised.Select(e => e.Id));
    }

    [Fact]
    public void NoFeedAtAllIsQuietRatherThanAThrow()
    {
        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        var raised = 0;
        monitor.Raised += _ => raised++;
        monitor.Start();
        monitor.Check();
        Assert.Equal(0, raised);
        Assert.Equal(0, monitor.Delivered);
    }

    [Fact]
    public void SomethingUnreadableIsIgnoredRatherThanPosted()
    {
        var path = Paths.AlertFeedPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<half a write>");

        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        var raised = 0;
        monitor.Raised += _ => raised++;
        monitor.Start();
        monitor.Check();
        Assert.Equal(0, raised);
        Assert.Null(AlertJson.Parse("<half a write>"));
    }

    /// <summary>An empty batch is not written by the engine, and would be nothing to post
    /// even if it were.</summary>
    [Fact]
    public void AnEmptyBatchRaisesNothing()
    {
        Write(1);
        var raised = 0;
        using var monitor = new AlertMonitor(Paths, pollInterval: TimeSpan.FromHours(1));
        monitor.Raised += _ => raised++;
        monitor.Start();
        Write(2);
        monitor.Check();
        Assert.Equal(0, raised);
        Assert.Equal(2, monitor.Delivered);
    }
}
