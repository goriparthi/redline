// The setup window's Start writes useCLIToken from whatever the radio shows, so the
// preselection rule is load-bearing rather than cosmetic. These tests pin it.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class ClaudeLimitsChoiceTests
{
    private static ClaudeLimitsChoice Current(bool feed, bool cli, bool signedIn) =>
        ClaudeLimitsChoiceRule.Current(feedInstalled: feed, useCLIToken: cli, signedIn: signedIn);

    [Fact]
    public void ATrueFirstRunOffersTheZeroCredentialRoute()
    {
        Assert.Equal(ClaudeLimitsChoice.Feed, Current(false, false, false));
    }

    [Fact]
    public void AnInstalledFeedIsShownWhenNothingElseIsChosen()
    {
        Assert.Equal(ClaudeLimitsChoice.Feed, Current(true, false, false));
    }

    /// The regression this rule exists for: an installed feed used to outrank the CLI token
    /// the user had chosen, and pressing Start then wrote useCLIToken false.
    [Fact]
    public void AnExplicitCLITokenChoiceOutranksAnInstalledFeed()
    {
        Assert.Equal(ClaudeLimitsChoice.CliToken, Current(true, true, false));
    }

    [Fact]
    public void AnOwnGrantOutranksAnInstalledFeed()
    {
        Assert.Equal(ClaudeLimitsChoice.Browser, Current(true, false, true));
    }

    /// Both credential routes on at once is real; the radio shows the one Start can switch off.
    [Fact]
    public void TheCLITokenWinsWhenBothCredentialRoutesAreOn()
    {
        Assert.Equal(ClaudeLimitsChoice.CliToken, Current(true, true, true));
    }

    [Fact]
    public void TheCLITokenWinsOverAnOwnGrantWithNoFeed()
    {
        Assert.Equal(ClaudeLimitsChoice.CliToken, Current(false, true, true));
    }

    /// Whatever the inputs, the rule never returns a choice Start would use to turn off a
    /// credential the user had switched on.
    [Fact]
    public void NoInputCombinationSilentlyRevokesAnExplicitChoice()
    {
        foreach (var feed in new[] { false, true })
        foreach (var cli in new[] { false, true })
        foreach (var signedIn in new[] { false, true })
        {
            var choice = Current(feed, cli, signedIn);
            // Start writes useCLIToken from the choice, so a CLI-token user sees nothing else
            if (cli) Assert.Equal(ClaudeLimitsChoice.CliToken, choice);
            // Start signs out on Off, so it is never preselected for someone with a credential
            if (cli || signedIn) Assert.NotEqual(ClaudeLimitsChoice.Off, choice);
        }
    }
}
