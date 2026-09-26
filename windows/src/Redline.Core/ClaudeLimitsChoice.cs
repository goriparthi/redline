// Where the Claude rate-limit percentages come from, if anywhere. The rule is load-bearing:
// the setup window's Start writes useCLIToken from whatever the radio shows.
namespace Redline.Core;

public enum ClaudeLimitsChoice
{
    /// Percentages stay hidden; everything else still works.
    Off,
    /// The statusline usage feed: no credentials, updates while Claude Code runs.
    Feed,
    /// Read (never refresh) the Claude Code CLI's own token.
    CliToken,
    /// RedLine's own OAuth sign-in, live between sessions and for claude.ai users.
    Browser,
}

/// Swift's static on the enum; C# enums carry no members, so the rule lives beside it.
public static class ClaudeLimitsChoiceRule
{
    /// An explicit credential decision outranks an installed feed, which never uninstalls itself;
    /// showing Feed to a CLI-token user made Start switch their choice off.
    public static ClaudeLimitsChoice Current(bool feedInstalled, bool useCLIToken, bool signedIn)
    {
        if (useCLIToken) return ClaudeLimitsChoice.CliToken;
        if (signedIn) return ClaudeLimitsChoice.Browser;
        if (feedInstalled) return ClaudeLimitsChoice.Feed;
        // A true first run has nothing set up, so the zero-credential route is the default
        return ClaudeLimitsChoice.Feed;
    }
}
