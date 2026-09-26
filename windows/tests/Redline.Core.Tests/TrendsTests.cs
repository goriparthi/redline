// Chart bucketing and the daily axis cadence. A fixed zone and instant keep bucket
// boundaries deterministic.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class TrendsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000); // 2023-11-14 22:13:20 UTC

    private static readonly Config Cfg = new()
    {
        Pricing = new() { ["sonnet"] = new ModelPrice(3, 15, 0.3) },
    };

    private static Entry MakeEntry(string provider, string model, DateTimeOffset at,
                                   int input = 0, int output = 0) =>
        new(provider, null, at, model, input, output, 0, 0, 0);

    [Fact]
    public void DailyBucketsCoverTheWholeRangeIncludingQuietDays()
    {
        var entries = new[] { MakeEntry("Claude", "claude-sonnet-5", Now, input: 100, output: 10) };
        var trends = Trends.Trend(entries, Bucketing.Day, 7, Cfg, Now, Utc);
        Assert.Single(trends);
        Assert.Equal(7, trends[0].Points.Count); // a quiet day is a zero, not a missing point
        Assert.Equal(110, trends[0].Points[^1].Io); // today is the final bucket
        Assert.True(trends[0].Points.Take(trends[0].Points.Count - 1).All(p => p.Io == 0));
    }

    [Fact]
    public void BucketsAreOrderedOldestFirst()
    {
        var starts = Trends.BucketStarts(Bucketing.Day, 5, Now, Utc);
        Assert.Equal(5, starts.Count);
        Assert.Equal(starts.OrderBy(s => s), starts); // charts read left to right
        Assert.Equal(Trends.StartOf(Bucketing.Day, Now, Utc), starts[^1]);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 0, 0, 0, TimeSpan.Zero), starts[^1]);
    }

    [Fact]
    public void EntriesOlderThanTheRangeAreExcluded()
    {
        var old = Now.AddDays(-30);
        var trends = Trends.Trend(new[] { MakeEntry("Claude", "claude-sonnet-5", old, input: 5) },
                                  Bucketing.Day, 7, Cfg, Now, Utc);
        Assert.Empty(trends); // no provider had usage inside the window
    }

    [Fact]
    public void SeparateProvidersGetSeparateSeries()
    {
        var entries = new[]
        {
            MakeEntry("Claude", "claude-sonnet-5", Now, input: 100),
            MakeEntry("Codex", "gpt-5.3-codex", Now, input: 50),
        };
        var trends = Trends.Trend(entries, Bucketing.Day, 3, Cfg, Now, Utc);
        Assert.Equal(new[] { "Claude", "Codex" }, trends.Select(t => t.Provider)); // sorted for stable colours
        Assert.Equal(100, trends[0].TotalIO);
        Assert.Equal(50, trends[1].TotalIO);
    }

    [Fact]
    public void HourlyBucketing()
    {
        var anHourAgo = Now.AddHours(-1);
        var entries = new[]
        {
            MakeEntry("Claude", "claude-sonnet-5", Now, input: 10),
            MakeEntry("Claude", "claude-sonnet-5", anHourAgo, input: 20),
        };
        var trends = Trends.Trend(entries, Bucketing.Hour, 3, Cfg, Now, Utc);
        Assert.Equal(3, trends[0].Points.Count);
        Assert.Equal(10, trends[0].Points[^1].Io);
        Assert.Equal(20, trends[0].Points[1].Io);
    }

    [Fact]
    public void CostMatchesTheMenuBarAggregation()
    {
        var e = new Entry("Claude", null, Now, "claude-sonnet-5", 1_000_000, 0, 0, 0, 0);
        var trend = Trends.Trend(new[] { e }, Bucketing.Day, 1, Cfg, Now, Utc);
        var agg = Usage.Aggregate(new[] { e }, Now.AddSeconds(-60), Cfg);
        // charts and the menu must not disagree on spend
        Assert.Equal(agg.Cost, trend[0].TotalCost, 4);
    }

    [Fact]
    public void PeakFindsTheBusiestBucket()
    {
        var yesterday = Now.AddDays(-1);
        var entries = new[]
        {
            MakeEntry("Claude", "claude-sonnet-5", Now, input: 10),
            MakeEntry("Claude", "claude-sonnet-5", yesterday, input: 900),
        };
        var trend = Trends.Trend(entries, Bucketing.Day, 3, Cfg, Now, Utc);
        Assert.Equal(900, trend[0].Peak?.Io);
    }

    [Fact]
    public void ByModelRanksAndFlagsUnpriced()
    {
        var entries = new[]
        {
            MakeEntry("Claude", "claude-sonnet-5", Now, input: 100, output: 0),
            MakeEntry("Codex", "gpt-5.3-codex", Now, input: 500, output: 0),
        };
        var shares = Trends.ByModel(entries, Now.AddSeconds(-3600), Cfg);
        Assert.Equal(new[] { "gpt-5.3-codex", "claude-sonnet-5" }, shares.Select(s => s.Model)); // largest first
        Assert.False(shares[0].Priced); // no pricing entry for the Codex model
        Assert.Equal(0, shares[0].Cost);
        Assert.True(shares[1].Priced);
    }

    [Fact]
    public void ZeroCountReturnsNothingRatherThanCrashing()
    {
        Assert.Empty(Trends.Trend(Array.Empty<Entry>(), Bucketing.Day, 0, Cfg, Now, Utc));
    }

    // Windows-port additions: local-zone bucketing is what the injected zone exists for

    [Fact]
    public void DailyBucketsFollowTheInjectedZonesMidnight()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(9), "t", "t");
        var start = Trends.StartOf(Bucketing.Day, Now, tokyo);
        // 22:13 UTC on the 14th is 07:13 on the 15th in UTC+9, whose midnight is 15:00 UTC
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 15, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void HourBucketsInAHalfHourZoneStartOnTheLocalHour()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("i", TimeSpan.FromMinutes(330), "i", "i");
        var start = Trends.StartOf(Bucketing.Hour, Now, india);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 21, 30, 0, TimeSpan.Zero), start);
    }
}

/// The axis cadence is what keeps a long range readable rather than a smear of dates.
public sealed class DailyAxisTests
{
    [Fact]
    public void EveryOfferedRangeGetsAReadableNumberOfLabels()
    {
        // The ranges the dashboard offers. Each should land in single digits of labels.
        foreach (var range in new[] { 7, 14, 30, 60, 90 })
        {
            var stride = DailyAxis.StrideDays(range);
            var labels = range / stride;
            Assert.True(labels >= 5, $"{range}d gives only {labels} labels");
            Assert.True(labels <= 8, $"{range}d gives {labels} labels, a smear");
        }
    }

    /// Both daily charts read this, so a change here must move them together
    [Fact]
    public void StrideNeverShrinksAsTheRangeGrows()
    {
        int last = 0;
        for (int range = 1; range <= 400; range++)
        {
            var stride = DailyAxis.StrideDays(range);
            Assert.True(stride >= last, $"stride shrank at {range}");
            Assert.True(stride > 0);
            last = stride;
        }
    }
}
