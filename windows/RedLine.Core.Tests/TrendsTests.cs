using RedLine.Core;

namespace RedLine.Core.Tests;

public class TrendsTests
{
    private static TrendReport Report(params (string Day, long Tokens)[] days) => new()
    {
        Days = days.Length,
        LabelEveryDays = 1,
        Tokens = days.Sum(d => d.Tokens),
        Series = days.Select(d => new TrendPoint
        {
            Day = d.Day, Label = d.Day[5..], Tokens = d.Tokens,
        }).ToList(),
    };

    [Fact]
    public void BarsAreMeasuredAgainstTheBusiestDay()
    {
        var bars = TrendChart.Bars(Report(("2026-08-20", 100), ("2026-08-21", 50),
                                          ("2026-08-22", 0)));
        Assert.Equal([1.0, 0.5, 0.0], bars.Select(b => b.Share));
    }

    /// <summary>
    /// A fortnight with nothing in it is still a fortnight. Dividing by a peak of zero would
    /// be a chart of NaN, which draws as nothing at all rather than as a flat line.
    /// </summary>
    [Fact]
    public void AnEmptyWindowIsFlatRatherThanNaN()
    {
        var bars = TrendChart.Bars(Report(("2026-08-20", 0), ("2026-08-21", 0)));
        Assert.All(bars, b => Assert.Equal(0, b.Share));
        Assert.DoesNotContain(bars, b => double.IsNaN(b.Share));
    }

    [Fact]
    public void NothingToDrawIsNoBarsRatherThanAThrow()
    {
        Assert.Empty(TrendChart.Bars(new TrendReport()));
    }

    /// <summary>
    /// Labels are counted back from the newest day. Counting forward labels the oldest and
    /// can leave the right hand end bare, which is the end anyone is actually looking at.
    /// </summary>
    [Fact]
    public void TheNewestDayIsAlwaysLabelled()
    {
        var report = Report(("2026-08-18", 1), ("2026-08-19", 1), ("2026-08-20", 1),
                            ("2026-08-21", 1), ("2026-08-22", 1)) with { LabelEveryDays = 2 };
        var bars = TrendChart.Bars(report);
        Assert.Equal(["08-18", "", "08-20", "", "08-22"], bars.Select(b => b.Label));
    }

    [Fact]
    public void EveryDayIsLabelledWhenTheStrideIsOne()
    {
        var bars = TrendChart.Bars(Report(("2026-08-21", 1), ("2026-08-22", 1)));
        Assert.All(bars, b => Assert.NotEqual("", b.Label));
    }

    /// <summary>An unpriced model has no cost to show, and a zero would be one someone
    /// might read as a fact.</summary>
    [Fact]
    public void AnUnpricedModelReadsAsNotApplicable()
    {
        Assert.Equal("n/a", TrendChart.CostOf(new ModelShare { Cost = 0, Priced = false }));
        Assert.Equal("$1.50", TrendChart.CostOf(new ModelShare { Cost = 1.5, Priced = true }));
    }

    [Fact]
    public void TheModelMixSaysHowManyItLeftOut()
    {
        var report = new TrendReport
        {
            Models = Enumerable.Range(0, 9)
                .Select(i => new ModelShare { Model = $"m{i}", Tokens = 10 - i }).ToList(),
            Tokens = 100,
        };
        var (shown, hidden) = TrendChart.TopModels(report, 6);
        Assert.Equal(6, shown.Count);
        Assert.Equal(3, hidden);
        Assert.Equal(0.1, TrendChart.Share(shown[0], report));
    }

    /// <summary>
    /// The summary is the one line above the chart, so the floor has to be in the words.
    /// </summary>
    [Fact]
    public void TheSummarySaysWhenTheCostIsOnlyAFloor()
    {
        var report = new TrendReport
        {
            Days = 14, Tokens = 1_200_000, Cost = 3.5, HasUnpriced = true,
            Series = [new TrendPoint { Tokens = 1_200_000 }],
        };
        Assert.Equal("1.2M over 14 days, at least $3.50", report.Summary);
        Assert.Equal("1.2M over 14 days, $3.50", (report with { HasUnpriced = false }).Summary);
    }

    [Fact]
    public void AQuietWindowSaysSoRatherThanShowingZero()
    {
        var report = Report(("2026-08-21", 0), ("2026-08-22", 0));
        Assert.True(report.IsEmpty);
        Assert.Contains("Nothing recorded", report.Summary);
        Assert.Null(report.Busiest);
    }

    [Fact]
    public void TheProblemIsWhatTheSummarySaysWhenThereIsOne()
    {
        var report = TrendReport.Unavailable("redline was not found");
        Assert.False(report.Available);
        Assert.Equal("redline was not found", report.Summary);
    }

    [Fact]
    public void TheBusiestDayIsTheOneToPointAt()
    {
        var report = Report(("2026-08-20", 10), ("2026-08-21", 90), ("2026-08-22", 5));
        Assert.Equal("2026-08-21", report.Busiest?.Day);
    }
}
