// Where the Claude rate-limit percentages come from, if anywhere.
//
// The enum and the rule that picks it live here rather than in the setup window, because the
// rule is load-bearing: the setup window's Start button writes `useCLIToken` from whatever the
// radio shows, so a preselection that under-reports the user's choice silently reverts it.
import Foundation

public enum ClaudeLimitsChoice: Equatable, Sendable {
    /// Percentages stay hidden; everything else still works.
    case off
    /// The statusline usage feed: no credentials, updates while Claude Code runs.
    case feed
    /// Read (never refresh) the Claude Code CLI's Keychain token.
    case cliToken
    /// RedLine's own OAuth sign-in, live between sessions and for claude.ai users.
    case browser

    /// Which route the setup window should show as current.
    ///
    /// An explicit credential decision outranks an installed artifact. Installing the feed
    /// does not uninstall itself when the user later switches to the CLI token or signs in, so
    /// the feed being on disk is not evidence of what the user wants now, while `useCLIToken`
    /// and a stored grant are exactly that.
    ///
    /// Getting this order wrong is not cosmetic. Start writes `useCLIToken: choice ==
    /// .cliToken`, so showing `.feed` to someone who had chosen the CLI token turned their
    /// choice off the moment they pressed Start.
    public static func current(feedInstalled: Bool,
                               useCLIToken: Bool,
                               signedIn: Bool) -> ClaudeLimitsChoice {
        if useCLIToken { return .cliToken }
        if signedIn { return .browser }
        if feedInstalled { return .feed }
        // A true first run has nothing set up, so the recommended zero-credential route is
        // the default.
        return .feed
    }
}
