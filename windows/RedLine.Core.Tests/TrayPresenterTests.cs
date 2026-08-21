using RedLine.Core;

namespace RedLine.Core.Tests;

public class TrayPresenterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static Snapshot Make(double? utilization, DateTimeOffset? updated = null,
                                 bool hasUnpriced = false, long io = 13740,
                                 double cost = 0.05)
    {
        return new Snapshot
        {
            UpdatedAt = updated ?? Now,
            Limits = utilization is null
                ? []
                : [new LimitWindow
                {
                    Provider = "Claude", Key = "five_hour", Utilization = utilization.Value,
                }],
            Today = new Totals { Io = io, Cost = cost, HasUnpriced = hasUnpriced },
        };
    }

    /// <summary>
    /// Nothing read yet is not a reading of zero, and must never be drawn as one.
    /// </summary>
    [Fact]
    public void NoSnapshotIsUnknownRatherThanZero()
    {
        var view = TrayPresenter.From(null, Now);
        Assert.Equal(TrayLevel.Unknown, view.Level);
        Assert.DoesNotContain("0%", view.Title);
    }

    [Theory]
    [InlineData(0, TrayLevel.Healthy)]
    [InlineData(59.9, TrayLevel.Healthy)]
    [InlineData(60, TrayLevel.Approaching)]
    [InlineData(84.9, TrayLevel.Approaching)]
    [InlineData(85, TrayLevel.AtLimit)]
    [InlineData(100, TrayLevel.AtLimit)]
    public void ThresholdsMatchTheEngines(double utilization, TrayLevel expected)
    {
        Assert.Equal(expected, TrayPresenter.From(Make(utilization), Now).Level);
    }

    [Fact]
    public void ThePercentageIsWhatTheTrayShows()
    {
        Assert.Equal("42%", TrayPresenter.From(Make(42), Now).Title);
    }

    /// <summary>
    /// A reading past its freshness is the last known one, not the current one. Drawing it as
    /// current is how someone acts on a number that stopped being true hours ago.
    /// </summary>
    [Fact]
    public void AnOldReadingIsDrawnAsStaleWhateverItSays()
    {
        var old = Make(95, updated: Now.AddHours(-2));
        var view = TrayPresenter.From(old, Now);
        Assert.Equal(TrayLevel.Stale, view.Level);
        Assert.Equal("Last known reading", view.Phrase);
    }

    [Fact]
    public void AReadingInsideTheFreshnessWindowIsCurrent()
    {
        var recent = Make(95, updated: Now.AddMinutes(-14));
        Assert.Equal(TrayLevel.AtLimit, TrayPresenter.From(recent, Now).Level);
    }

    /// <summary>
    /// An unpriced model makes the cost a floor. Presenting it as exact is exactly the kind of
    /// invented figure the project forbids.
    /// </summary>
    [Fact]
    public void AnUnpricedTotalIsDescribedAsAFloor()
    {
        var view = TrayPresenter.From(Make(42, hasUnpriced: true, cost: 12.274), Now);
        Assert.Contains("at least $12.27", view.Detail);
    }

    [Fact]
    public void APricedTotalIsStatedPlainly()
    {
        var view = TrayPresenter.From(Make(42, hasUnpriced: false, cost: 12.274), Now);
        Assert.Contains("$12.27", view.Detail);
        Assert.DoesNotContain("at least", view.Detail);
    }

    [Fact]
    public void WithNoLimitsTheTitleFallsBackToTokens()
    {
        var view = TrayPresenter.From(Make(null), Now);
        Assert.Equal("13.7K", view.Title);
    }

    [Fact]
    public void TheDetailNamesTheWindowBeingReported()
    {
        Assert.Contains("Claude Session · 5h", TrayPresenter.From(Make(42), Now).Detail);
    }

    /// <summary>Every level says something. A colour on its own carries nothing.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(70)]
    [InlineData(90)]
    public void EveryLevelCarriesWords(double utilization)
    {
        Assert.False(string.IsNullOrWhiteSpace(
            TrayPresenter.From(Make(utilization), Now).Phrase));
    }
}
