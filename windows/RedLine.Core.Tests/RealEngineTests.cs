using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// Runs the actual engine binary rather than a stub. Only when CI hands us one in
/// REDLINE_TEST_BIN: on a developer machine there may be no build to point at, and a test
/// that invented a path would be testing the invention.
/// </summary>
public class RealEngineTests
{
    private static string? Binary
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("REDLINE_TEST_BIN");
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
    }

    private static (Engine Engine, string Home) Fresh(string binary)
    {
        var home = Directory.CreateTempSubdirectory("redline-real").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects", "demo"));
        var paths = new EnginePaths(new Dictionary<string, string>
        {
            ["REDLINE_HOME"] = home,
            ["REDLINE_BIN"] = binary,
        });
        return (new Engine(paths), home);
    }

    [Fact]
    public void TheEngineReportsItsVersion()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var result = engine.Run("--version");
            Assert.True(result.Ran, result.Output);
            Assert.Contains("redline", result.Output);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// An empty machine must say so rather than report a zero. Code 30 is that answer, and
    /// the shell needs to be able to tell it apart from a successful reading of nothing.
    /// </summary>
    [Fact]
    public void AnEmptyMachineReportsNoDataRatherThanZero()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            Assert.Equal(EngineStatus.NoData, engine.Run("status", "--json").Status);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// The whole point of the shell: a transcript on disk turns into numbers on screen,
    /// through the engine, with nothing here parsing a transcript itself.
    /// </summary>
    [Fact]
    public void ATranscriptBecomesHistoryThroughTheEngine()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var stamp = DateTimeOffset.UtcNow.AddMinutes(-30)
                .ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var line = "{\"timestamp\":\"" + stamp + "\",\"requestId\":\"req_a\",\"message\":"
                + "{\"id\":\"a\",\"model\":\"claude-sonnet-5\",\"usage\":"
                + "{\"input_tokens\":1000,\"output_tokens\":100,\"cache_read_input_tokens\":0}}}";
            File.WriteAllText(
                Path.Combine(home, ".claude", "projects", "demo", "session.jsonl"),
                line + "\n");

            var ingested = engine.Run("ingest", "--json");
            Assert.Equal(EngineStatus.Ok, ingested.Status);
            Assert.Contains("\"added\" : 1", ingested.Output);

            // The second pass must add nothing, which is the property incremental reading exists for
            Assert.Contains("\"added\" : 0", engine.Run("ingest", "--json").Output);

            var history = engine.Run("history");
            Assert.Equal(EngineStatus.Ok, history.Status);
            Assert.Contains("1.1K", history.Output);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// Settings through the real engine: what it offers, a change it takes, and the same
    /// change again. The second one is "already that" rather than a failure.
    /// </summary>
    [Fact]
    public void SettingsAreReadAndChangedThroughTheEngine()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var store = new SettingsStore(engine);

            var catalog = store.Read();
            Assert.True(catalog.Available, catalog.Problem);
            Assert.NotEmpty(catalog.Settings);
            var yellow = catalog.Find("limitYellowPct");
            Assert.NotNull(yellow);
            Assert.Equal(SettingKind.Number, yellow.Kind);

            var changed = store.Write("limitYellowPct", 70d);
            Assert.Equal(SettingsOutcomeKind.Changed, changed.Kind);
            Assert.Equal("70", changed.Value);

            Assert.Equal("70", store.Read().Find("limitYellowPct")?.Value);
            Assert.Equal(SettingsOutcomeKind.Unchanged,
                         store.Write("limitYellowPct", 70d).Kind);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// The engine is the only validator. A value it would not load comes back refused, with
    /// its own words for what it wanted, and nothing is written.
    /// </summary>
    [Fact]
    public void AValueTheEngineWouldNotLoadIsRefusedRatherThanStored()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var store = new SettingsStore(engine);
            var before = store.Read().Find("limitYellowPct")?.Value;

            var refused = store.Write("limitYellowPct", 900d);
            Assert.Equal(SettingsOutcomeKind.Rejected, refused.Kind);
            Assert.False(refused.Accepted);
            Assert.NotEqual("", refused.Expected);
            Assert.Equal(before, store.Read().Find("limitYellowPct")?.Value);

            Assert.Equal(SettingsOutcomeKind.UnknownKey, store.Write("nosuch", "1").Kind);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// The usage feed through the real engine, in a scratch home so nothing touches the
    /// machine's own Claude settings. Off, on, still on, and off again.
    /// </summary>
    [Fact]
    public void TheUsageFeedIsWiredAndUnwiredThroughTheEngine()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var toggles = new ToggleStore(engine);

            var before = toggles.UsageFeed();
            Assert.Equal(ToggleOutcomeKind.Status, before.Kind);
            Assert.False(before.On);

            var wired = toggles.SetUsageFeed(true);
            Assert.Equal(ToggleOutcomeKind.Changed, wired.Kind);
            Assert.True(wired.On);
            Assert.True(toggles.UsageFeed().On);
            Assert.Equal(ToggleOutcomeKind.Unchanged, toggles.SetUsageFeed(true).Kind);

            Assert.Equal(ToggleOutcomeKind.Changed, toggles.SetUsageFeed(false).Kind);
            Assert.False(toggles.UsageFeed().On);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>Reading only: turning autostart on would write a real login entry.</summary>
    [Fact]
    public void AutostartReportsItselfThroughTheEngine()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var outcome = new ToggleStore(engine).Autostart();
            Assert.Equal(ToggleOutcomeKind.Status, outcome.Kind);
            Assert.True(outcome.Known);
            Assert.NotEqual("", outcome.Name);
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
