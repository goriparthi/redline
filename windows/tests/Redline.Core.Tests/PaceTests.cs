// Burn rate and projection. The rules that matter keep it quiet: no projection without a
// length, without elapsed time, or across a window that rolled over.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class PaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private static LimitWindow Window(string key = "five_hour", double utilization = 0, double resetsIn = 0) =>
        new("Claude", key, utilization, Now.AddSeconds(resetsIn), Provenance.Official);

    [Fact]
    public void WindowAverageProjectsFromElapsedTime()
    {
        // Half way through a five hour window at 60% used: the cap arrives before the reset
        var w = Window(utilization: 60, resetsIn: 2.5 * 3600);
        var pace = PaceEstimator.Pace(w, now: Now);
        Assert.NotNull(pace);
        Assert.Equal(24, pace!.RatePerHour, 2);
        Assert.Equal(0.5, pace.ElapsedFraction ?? 0, 3);
        Assert.True(pace.HitsLimitBeforeReset);
        var toLimit = pace.TimeToLimit(Now);
        Assert.NotNull(toLimit);
        Assert.InRange(toLimit!.Value / 60, 99, 101); // 40 points at 24/hour is 100 minutes
    }

    [Fact]
    public void OnPaceDoesNotClaimTheCap()
    {
        var w = Window(utilization: 50, resetsIn: 2.5 * 3600);
        var pace = PaceEstimator.Pace(w, now: Now);
        Assert.NotNull(pace);
        Assert.False(pace!.HitsLimitBeforeReset);
        Assert.Equal("on pace", pace.Summary(Now));
    }

    [Fact]
    public void MeasuredRateBeatsTheWindowAverage()
    {
        // Quiet for hours, then a burst. Averaged over the window this looks survivable;
        // the last half hour says the cap arrives well before the reset does.
        var w = Window(utilization: 40, resetsIn: 2 * 3600);
        var reset = w.ResetsAt!.Value;
        var samples = new[]
        {
            new LimitSample(Now.AddSeconds(-1800), "Claude", "five_hour", 10, reset, Provenance.Official),
        };
        // the window average alone would not raise this
        Assert.False(PaceEstimator.Pace(w, now: Now)?.HitsLimitBeforeReset ?? true);
        var pace = PaceEstimator.Pace(w, samples, Now);
        Assert.NotNull(pace);
        Assert.Equal(60, pace!.RatePerHour, 2);
        var measured = Assert.IsType<PaceBasis.Measured>(pace.Basis); // expected a measured basis
        Assert.InRange(measured.Over, 1799, 1801);
        Assert.True(pace.HitsLimitBeforeReset);
    }

    [Fact]
    public void ReadingsOlderThanTheLookbackAreNotTheCurrentRate()
    {
        // A five hour window looks at its recent stretch; an hour-old reading describes
        // what was happening then, not now.
        var w = Window(utilization: 40, resetsIn: 3600);
        var reset = w.ResetsAt!.Value;
        var samples = new[]
        {
            new LimitSample(Now.AddSeconds(-3600), "Claude", "five_hour", 10, reset, Provenance.Official),
        };
        var pace = PaceEstimator.Pace(w, samples, Now);
        Assert.NotNull(pace);
        Assert.Equal(new PaceBasis.WindowAverage(), pace!.Basis);
    }

    [Fact]
    public void SamplesFromThePreviousWindowAreIgnored()
    {
        var w = Window(utilization: 12, resetsIn: 3600);
        // Same key, different reset time: this belongs to the window that already rolled over
        var stale = new[]
        {
            new LimitSample(Now.AddSeconds(-3600), "Claude", "five_hour", 95,
                            Now.AddSeconds(-1800), Provenance.Official),
        };
        var pace = PaceEstimator.Pace(w, stale, Now);
        Assert.NotNull(pace);
        // differencing across a rollover would invent a negative rate
        Assert.Equal(new PaceBasis.WindowAverage(), pace!.Basis);
    }

    [Fact]
    public void TwoReadingsTooCloseTogetherAreNotARate()
    {
        var w = Window(utilization: 40, resetsIn: 3600);
        var reset = w.ResetsAt!.Value;
        var samples = new[]
        {
            new LimitSample(Now.AddSeconds(-60), "Claude", "five_hour", 39, reset, Provenance.Official),
        };
        var pace = PaceEstimator.Pace(w, samples, Now);
        Assert.NotNull(pace);
        Assert.Equal(new PaceBasis.WindowAverage(), pace!.Basis);
    }

    [Fact]
    public void AWeeklyWindowNeedsHoursOfReadingsNotMinutes()
    {
        // A busy quarter of an hour is not a week's rate. This shipped once as "23h to
        // limit" on a window 5% used with six days left.
        var w = Window("seven_day", utilization: 5, resetsIn: 6.5 * 86400);
        var reset = w.ResetsAt!.Value;
        var recent = new[]
        {
            new LimitSample(Now.AddSeconds(-900), "Claude", "seven_day", 4, reset, Provenance.Official),
        };
        var pace = PaceEstimator.Pace(w, recent, Now);
        Assert.NotNull(pace);
        Assert.Equal(new PaceBasis.WindowAverage(), pace!.Basis);
        Assert.False(pace.HitsLimitBeforeReset);

        // Seven hours of readings is long enough to mean something about a week
        var longer = new[]
        {
            new LimitSample(Now.AddSeconds(-8 * 3600), "Claude", "seven_day", 1, reset, Provenance.Official),
        };
        var measured = PaceEstimator.Pace(w, longer, Now);
        Assert.NotNull(measured);
        Assert.IsType<PaceBasis.Measured>(measured!.Basis); // expected a measured basis
    }

    [Fact]
    public void UnknownWindowLengthSaysNothing()
    {
        var w = new LimitWindow("Claude", "nimbus_quill", 50, Now.AddSeconds(3600));
        Assert.Null(PaceEstimator.Pace(w, now: Now));
    }

    [Fact]
    public void AWindowWithNoUsageYetSaysNothing()
    {
        var w = Window(utilization: 0, resetsIn: 4 * 3600);
        Assert.Null(PaceEstimator.Pace(w, now: Now));
    }

    [Fact]
    public void WeeklyWindowLengthIsRecognised()
    {
        var w = Window("seven_day_opus", utilization: 50, resetsIn: 3.5 * 86400);
        var pace = PaceEstimator.Pace(w, now: Now);
        Assert.NotNull(pace);
        Assert.Equal(0.5, pace!.ElapsedFraction ?? 0, 3);
    }

    [Fact]
    public void ShortFormatting()
    {
        Assert.Equal("40m", Pace.Short(40 * 60));
        Assert.Equal("2h 10m", Pace.Short(2 * 3600 + 10 * 60));
        Assert.Equal("3d 4h", Pace.Short(3 * 86400 + 4 * 3600));
        Assert.Equal("under a minute", Pace.Short(20));
    }

    [Fact]
    public void WorstFirstOrdering()
    {
        var soon = Window(utilization: 90, resetsIn: 3600);
        var later = Window("seven_day", utilization: 20, resetsIn: 3 * 86400);
        var paces = PaceEstimator.Paces(new[] { later, soon }, now: Now);
        Assert.Equal("five_hour", paces.FirstOrDefault()?.Key);
    }
}
