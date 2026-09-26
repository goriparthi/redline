// When RedLine is allowed to interrupt. Most of these assert silence, the harder half: a
// monitor that cries wolf gets its notifications switched off.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class AlertingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
    private readonly Config _config = new() { Alerts = true };

    private static LimitWindow Window(double utilization, double resetsIn = 3600, string key = "five_hour") =>
        new("Claude", key, utilization, Now.AddSeconds(resetsIn), Provenance.Official);

    [Fact]
    public void FiresOncePerThresholdPerWindow()
    {
        var state = new AlertState();
        var events = Alerting.Evaluate(new[] { Window(88) }, _config, state, now: Now);
        Assert.Equal(2, events.Count); // 60 and 85 are both newly crossed
        events = Alerting.Evaluate(new[] { Window(89) }, _config, state, now: Now.AddSeconds(300));
        Assert.Empty(events); // still past the same thresholds, so still not news
    }

    [Fact]
    public void StaleReadingsNeverFire()
    {
        var state = new AlertState();
        var events = Alerting.Evaluate(new[] { Window(99) }, _config, state, now: Now, isStale: _ => true);
        Assert.Empty(events);
        // the reading is still recorded, so the next fresh one is not a reset
        Assert.Single(state.Windows);
    }

    [Fact]
    public void AlertsOffMeansSilence()
    {
        var off = new Config { Alerts = false };
        var state = new AlertState();
        Assert.Empty(Alerting.Evaluate(new[] { Window(99) }, off, state, now: Now));
    }

    [Fact]
    public void LimitReachedIsItsOwnEvent()
    {
        var state = new AlertState();
        var events = Alerting.Evaluate(new[] { Window(100) }, _config, state, now: Now);
        Assert.Single(events);
        Assert.Equal(new AlertKind.LimitReached(), events[0].Kind);
    }

    [Fact]
    public void ResetIsAnnouncedOnlyForAWindowThatWasBeingUsed()
    {
        var state = new AlertState();
        Alerting.Evaluate(new[] { Window(70) }, _config, state, now: Now);
        // Same window key, new reset time, back down to nothing
        var rolled = new LimitWindow("Claude", "five_hour", 1, Now.AddSeconds(18000), Provenance.Official);
        var events = Alerting.Evaluate(new[] { rolled }, _config, state, now: Now.AddSeconds(3600));
        Assert.Contains(events, e => e.Kind is AlertKind.Reset);
    }

    [Fact]
    public void ResetOfAnUntouchedWindowIsNotNews()
    {
        var state = new AlertState();
        Alerting.Evaluate(new[] { Window(3) }, _config, state, now: Now);
        var rolled = new LimitWindow("Claude", "five_hour", 0, Now.AddSeconds(18000), Provenance.Official);
        var events = Alerting.Evaluate(new[] { rolled }, _config, state, now: Now.AddSeconds(3600));
        Assert.DoesNotContain(events, e => e.Kind is AlertKind.Reset);
    }

    [Fact]
    public void NewWindowInstanceRearmsThresholds()
    {
        var state = new AlertState();
        Alerting.Evaluate(new[] { Window(90) }, _config, state, now: Now);
        var next = new LimitWindow("Claude", "five_hour", 90, Now.AddSeconds(18000), Provenance.Official);
        var events = Alerting.Evaluate(new[] { next }, _config, state, now: Now.AddSeconds(7200));
        // a fresh window that is already deep in is worth saying again
        Assert.Contains(events, e => e.Kind == new AlertKind.Threshold(85));
    }

    [Fact]
    public void ProjectionFiresOnlyInsideTheHorizon()
    {
        var state = new AlertState();
        var w = Window(50, resetsIn: 4 * 3600);
        // Racing: half the window gone in the first hour, so the cap lands well before reset
        var samples = new[]
        {
            new LimitSample(Now.AddSeconds(-3600), "Claude", "five_hour", 10, w.ResetsAt, Provenance.Official),
        };
        var paces = PaceEstimator.Paces(new[] { w }, samples, Now);
        Assert.True(paces.FirstOrDefault()?.HitsLimitBeforeReset ?? false);
        var events = Alerting.Evaluate(new[] { w }, _config, state, paces, Now);
        Assert.Contains(events, e => e.Kind is AlertKind.Projection);
        // And only once
        var again = Alerting.Evaluate(new[] { w }, _config, state, paces, Now.AddSeconds(60));
        Assert.DoesNotContain(again, e => e.Kind is AlertKind.Projection);
    }

    [Fact]
    public void ThresholdsFollowTheConfiguredColours()
    {
        var custom = new Config { LimitYellowPct = 50, LimitRedPct = 75 };
        Assert.Equal(new[] { 50, 75, 95 }, Alerting.Thresholds(custom));
    }

    [Fact]
    public void StateFileRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redline-alerts-{Guid.NewGuid()}.json");
        try
        {
            var state = new AlertState();
            Alerting.Evaluate(new[] { Window(88) }, _config, state, now: Now);
            Assert.True(AlertStore.Save(state, path));
            Assert.Equal(state, AlertStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
