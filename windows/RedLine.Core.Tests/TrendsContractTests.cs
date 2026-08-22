using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// The dashboard's wire format, against the same fixture the Swift suite asserts against. The
/// fixture is engine output for fixed inputs rather than a live run, because the numbers move
/// with the clock and the shape must not.
/// </summary>
public class TrendsContractTests
{
    private static TrendReport Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "trends.json");
        var parsed = TrendJson.Parse(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void TheReportTheEnginePublishesParses()
    {
        var report = Load();
        Assert.Equal(3, report.Days);
        Assert.Equal(1, report.LabelEveryDays);
        Assert.Equal("local", report.DayBasis);
        Assert.Equal(14_900, report.Tokens);
        Assert.True(report.Available);
        Assert.False(report.IsEmpty);
    }

    /// <summary>
    /// The combined series is what one chart draws, and it has to be the providers added up.
    /// A dashboard that summed them itself could disagree with the engine about a day.
    /// </summary>
    [Fact]
    public void TheSeriesIsEveryProviderAddedUp()
    {
        var report = Load();
        Assert.Equal(3, report.Series.Count);
        Assert.Equal([6200, 5700, 3000], report.Series.Select(p => p.Tokens));
        Assert.Equal(["Aug 20", "Aug 21", "Aug 22"], report.Series.Select(p => p.Label));

        foreach (var (point, index) in report.Series.Select((p, i) => (p, i)))
        {
            Assert.Equal(point.Tokens, report.Providers.Sum(t => t.Points[index].Tokens));
        }
    }

    /// <summary>A day nobody worked is a zero in the series, not a missing date: dropping it
    /// would slide every later day left and draw a week that never happened.</summary>
    [Fact]
    public void AQuietDayIsPresentAsAZero()
    {
        var codex = Assert.Single(Load().Providers, p => p.Provider == "Codex");
        Assert.Equal(3, codex.Points.Count);
        Assert.Equal(0, codex.Points[0].Tokens);
        Assert.Equal("2026-08-20", codex.Points[0].Day);
    }

    [Fact]
    public void TheUnpricedFlagSurvivesWithTheModelItBelongsTo()
    {
        var report = Load();
        Assert.True(report.HasUnpriced);
        var codex = Assert.Single(report.Models, m => m.Provider == "Codex");
        Assert.False(codex.Priced);
        Assert.Equal("n/a", TrendChart.CostOf(codex));
        Assert.Contains("at least", report.Summary);
    }

    /// <summary>The bars a real report draws, end to end from the file.</summary>
    [Fact]
    public void TheFixtureDrawsBars()
    {
        var bars = TrendChart.Bars(Load());
        Assert.Equal(3, bars.Count);
        Assert.Equal(1.0, bars[0].Share);
        Assert.All(bars, b => Assert.NotEqual("", b.Label));
    }
}
