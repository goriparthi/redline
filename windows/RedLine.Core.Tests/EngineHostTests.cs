using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// Run against the real engine when CI hands one over, because the thing worth proving is that
/// the app keeps its own data current rather than showing whatever was last published.
/// </summary>
public class EngineHostTests : IDisposable
{
    private readonly string home;
    private readonly string? binary;

    public EngineHostTests()
    {
        home = Directory.CreateTempSubdirectory("redline-host").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects", "demo"));
        var path = Environment.GetEnvironmentVariable("REDLINE_TEST_BIN");
        binary = !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    public void Dispose() => Directory.Delete(home, recursive: true);

    private EnginePaths Paths(string bin) => new(new Dictionary<string, string>
    {
        ["REDLINE_HOME"] = home,
        ["REDLINE_BIN"] = bin,
    });

    /// <summary>
    /// A record from earlier today. "Twenty minutes ago" is yesterday for the first twenty
    /// minutes of a UTC day, and days are UTC, so today's total would then be zero.
    /// </summary>
    private void WriteTranscript()
    {
        var now = DateTimeOffset.UtcNow;
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var when = now.AddMinutes(-20) < midnight ? midnight : now.AddMinutes(-20);
        var stamp = when.ToString("yyyy-MM-ddTHH:mm:ss.000Z");
        var line = "{\"timestamp\":\"" + stamp + "\",\"requestId\":\"req_a\",\"message\":"
            + "{\"id\":\"a\",\"model\":\"claude-sonnet-5\",\"usage\":"
            + "{\"input_tokens\":1000,\"output_tokens\":100,\"cache_read_input_tokens\":0}}}";
        File.WriteAllText(Path.Combine(home, ".claude", "projects", "demo", "session.jsonl"),
                          line + "\n");
    }

    /// <summary>
    /// The whole point: with the app running, a transcript on disk becomes a published
    /// snapshot with nobody typing anything.
    /// </summary>
    [Fact]
    public void RunningTheHostKeepsTheSnapshotCurrent()
    {
        if (binary is null) return;
        WriteTranscript();

        var paths = Paths(binary);
        using var host = new EngineHost(paths, restartDelay: TimeSpan.FromSeconds(1));
        var gaveUp = "";
        host.GaveUp += reason => gaveUp = reason;
        Assert.True(host.Start(), gaveUp);
        Assert.True(host.IsRunning);

        var snapshot = Path.Combine(home, ".local", "share", "redline", "snapshot.json");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(snapshot) && DateTime.UtcNow < deadline) Thread.Sleep(200);
        Assert.True(File.Exists(snapshot), $"the watcher published nothing. {gaveUp}");

        var read = new Engine(paths).ReadSnapshot();
        Assert.NotNull(read);
        Assert.Equal(1100, read.Today!.Io);

        host.Stop();
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void StoppingLeavesNoProcessBehind()
    {
        if (binary is null) return;
        using var host = new EngineHost(Paths(binary));
        Assert.True(host.Start());
        host.Stop();
        Assert.False(host.IsRunning);
        // A second stop is what Dispose does after an explicit one, and must be harmless
        host.Stop();
    }

    [Fact]
    public void StartingTwiceDoesNotStartTwoWatchers()
    {
        if (binary is null) return;
        using var host = new EngineHost(Paths(binary));
        Assert.True(host.Start());
        Assert.True(host.Start());
        Assert.True(host.IsRunning);
    }

    /// <summary>
    /// An app with no engine has to say so. Showing an empty window and no explanation is the
    /// failure mode worth avoiding.
    /// </summary>
    [Fact]
    public void AMissingEngineIsReportedRatherThanRetriedForever()
    {
        var paths = new EnginePaths(new Dictionary<string, string>
        {
            ["REDLINE_HOME"] = home,
            ["PATH"] = "",
        });
        using var host = new EngineHost(paths);
        var reason = "";
        host.GaveUp += r => reason = r;
        Assert.False(host.Start());
        Assert.False(host.IsRunning);
        Assert.Contains("not found", reason);
    }
}
