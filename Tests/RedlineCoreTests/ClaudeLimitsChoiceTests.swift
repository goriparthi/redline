// The setup window's Start button writes `useCLIToken` from whatever the radio shows, so the
// preselection rule is load-bearing rather than cosmetic. These tests pin it.
import XCTest
@testable import RedlineCore

final class ClaudeLimitsChoiceTests: XCTestCase {
    func testATrueFirstRunOffersTheZeroCredentialRoute() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: false, useCLIToken: false,
                                                  signedIn: false),
                       .feed)
    }

    func testAnInstalledFeedIsShownWhenNothingElseIsChosen() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: true, useCLIToken: false,
                                                  signedIn: false),
                       .feed)
    }

    /// The regression this rule exists for. Installing the feed does not uninstall itself when
    /// the user later switches to the CLI token, so an installed feed used to outrank the
    /// choice they had actually made. Reopening the window showed the feed, and pressing Start
    /// wrote `useCLIToken: false`, silently reverting them to the default.
    func testAnExplicitCLITokenChoiceOutranksAnInstalledFeed() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: true, useCLIToken: true,
                                                  signedIn: false),
                       .cliToken,
                       "an installed feed must not outrank the credential the user chose")
    }

    func testAnOwnGrantOutranksAnInstalledFeed() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: true, useCLIToken: false,
                                                  signedIn: true),
                       .browser)
    }

    /// Both explicit routes on at once is a real state: the ladder prefers the feed while it is
    /// fresh and falls back to the token. The radio shows the CLI token because that is the one
    /// Start can switch off; the feed cannot be uninstalled by pressing Start.
    func testTheCLITokenWinsWhenBothCredentialRoutesAreOn() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: true, useCLIToken: true,
                                                  signedIn: true),
                       .cliToken)
    }

    func testTheCLITokenWinsOverAnOwnGrantWithNoFeed() {
        XCTAssertEqual(ClaudeLimitsChoice.current(feedInstalled: false, useCLIToken: true,
                                                  signedIn: true),
                       .cliToken)
    }

    /// Whatever the inputs, the rule never returns a choice that Start would use to turn off a
    /// credential the user had switched on.
    func testNoInputCombinationSilentlyRevokesAnExplicitChoice() {
        for feed in [false, true] {
            for cli in [false, true] {
                for signedIn in [false, true] {
                    let choice = ClaudeLimitsChoice.current(feedInstalled: feed,
                                                            useCLIToken: cli,
                                                            signedIn: signedIn)
                    // Start writes `useCLIToken: choice == .cliToken`, so a user with the CLI
                    // token on must never be shown anything else.
                    if cli {
                        XCTAssertEqual(choice, .cliToken,
                                       "feed=\(feed) cli=\(cli) signedIn=\(signedIn)")
                    }
                    // Start signs out on `.off`, so it must never be preselected for someone
                    // who has a credential at all.
                    if cli || signedIn {
                        XCTAssertNotEqual(choice, .off,
                                          "feed=\(feed) cli=\(cli) signedIn=\(signedIn)")
                    }
                }
            }
        }
    }
}
