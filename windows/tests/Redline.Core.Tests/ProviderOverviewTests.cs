// Which state a provider card resolves to, and what it says when a figure is missing.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class ProviderCardStateTests
{
    private static ProviderUsage Usage(int io, double cost = 1) => new() { Io = io, Cost = cost };

    [Fact]
    public void AMissingToolIsNotInstalledWhateverElseIsTrue()
    {
        var card = ProviderOverview.Card("Codex", installed: false, read: true, usage: Usage(5000));
        Assert.Equal(ProviderConnection.NotInstalled, card.Connection);
        // a tool that is not on this machine has no figures to draw
        Assert.False(card.Connection.HasFigures());
    }

    [Fact]
    public void AnInstalledToolSwitchedOffReadsAsNotRead()
    {
        var card = ProviderOverview.Card("Codex", installed: true, read: false, usage: Usage(5000));
        Assert.Equal(ProviderConnection.NotRead, card.Connection);
    }

    [Fact]
    public void UnreachableOutranksIdleSoAStoppedServerIsNeverReportedAsQuiet()
    {
        // The bug this guards: a stopped Ollama has no usage, and "no usage in this range"
        // reads as a quiet day rather than as a server that is not running.
        var card = ProviderOverview.Card("Ollama", installed: true, read: true, reachable: false, usage: null);
        Assert.Equal(ProviderConnection.Unreachable, card.Connection);
    }

    [Fact]
    public void NoUsageAndNoWindowIsIdle()
    {
        var card = ProviderOverview.Card("Codex", installed: true, read: true, usage: null);
        Assert.Equal(ProviderConnection.Idle, card.Connection);
    }

    [Fact]
    public void AWindowAloneMakesACardActiveEvenWithNoTokens()
    {
        // A claude.ai user has rate limits and no transcripts, and the card still has news
        var window = new LimitWindow("Claude", "five_hour", 40, DateTimeOffset.UtcNow.AddSeconds(3600));
        var card = ProviderOverview.Card("Claude", installed: true, read: true, usage: null, windows: new[] { window });
        Assert.Equal(ProviderConnection.Active, card.Connection);
        Assert.Equal(40.0, card.Utilization);
    }

    [Fact]
    public void TheWorstWindowIsTheOneShown()
    {
        var session = new LimitWindow("Codex", "five_hour", 22, null);
        var week = new LimitWindow("Codex", "seven_day", 71, null);
        var card = ProviderOverview.Card("Codex", installed: true, read: true, usage: Usage(10),
                                         windows: new[] { session, week });
        Assert.Equal("seven_day", card.WorstWindow?.Key);
        Assert.Equal(29.0, card.RemainingPercent);
    }

    [Fact]
    public void WindowsBelongingToOtherProvidersAreIgnored()
    {
        var mine = new LimitWindow("Codex", "five_hour", 10, null);
        var theirs = new LimitWindow("Claude", "five_hour", 99, null);
        var card = ProviderOverview.Card("Codex", installed: true, read: true, usage: Usage(10),
                                         windows: new[] { mine, theirs });
        Assert.Equal(10.0, card.Utilization); // another provider's window leaked into this card
    }

    [Fact]
    public void UninformativeWindowsAreDropped()
    {
        var junk = new LimitWindow("Claude", "nimbus_quill", 0, null);
        var card = ProviderOverview.Card("Claude", installed: true, read: true, usage: null, windows: new[] { junk });
        Assert.Null(card.WorstWindow);
        Assert.Equal(ProviderConnection.Idle, card.Connection);
    }

    [Fact]
    public void AStaleReadingCarriesNoPaceBecauseARateNeedsACurrentNumber()
    {
        var resets = DateTimeOffset.UtcNow.AddSeconds(3600);
        var window = new LimitWindow("Claude", "five_hour", 50, resets);
        var pace = PaceEstimator.Pace(window);
        Assert.NotNull(pace); // fixture needs a pace for the test to mean anything
        var card = ProviderOverview.Card("Claude", installed: true, read: true, usage: null,
                                         windows: new[] { window }, paces: new[] { pace! },
                                         asOf: DateTimeOffset.UtcNow.AddSeconds(-3600));
        Assert.True(card.IsStale);
        Assert.Null(card.Pace);
    }

    [Fact]
    public void AFreshReadingKeepsItsPace()
    {
        var resets = DateTimeOffset.UtcNow.AddSeconds(3600);
        var window = new LimitWindow("Claude", "five_hour", 50, resets);
        var pace = PaceEstimator.Pace(window);
        Assert.NotNull(pace); // fixture needs a pace
        var card = ProviderOverview.Card("Claude", installed: true, read: true, usage: null,
                                         windows: new[] { window }, paces: new[] { pace! },
                                         asOf: DateTimeOffset.UtcNow);
        Assert.False(card.IsStale);
        Assert.NotNull(card.Pace);
    }

    [Fact]
    public void StaleOutranksTheStatusItWouldOtherwiseDraw()
    {
        var window = new LimitWindow("Claude", "five_hour", 96, null);
        var card = ProviderOverview.Card("Claude", installed: true, read: true, usage: null,
                                         windows: new[] { window }, asOf: DateTimeOffset.UtcNow.AddSeconds(-4000));
        // an old reading must never be drawn as a live one
        Assert.Equal(RLStatusKind.Stale, card.Status(approaching: 60, atLimit: 85).Kind);
    }

    [Fact]
    public void ALocalProviderExplainsWhyItHasNoLimitRatherThanShowingNothing()
    {
        var card = ProviderOverview.Card("Ollama", installed: true, read: true, reachable: true, usage: Usage(900));
        Assert.Equal(ProviderConnection.Active, card.Connection);
        Assert.Null(card.Utilization);
        Assert.Equal("Runs on this PC, so there is no rate limit to report", card.LimitNote);
    }

    [Fact]
    public void AHostedProviderWithNoWindowSaysSoAndPrefersTheCallersReason()
    {
        var plain = ProviderOverview.Card("Claude", installed: true, read: true, usage: Usage(900));
        Assert.Equal("No limit window reported yet", plain.LimitNote);

        var given = ProviderOverview.Card("Claude", installed: true, read: true, usage: Usage(900),
                                          limitsNote: "rate limited, retrying");
        Assert.Equal("rate limited, retrying", given.LimitNote);
    }

    [Fact]
    public void ACardWithAWindowCarriesNoLimitNote()
    {
        var window = new LimitWindow("Codex", "five_hour", 12, null);
        var card = ProviderOverview.Card("Codex", installed: true, read: true, usage: null,
                                         windows: new[] { window }, limitsNote: "should not be shown");
        Assert.Null(card.LimitNote);
    }

    [Fact]
    public void AnAbsentToolSaysNothingAboutLimitsBecauseTheCardAlreadySaysWhy()
    {
        var card = ProviderOverview.Card("Codex", installed: false, read: true, usage: null, limitsNote: "irrelevant");
        Assert.Null(card.LimitNote);
    }

    [Fact]
    public void EveryConnectionStateSaysSomethingAndHasATone()
    {
        foreach (var state in Enum.GetValues<ProviderConnection>())
        {
            Assert.False(string.IsNullOrEmpty(state.Phrase()));
            _ = state.Tone();
        }
    }
}

public sealed class OverviewWarningTests
{
    private static LimitWindow Window(string provider, string key, double pct, DateTimeOffset? resets = null) =>
        new(provider, key, pct, resets);

    [Fact]
    public void AHealthyWindowRaisesNothing()
    {
        var warnings = ProviderOverview.Warnings(new[] { Window("Claude", "five_hour", 12) },
            Array.Empty<Pace>(), approaching: 60, atLimit: 85);
        Assert.Empty(warnings); // a banner that is always lit says nothing
    }

    [Fact]
    public void CrossingTheConfiguredThresholdRaisesOne()
    {
        var warnings = ProviderOverview.Warnings(new[] { Window("Claude", "five_hour", 62) },
            Array.Empty<Pace>(), approaching: 60, atLimit: 85);
        Assert.Single(warnings);
        Assert.Equal(OverviewWarningKind.Approaching, warnings[0].Kind);
        Assert.Contains("62%", warnings[0].Text);
    }

    [Fact]
    public void TheThresholdsAreTheConfiguredOnesNotHardcodedSixtyAndEightyFive()
    {
        var warnings = ProviderOverview.Warnings(new[] { Window("Claude", "five_hour", 45) },
            Array.Empty<Pace>(), approaching: 40, atLimit: 70);
        Assert.Equal(OverviewWarningKind.Approaching, warnings.FirstOrDefault()?.Kind);

        var atLimit = ProviderOverview.Warnings(new[] { Window("Claude", "five_hour", 72) },
            Array.Empty<Pace>(), approaching: 40, atLimit: 70);
        Assert.Equal(OverviewWarningKind.AtLimit, atLimit.FirstOrDefault()?.Kind);
    }

    [Fact]
    public void WorstNewsIsReadFirst()
    {
        var windows = new[] { Window("Claude", "five_hour", 65), Window("Codex", "seven_day", 91) };
        var warnings = ProviderOverview.Warnings(windows, Array.Empty<Pace>(), approaching: 60, atLimit: 85);
        Assert.Equal(OverviewWarningKind.AtLimit, warnings[0].Kind);
        Assert.Equal("Codex", warnings[0].Provider);
    }

    [Fact]
    public void AStaleProviderRaisesNothingBecauseItIsNotEvidenceAboutNow()
    {
        var warnings = ProviderOverview.Warnings(new[] { Window("Claude", "five_hour", 99) },
            Array.Empty<Pace>(), approaching: 60, atLimit: 85, staleProviders: new HashSet<string> { "Claude" });
        Assert.Empty(warnings);
    }

    [Fact]
    public void AWindowRunningOutBeforeItResetsIsWorthSayingEvenWhileHealthy()
    {
        var resets = DateTimeOffset.UtcNow.AddSeconds(4 * 3600);
        var w = Window("Codex", "five_hour", 20, resets);
        // Burning fast enough to hit the cap well before the reset
        var pace = new Pace("Codex", "five_hour", 20, resets, 80, new PaceBasis.WindowAverage(), 0.2,
                            DateTimeOffset.UtcNow.AddSeconds(3600));
        var warnings = ProviderOverview.Warnings(new[] { w }, new[] { pace }, approaching: 60, atLimit: 85);
        Assert.Single(warnings);
        Assert.Equal(OverviewWarningKind.RunsOutEarly, warnings[0].Kind);
        Assert.Contains("before it resets", warnings[0].Text);
    }

    [Fact]
    public void UninformativeWindowsRaiseNothing()
    {
        var junk = Window("Claude", "nimbus_quill", 0);
        Assert.Empty(ProviderOverview.Warnings(new[] { junk }, Array.Empty<Pace>(), approaching: 60, atLimit: 85));
    }

    [Fact]
    public void WarningIdentityIncludesTheProviderSoTwoTracksDoNotCollide()
    {
        var windows = new[] { Window("Claude", "five_hour", 90), Window("Codex", "five_hour", 90) };
        var warnings = ProviderOverview.Warnings(windows, Array.Empty<Pace>(), approaching: 60, atLimit: 85);
        Assert.Equal(2, warnings.Select(w => w.Id).ToHashSet().Count);
    }
}
