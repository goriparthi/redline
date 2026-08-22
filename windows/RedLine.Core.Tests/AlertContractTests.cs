using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// What a toast is built from, against the same fixture the Swift suite asserts against. The
/// shell posts these words verbatim, so a missing one is a notification with nothing in it.
/// </summary>
public class AlertContractTests
{
    private static AlertBatch Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "alert-feed.json");
        var parsed = AlertJson.Parse(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void TheFeedTheWatcherWritesParses()
    {
        var batch = Load();
        Assert.Equal(1, batch.Seq);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), batch.At);
        Assert.Equal(2, batch.Events.Count);
    }

    [Fact]
    public void EveryEventCarriesTheWordsToPost()
    {
        foreach (var alert in Load().Events)
        {
            Assert.NotEqual("", alert.Id);
            Assert.NotEqual("", alert.Kind);
            Assert.NotEqual("", alert.Title);
            Assert.NotEqual("", alert.Body);
            Assert.NotEqual("", alert.Provider);
        }
    }

    /// <summary>
    /// A limit reached is the only one that makes a noise, and the engine says which, so a
    /// toast cannot decide to be louder than a notification on macOS.
    /// </summary>
    [Fact]
    public void OnlyTheReachedLimitMakesASound()
    {
        var batch = Load();
        var loud = Assert.Single(batch.Events, e => e.Sound);
        Assert.Equal("limit_reached", loud.Kind);
        Assert.Equal("Codex", loud.Provider);

        var threshold = Assert.Single(batch.Events, e => e.Kind == "threshold");
        Assert.False(threshold.Sound);
        Assert.Equal(80, threshold.Percent);
    }

    /// <summary>Only a threshold has a percentage to report, and a zero would be one.</summary>
    [Fact]
    public void NothingButAThresholdCarriesAPercent()
    {
        Assert.Null(Assert.Single(Load().Events, e => e.Kind == "limit_reached").Percent);
    }
}
