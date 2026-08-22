using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// The store against a stubbed engine, so every answer the engine can give is covered without
/// needing a build of it. RealEngineTests proves the same paths against the actual binary.
/// </summary>
public class SettingsStoreTests
{
    private static SettingsStore Stub(EngineResult result,
                                      Action<IReadOnlyList<string>>? sawArguments = null)
    {
        return new SettingsStore(new Engine(runner: (_, arguments) =>
        {
            sawArguments?.Invoke(arguments);
            return result;
        }));
    }

    private static EngineResult Said(string output, int exitCode = 0) =>
        new(Engine.Classify(exitCode), output, exitCode);

    [Fact]
    public void TheCatalogComesBackAsTheEngineDescribedIt()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(
            Said("""{"settings":[{"key":"alerts","kind":"bool","summary":"s","value":"true"}]}"""),
            seen => arguments = seen);

        var catalog = store.Read();
        Assert.True(catalog.Available);
        Assert.Equal(["config", "--json"], arguments);
        Assert.Equal("alerts", Assert.Single(catalog.Settings).Key);
        Assert.NotNull(catalog.Find("ALERTS"));
        Assert.Null(catalog.Find("nosuch"));
    }

    /// <summary>
    /// No engine is not the same as no settings. A page that showed nothing would look like an
    /// app that has none, so the reason travels with the empty list.
    /// </summary>
    [Fact]
    public void AnEngineThatWillNotRunIsSaidRatherThanShownAsEmpty()
    {
        var store = Stub(new EngineResult(EngineStatus.Unavailable, "redline was not found"));
        var catalog = store.Read();
        Assert.False(catalog.Available);
        Assert.Empty(catalog.Settings);
        Assert.Contains("not found", catalog.Problem);
    }

    [Fact]
    public void UnreadableOutputIsAProblemRatherThanNoSettings()
    {
        var catalog = Stub(Said("<html>")).Read();
        Assert.False(catalog.Available);
        Assert.Contains("readable", catalog.Problem);
    }

    [Fact]
    public void AChangeIsSentAsTheEngineExpectsIt()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(
            Said("""{"outcome":"changed","key":"limitYellowPct","from":"60","to":"70"}"""),
            seen => arguments = seen);

        var outcome = store.Write("limitYellowPct", 70d);
        Assert.Equal(["config", "limitYellowPct", "70", "--json"], arguments);
        Assert.Equal(SettingsOutcomeKind.Changed, outcome.Kind);
        Assert.Equal("70", outcome.Value);
    }

    [Fact]
    public void ABoolAndAListAreSpelledTheWayTheEngineReadsThem()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(Said("""{"outcome":"unchanged","key":"k","value":"v"}"""),
                         seen => arguments = seen);

        store.Write("alerts", false);
        Assert.Equal(["config", "alerts", "false", "--json"], arguments);

        store.Write("providers", ["Claude", "Ollama"]);
        Assert.Equal(["config", "providers", "Claude,Ollama", "--json"], arguments);
    }

    /// <summary>
    /// A refusal exits 2, which is not one of the status codes. Treating that as "the engine
    /// could not be run" would turn a plain "that is not a legal value" into a broken app.
    /// </summary>
    [Fact]
    public void ARefusalIsAnAnswerAndNotAFailureToRun()
    {
        var store = Stub(Said("""
            {"outcome":"rejected","key":"limitYellowPct","expected":"a number from 1 to 100"}
            """, exitCode: 2));

        var outcome = store.Write("limitYellowPct", "900");
        Assert.Equal(SettingsOutcomeKind.Rejected, outcome.Kind);
        Assert.False(outcome.Accepted);
        Assert.Equal("limitYellowPct needs a number from 1 to 100", outcome.Message);
    }

    [Fact]
    public void AnEngineThatSaysNothingUsableStillExplainsItself()
    {
        var store = Stub(new EngineResult(EngineStatus.Unavailable, "", 2, "unknown option"));
        var outcome = store.Write("alerts", true);
        Assert.Equal(SettingsOutcomeKind.Unavailable, outcome.Kind);
        Assert.Equal("unknown option", outcome.Message);
    }

    [Fact]
    public void AnEngineThatNeverRanIsReportedAsSuch()
    {
        var store = Stub(new EngineResult(EngineStatus.Unavailable, "redline was not found"));
        var outcome = store.Write("alerts", true);
        Assert.Equal(SettingsOutcomeKind.Unavailable, outcome.Kind);
        Assert.False(outcome.Accepted);
        Assert.Contains("not found", outcome.Message);
    }
}
