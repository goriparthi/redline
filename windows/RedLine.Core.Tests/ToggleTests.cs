using RedLine.Core;

namespace RedLine.Core.Tests;

public class ToggleTests
{
    private static ToggleStore Stub(EngineResult result,
                                    Action<IReadOnlyList<string>>? sawArguments = null) =>
        new(new Engine(runner: (_, arguments) =>
        {
            sawArguments?.Invoke(arguments);
            return result;
        }));

    private static EngineResult Said(string output, int exitCode = 0) =>
        new(Engine.Classify(exitCode), output, exitCode);

    [Fact]
    public void AStateIsReadWithoutChangingAnything()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(
            Said("""{"outcome":"status","key":"autostart","on":true,"name":"Run key"}"""),
            seen => arguments = seen);

        var outcome = store.Autostart();
        Assert.Equal(["autostart", "--json"], arguments);
        Assert.Equal(ToggleOutcomeKind.Status, outcome.Kind);
        Assert.True(outcome.On);
        Assert.True(outcome.Known);
        Assert.Equal("Run key", outcome.Name);
    }

    /// <summary>
    /// Autostart has to start the app, not the watcher: the app runs a watcher of its own and
    /// a second one would only lose the race for the lock.
    /// </summary>
    [Fact]
    public void TurningAutostartOnStartsTheAppWithNoArguments()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(Said("""{"outcome":"changed","key":"autostart","on":true}"""),
                         seen => arguments = seen);

        var outcome = store.SetAutostart(true, program: @"C:\Apps\RedLine.exe");
        Assert.Equal(
            ["autostart", "on", "--program", @"C:\Apps\RedLine.exe", "--args", "", "--json"],
            arguments);
        Assert.Equal(ToggleOutcomeKind.Changed, outcome.Kind);
        Assert.True(outcome.On);
    }

    [Fact]
    public void TurningItOffAsksForNothingElse()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(Said("""{"outcome":"changed","key":"autostart","on":false}"""),
                         seen => arguments = seen);

        Assert.False(store.SetAutostart(false).On);
        Assert.Equal(["autostart", "off", "--json"], arguments);
    }

    /// <summary>
    /// The feed reports 20 when it is off, which is an answer rather than a failure. Reading
    /// the exit code instead of the outcome would turn "off" into "the engine is broken".
    /// </summary>
    [Fact]
    public void TheFeedBeingOffIsAnAnswerAndNotAFailure()
    {
        var store = Stub(Said("""{"outcome":"status","key":"usageFeed","on":false}""",
                              exitCode: 20));
        var outcome = store.UsageFeed();
        Assert.Equal(ToggleOutcomeKind.Status, outcome.Kind);
        Assert.False(outcome.On);
        Assert.True(outcome.Known);
        Assert.True(outcome.Accepted);
    }

    [Fact]
    public void WiringTheFeedCarriesWhatToDoNext()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(Said("""
            {"outcome":"changed","key":"usageFeed","on":true,
             "detail":"Start a new Claude Code session for it to take effect."}
            """), seen => arguments = seen);

        var outcome = store.SetUsageFeed(true);
        Assert.Equal(["setup", "claude", "--json"], arguments);
        Assert.Contains("new Claude Code session", outcome.Detail);
        Assert.Equal("", outcome.Message);
    }

    [Fact]
    public void AlreadyWiredIsNotAChange()
    {
        var store = Stub(Said("""{"outcome":"unchanged","key":"usageFeed","on":true}"""));
        var outcome = store.SetUsageFeed(true);
        Assert.Equal(ToggleOutcomeKind.Unchanged, outcome.Kind);
        Assert.True(outcome.Accepted);
    }

    [Fact]
    public void AFailureCarriesTheReasonAndNoState()
    {
        var store = Stub(Said("""{"outcome":"failed","key":"usageFeed","message":"read only"}""",
                              exitCode: 1));
        var outcome = store.SetUsageFeed(true);
        Assert.Equal(ToggleOutcomeKind.Failed, outcome.Kind);
        Assert.False(outcome.Accepted);
        // On is meaningless here, and a switch that moved anyway would be lying
        Assert.False(outcome.Known);
        Assert.Equal("read only", outcome.Message);
    }

    [Fact]
    public void AnEngineThatWillNotRunIsSaidRatherThanGuessed()
    {
        var store = Stub(new EngineResult(EngineStatus.Unavailable, "redline was not found"));
        var outcome = store.Autostart();
        Assert.Equal(ToggleOutcomeKind.Unavailable, outcome.Kind);
        Assert.False(outcome.Known);
        Assert.Equal("autostart", outcome.Key);
        Assert.Contains("not found", outcome.Message);
    }

    [Fact]
    public void SomethingUnreadableIsNotMistakenForAnAnswer()
    {
        Assert.Null(ToggleJson.Parse("not json"));
        Assert.Null(ToggleJson.Parse("""{"outcome":"invented","on":true}"""));
        Assert.Equal(ToggleOutcomeKind.Unavailable, Stub(Said("<html>")).UsageFeed().Kind);
    }
}
