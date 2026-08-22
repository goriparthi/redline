using RedLine.Core;

namespace RedLine.Core.Tests;

public class TrendsStoreTests
{
    private static TrendStore Stub(EngineResult result,
                                   Action<IReadOnlyList<string>>? sawArguments = null) =>
        new(new Engine(runner: (_, arguments) =>
        {
            sawArguments?.Invoke(arguments);
            return result;
        }));

    [Fact]
    public void TheWindowIsAskedForInDays()
    {
        IReadOnlyList<string> arguments = [];
        var store = Stub(new EngineResult(EngineStatus.Ok, """{"days":30,"series":[]}""", 0),
                         seen => arguments = seen);

        var report = store.Read(30);
        Assert.Equal(["trends", "--days", "30", "--json"], arguments);
        Assert.Equal(30, report.Days);
        Assert.True(report.Available);
    }

    /// <summary>
    /// No history yet exits 20 with an empty series, which is an answer. Reading the exit
    /// code as failure would turn a new install into a broken app.
    /// </summary>
    [Fact]
    public void AnEmptyHistoryIsAnAnswerAndNotAFailure()
    {
        var store = Stub(new EngineResult(EngineStatus.Indeterminate,
                                          """{"days":14,"series":[],"models":[]}""", 20));
        var report = store.Read();
        Assert.True(report.Available);
        Assert.True(report.IsEmpty);
        Assert.Contains("Nothing recorded", report.Summary);
    }

    [Fact]
    public void AnEngineThatWillNotRunIsSaidRatherThanDrawnAsQuiet()
    {
        var store = Stub(new EngineResult(EngineStatus.Unavailable, "redline was not found"));
        var report = store.Read();
        Assert.False(report.Available);
        Assert.Contains("not found", report.Problem);
    }

    [Fact]
    public void UnreadableOutputIsAProblemRatherThanAnEmptyChart()
    {
        var report = Stub(new EngineResult(EngineStatus.Ok, "<html>", 0)).Read();
        Assert.False(report.Available);
        Assert.Contains("readable", report.Problem);
    }
}
