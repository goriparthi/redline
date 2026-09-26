// Cues about how the work is spread out. The rules pinned are the ones that keep it from
// becoming a nag: once per stretch, once per night, nothing when off or when the data is old.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class CadenceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Entry Entry(DateTimeOffset ts) =>
        new("Claude", Guid.NewGuid().ToString(), ts, "claude-sonnet-5", 100, 10, 0, 0, 0);

    /// 2026-08-18 09:00:00 UTC
    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeSeconds(1_787_043_600);

    private static List<Entry> Run(IEnumerable<double> minutes) =>
        minutes.Select(m => Entry(Base.AddSeconds(m * 60))).ToList();

    private static IEnumerable<double> Stride(double from, double through, double by)
    {
        for (var x = from; x <= through; x += by) yield return x;
    }

    // MARK: - Shapes

    [Fact]
    public void AGapLongerThanTheBreakSplitsTheStretch()
    {
        var stretches = Cadence.Stretches(Run(new double[] { 0, 5, 10, 40, 45 }));
        Assert.Equal(2, stretches.Count);
        Assert.Equal(600, stretches[0].Length);
        Assert.Equal(300, stretches[1].Length);
    }

    [Fact]
    public void AShortPauseDoesNotSplitAStretch()
    {
        // A slow turn or a build is not a break.
        Assert.Single(Cadence.Stretches(Run(new double[] { 0, 12, 24 })));
    }

    [Fact]
    public void StretchLengthIsMeasuredToTheLastActivityNotToNow()
    {
        var stretch = Cadence.Stretches(Run(new double[] { 0, 10, 20, 30 })).LastOrDefault();
        // a counter that climbs while nothing happens is not a measurement
        Assert.Equal(1800, stretch?.Length);
    }

    [Fact]
    public void CurrentStretchClosesOnceTheGapHasPassed()
    {
        var entries = Run(new double[] { 0, 10, 20, 30 });
        Assert.NotNull(Cadence.Current(entries, now: Base.AddSeconds(35 * 60)));
        Assert.Null(Cadence.Current(entries, now: Base.AddSeconds(60 * 60)));
    }

    [Fact]
    public void StreakCountsBackFromTodayAndStopsAtTheFirstGap()
    {
        var entries = new[] { 0, 1, 2, 4, 5 }.Select(d => Entry(Base.AddSeconds(-d * 86400.0))).ToList();
        Assert.Equal(3, Cadence.Streak(entries, Base, Utc));
    }

    [Fact]
    public void StreakIsZeroWhenTodayHasNothing()
    {
        var entries = new[] { Entry(Base.AddSeconds(-2 * 86400)) };
        Assert.Equal(0, Cadence.Streak(entries, Base, Utc));
    }

    [Fact]
    public void HourHistogramBucketsByLocalHour()
    {
        var hours = Cadence.ByHourOfDay(Run(new double[] { 0, 1, 120 }), Utc);
        Assert.Equal(220, hours[9]);
        Assert.Equal(110, hours[11]);
        Assert.Equal(330, hours.Sum());
    }

    [Fact]
    public void NightRollsAtFiveNotAtMidnight()
    {
        // 01:30 belongs to the evening that ran into it.
        var lateEvening = Base.AddSeconds(14 * 3600);       // 23:00
        var afterMidnight = Base.AddSeconds(16.5 * 3600);   // 01:30 next day
        Assert.Equal(Cadence.NightKey(lateEvening, Utc), Cadence.NightKey(afterMidnight, Utc));
    }

    /// Windows port only: the injected zone decides the local hour, not the machine's own.
    [Fact]
    public void LocalHourFollowsTheInjectedTimeZone()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        var hours = Cadence.ByHourOfDay(Run(new double[] { 0 }), tokyo);
        Assert.Equal(110, hours[18]);
        // 16:00 UTC on the 18th is 01:00 on the 19th in Tokyo: a late night, not a new day
        var late = Base.AddSeconds(7 * 3600);
        Assert.Equal(Cadence.NightKey(Base.AddSeconds(-4 * 3600), tokyo), Cadence.NightKey(late, tokyo));
    }

    // MARK: - Cues

    private static Config Cfg() => new()
    {
        MindfulCues = true,
        StretchMinutes = 90,
        LateHour = 23,
        StreakDays = 7,
    };

    [Fact]
    public void CuesAreSilentWhenTheSettingIsOff()
    {
        var state = new CadenceState();
        var off = Cfg();
        off.MindfulCues = false;
        var entries = Run(Stride(0, 240, 10));
        var cues = CadenceRules.Evaluate(entries, off, state, Base.AddSeconds(240 * 60), Utc);
        Assert.Empty(cues);
    }

    [Fact]
    public void AStretchIsAnnouncedOnceAndThenAtTheNextMultiple()
    {
        var state = new CadenceState();
        var minutes = Stride(0, 200, 10).ToList();
        // At 100 minutes: one threshold reached
        var atFirst = CadenceRules.Evaluate(Run(minutes.Where(m => m <= 100)), Cfg(), state,
            Base.AddSeconds(105 * 60), Utc);
        Assert.Single(atFirst);
        Assert.IsType<CadenceCueKind.Stretch>(atFirst[0].Kind);

        // Ten minutes later, still the same run: nothing new to say
        var again = CadenceRules.Evaluate(Run(minutes.Where(m => m <= 110)), Cfg(), state,
            Base.AddSeconds(115 * 60), Utc);
        Assert.Empty(again); // a stretch says something once per threshold, not per poll

        // Past three hours: the second multiple
        var atSecond = CadenceRules.Evaluate(Run(minutes), Cfg(), state, Base.AddSeconds(205 * 60), Utc);
        Assert.Single(atSecond);
    }

    [Fact]
    public void ANewStretchRearmsTheCue()
    {
        var state = new CadenceState();
        var first = Stride(0, 100, 10).ToList();
        CadenceRules.Evaluate(Run(first), Cfg(), state, Base.AddSeconds(105 * 60), Utc);
        // A real break, then a second long run
        var second = first.Concat(Stride(200, 300, 10));
        var cues = CadenceRules.Evaluate(Run(second), Cfg(), state, Base.AddSeconds(305 * 60), Utc);
        Assert.Single(cues);
    }

    [Fact]
    public void LateCueFiresOncePerNight()
    {
        var state = new CadenceState();
        var cfg = Cfg();
        cfg.StretchMinutes = 600; // keep the stretch rule out of this test
        var lateOne = Base.AddSeconds(14 * 3600 + 600);  // 23:10
        var lateTwo = Base.AddSeconds(15 * 3600);        // 00:00, same night
        var entries = new List<Entry> { Entry(lateOne) };
        var first = CadenceRules.Evaluate(entries, cfg, state, lateOne.AddSeconds(60), Utc);
        Assert.Single(first);
        Assert.IsType<CadenceCueKind.Late>(first[0].Kind);

        entries.Add(Entry(lateTwo));
        var second = CadenceRules.Evaluate(entries, cfg, state, lateTwo.AddSeconds(60), Utc);
        Assert.Empty(second); // the same night is one cue
    }

    [Fact]
    public void LateCueNeedsRecentActivityNotAMemoryOfIt()
    {
        var state = new CadenceState();
        var cfg = Cfg();
        cfg.StretchMinutes = 600;
        var lateLastNight = Base.AddSeconds(14 * 3600);
        var cues = CadenceRules.Evaluate(new[] { Entry(lateLastNight) }, cfg, state,
            lateLastNight.AddSeconds(4 * 3600), Utc);
        Assert.Empty(cues); // an old reading is not news about tonight
    }

    [Fact]
    public void StreakIsAnnouncedAtItsThresholdAndNotEveryDayAfter()
    {
        var cfg = Cfg();
        cfg.StretchMinutes = 600;
        var state = new CadenceState();
        List<Entry> Entries(int days) =>
            Enumerable.Range(0, days).Select(d => Entry(Base.AddSeconds(-d * 86400.0))).ToList();

        var atSeven = CadenceRules.Evaluate(Entries(7), cfg, state, Base, Utc);
        Assert.Single(atSeven);
        var streak = Assert.IsType<CadenceCueKind.Streak>(atSeven[0].Kind);
        Assert.Equal(7, streak.Days);

        var atEight = CadenceRules.Evaluate(Entries(8), cfg, state, Base, Utc);
        Assert.Empty(atEight); // an eighth day is not a second announcement

        var atFourteen = CadenceRules.Evaluate(Entries(14), cfg, state, Base, Utc);
        Assert.Single(atFourteen); // the next multiple is worth saying
    }

    [Fact]
    public void NoEntriesMeansNoCues()
    {
        var state = new CadenceState();
        Assert.Empty(CadenceRules.Evaluate(new List<Entry>(), Cfg(), state, Base, Utc));
    }

    [Fact]
    public void StateRoundTripsThroughItsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadence-{Guid.NewGuid()}.json");
        try
        {
            var state = new CadenceState
            {
                StretchID = "stretch|123",
                StretchFired = 2,
                LastLateNight = "2026-08-18",
                StreakFired = 1,
            };
            Assert.True(CadenceStore.Save(state, path));
            Assert.Equal(state, CadenceStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
