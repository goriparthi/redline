// Which state a provider card resolves to, and what it says when a figure is missing.
import XCTest
@testable import RedlineCore

final class ProviderCardStateTests: XCTestCase {
    private func usage(io: Int, cost: Double = 1) -> ProviderUsage {
        var u = ProviderUsage()
        u.io = io
        u.cost = cost
        return u
    }

    func testAMissingToolIsNotInstalledWhateverElseIsTrue() {
        let card = ProviderOverview.card(provider: "Codex", installed: false, read: true,
                                        usage: usage(io: 5000))
        XCTAssertEqual(card.connection, .notInstalled)
        XCTAssertFalse(card.connection.hasFigures,
                       "a tool that is not on this Mac has no figures to draw")
    }

    func testAnInstalledToolSwitchedOffReadsAsNotRead() {
        let card = ProviderOverview.card(provider: "Codex", installed: true, read: false,
                                        usage: usage(io: 5000))
        XCTAssertEqual(card.connection, .notRead)
    }

    func testUnreachableOutranksIdleSoAStoppedServerIsNeverReportedAsQuiet() {
        // The bug this guards: a stopped Ollama has no usage, and "no usage in this range"
        // reads as a quiet day rather than as a server that is not running.
        let card = ProviderOverview.card(provider: "Ollama", installed: true, read: true,
                                        reachable: false, usage: nil)
        XCTAssertEqual(card.connection, .unreachable)
    }

    func testNoUsageAndNoWindowIsIdle() {
        let card = ProviderOverview.card(provider: "Codex", installed: true, read: true,
                                        usage: nil)
        XCTAssertEqual(card.connection, .idle)
    }

    func testAWindowAloneMakesACardActiveEvenWithNoTokens() {
        // A claude.ai user has rate limits and no transcripts, and the card still has news
        let window = LimitWindow(provider: "Claude", key: "five_hour", utilization: 40,
                                 resetsAt: Date().addingTimeInterval(3600))
        let card = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                        usage: nil, windows: [window])
        XCTAssertEqual(card.connection, .active)
        XCTAssertEqual(card.utilization, 40)
    }

    func testTheWorstWindowIsTheOneShown() {
        let session = LimitWindow(provider: "Codex", key: "five_hour", utilization: 22,
                                 resetsAt: nil)
        let week = LimitWindow(provider: "Codex", key: "seven_day", utilization: 71,
                               resetsAt: nil)
        let card = ProviderOverview.card(provider: "Codex", installed: true, read: true,
                                        usage: usage(io: 10), windows: [session, week])
        XCTAssertEqual(card.worstWindow?.key, "seven_day")
        XCTAssertEqual(card.remainingPercent, 29)
    }

    func testWindowsBelongingToOtherProvidersAreIgnored() {
        let mine = LimitWindow(provider: "Codex", key: "five_hour", utilization: 10,
                               resetsAt: nil)
        let theirs = LimitWindow(provider: "Claude", key: "five_hour", utilization: 99,
                                 resetsAt: nil)
        let card = ProviderOverview.card(provider: "Codex", installed: true, read: true,
                                        usage: usage(io: 10), windows: [mine, theirs])
        XCTAssertEqual(card.utilization, 10, "another provider's window leaked into this card")
    }

    func testUninformativeWindowsAreDropped() {
        let junk = LimitWindow(provider: "Claude", key: "nimbus_quill", utilization: 0,
                               resetsAt: nil)
        let card = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                        usage: nil, windows: [junk])
        XCTAssertNil(card.worstWindow)
        XCTAssertEqual(card.connection, .idle)
    }

    func testAStaleReadingCarriesNoPaceBecauseARateNeedsACurrentNumber() {
        let resets = Date().addingTimeInterval(3600)
        let window = LimitWindow(provider: "Claude", key: "five_hour", utilization: 50,
                                 resetsAt: resets)
        let pace = PaceEstimator.pace(for: window)
        XCTAssertNotNil(pace, "fixture needs a pace for the test to mean anything")
        let card = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                        usage: nil, windows: [window],
                                        paces: [pace].compactMap { $0 },
                                        asOf: Date().addingTimeInterval(-3600))
        XCTAssertTrue(card.isStale)
        XCTAssertNil(card.pace)
    }

    func testAFreshReadingKeepsItsPace() {
        let resets = Date().addingTimeInterval(3600)
        let window = LimitWindow(provider: "Claude", key: "five_hour", utilization: 50,
                                 resetsAt: resets)
        guard let pace = PaceEstimator.pace(for: window) else {
            return XCTFail("fixture needs a pace")
        }
        let card = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                        usage: nil, windows: [window], paces: [pace],
                                        asOf: Date())
        XCTAssertFalse(card.isStale)
        XCTAssertNotNil(card.pace)
    }

    func testStaleOutranksTheStatusItWouldOtherwiseDraw() {
        let window = LimitWindow(provider: "Claude", key: "five_hour", utilization: 96,
                                 resetsAt: nil)
        let card = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                        usage: nil, windows: [window],
                                        asOf: Date().addingTimeInterval(-4000))
        XCTAssertEqual(card.status(approaching: 60, atLimit: 85).kind, .stale,
                       "an old reading must never be drawn as a live one")
    }

    func testALocalProviderExplainsWhyItHasNoLimitRatherThanShowingNothing() {
        let card = ProviderOverview.card(provider: "Ollama", installed: true, read: true,
                                        reachable: true, usage: usage(io: 900))
        XCTAssertEqual(card.connection, .active)
        XCTAssertNil(card.utilization)
        XCTAssertEqual(card.limitNote,
                       "Runs on this Mac, so there is no rate limit to report")
    }

    func testAHostedProviderWithNoWindowSaysSoAndPrefersTheCallersReason() {
        let plain = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                         usage: usage(io: 900))
        XCTAssertEqual(plain.limitNote, "No limit window reported yet")

        let given = ProviderOverview.card(provider: "Claude", installed: true, read: true,
                                          usage: usage(io: 900),
                                          limitsNote: "rate limited, retrying")
        XCTAssertEqual(given.limitNote, "rate limited, retrying")
    }

    func testACardWithAWindowCarriesNoLimitNote() {
        let window = LimitWindow(provider: "Codex", key: "five_hour", utilization: 12,
                                 resetsAt: nil)
        let card = ProviderOverview.card(provider: "Codex", installed: true, read: true,
                                        usage: nil, windows: [window],
                                        limitsNote: "should not be shown")
        XCTAssertNil(card.limitNote)
    }

    func testAnAbsentToolSaysNothingAboutLimitsBecauseTheCardAlreadySaysWhy() {
        let card = ProviderOverview.card(provider: "Codex", installed: false, read: true,
                                        usage: nil, limitsNote: "irrelevant")
        XCTAssertNil(card.limitNote)
    }

    func testEveryConnectionStateSaysSomethingAndHasATone() {
        for state in [ProviderConnection.notInstalled, .notRead, .unreachable, .idle, .active] {
            XCTAssertFalse(state.phrase.isEmpty)
            _ = state.tone
        }
    }
}

final class OverviewWarningTests: XCTestCase {
    private func window(_ provider: String, _ key: String, _ pct: Double,
                        resets: Date? = nil) -> LimitWindow {
        LimitWindow(provider: provider, key: key, utilization: pct, resetsAt: resets)
    }

    func testAHealthyWindowRaisesNothing() {
        let warnings = ProviderOverview.warnings(
            windows: [window("Claude", "five_hour", 12)], paces: [],
            approaching: 60, atLimit: 85)
        XCTAssertTrue(warnings.isEmpty, "a banner that is always lit says nothing")
    }

    func testCrossingTheConfiguredThresholdRaisesOne() {
        let warnings = ProviderOverview.warnings(
            windows: [window("Claude", "five_hour", 62)], paces: [],
            approaching: 60, atLimit: 85)
        XCTAssertEqual(warnings.count, 1)
        XCTAssertEqual(warnings.first?.kind, .approaching)
        XCTAssertTrue(warnings.first?.text.contains("62%") ?? false)
    }

    func testTheThresholdsAreTheConfiguredOnesNotHardcodedSixtyAndEightyFive() {
        let warnings = ProviderOverview.warnings(
            windows: [window("Claude", "five_hour", 45)], paces: [],
            approaching: 40, atLimit: 70)
        XCTAssertEqual(warnings.first?.kind, .approaching)

        let atLimit = ProviderOverview.warnings(
            windows: [window("Claude", "five_hour", 72)], paces: [],
            approaching: 40, atLimit: 70)
        XCTAssertEqual(atLimit.first?.kind, .atLimit)
    }

    func testWorstNewsIsReadFirst() {
        let windows = [window("Claude", "five_hour", 65), window("Codex", "seven_day", 91)]
        let warnings = ProviderOverview.warnings(windows: windows, paces: [],
                                                 approaching: 60, atLimit: 85)
        XCTAssertEqual(warnings.first?.kind, .atLimit)
        XCTAssertEqual(warnings.first?.provider, "Codex")
    }

    func testAStaleProviderRaisesNothingBecauseItIsNotEvidenceAboutNow() {
        let warnings = ProviderOverview.warnings(
            windows: [window("Claude", "five_hour", 99)], paces: [],
            approaching: 60, atLimit: 85, staleProviders: ["Claude"])
        XCTAssertTrue(warnings.isEmpty)
    }

    func testAWindowRunningOutBeforeItResetsIsWorthSayingEvenWhileHealthy() {
        let resets = Date().addingTimeInterval(4 * 3600)
        let w = window("Codex", "five_hour", 20, resets: resets)
        // Burning fast enough to hit the cap well before the reset
        let pace = Pace(provider: "Codex", key: "five_hour", utilization: 20,
                        resetsAt: resets, ratePerHour: 80, basis: .windowAverage,
                        elapsedFraction: 0.2,
                        exhaustsAt: Date().addingTimeInterval(3600))
        let warnings = ProviderOverview.warnings(windows: [w], paces: [pace],
                                                 approaching: 60, atLimit: 85)
        XCTAssertEqual(warnings.count, 1)
        XCTAssertEqual(warnings.first?.kind, .runsOutEarly)
        XCTAssertTrue(warnings.first?.text.contains("before it resets") ?? false)
    }

    func testUninformativeWindowsRaiseNothing() {
        let junk = window("Claude", "nimbus_quill", 0)
        XCTAssertTrue(ProviderOverview.warnings(windows: [junk], paces: [],
                                                approaching: 60, atLimit: 85).isEmpty)
    }

    func testWarningIdentityIncludesTheProviderSoTwoTracksDoNotCollide() {
        let windows = [window("Claude", "five_hour", 90), window("Codex", "five_hour", 90)]
        let warnings = ProviderOverview.warnings(windows: windows, paces: [],
                                                 approaching: 60, atLimit: 85)
        XCTAssertEqual(Set(warnings.map(\.id)).count, 2)
    }
}
