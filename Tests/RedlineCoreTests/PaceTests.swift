// Burn rate and projection. The rules that matter are the ones that keep it quiet: no
// projection without a length, without elapsed time, or across a window that rolled over.
import XCTest
@testable import RedlineCore

final class PaceTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_760_000_000)

    private func window(_ key: String = "five_hour", utilization: Double,
                        resetsIn: TimeInterval) -> LimitWindow {
        LimitWindow(provider: "Claude", key: key, utilization: utilization,
                    resetsAt: now.addingTimeInterval(resetsIn), source: .official)
    }

    func testWindowAverageProjectsFromElapsedTime() throws {
        // Half way through a five hour window at 60% used: the cap arrives before the reset
        let w = window(utilization: 60, resetsIn: 2.5 * 3600)
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, now: now))
        XCTAssertEqual(pace.ratePerHour, 24, accuracy: 0.01)
        XCTAssertEqual(pace.elapsedFraction ?? 0, 0.5, accuracy: 0.001)
        XCTAssertTrue(pace.hitsLimitBeforeReset)
        let toLimit = try XCTUnwrap(pace.timeToLimit(now: now))
        XCTAssertEqual(toLimit / 60, 100, accuracy: 1, "40 points at 24/hour is 100 minutes")
    }

    func testOnPaceDoesNotClaimTheCap() throws {
        let w = window(utilization: 50, resetsIn: 2.5 * 3600)
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, now: now))
        XCTAssertFalse(pace.hitsLimitBeforeReset)
        XCTAssertEqual(pace.summary(now: now), "on pace")
    }

    func testMeasuredRateBeatsTheWindowAverage() throws {
        // Quiet for hours, then a burst. Averaged over the window this looks survivable;
        // the last half hour says the cap arrives well before the reset does.
        let w = window(utilization: 40, resetsIn: 2 * 3600)
        let reset = try XCTUnwrap(w.resetsAt)
        let samples = [
            LimitSample(at: now.addingTimeInterval(-1800), provider: "Claude",
                        key: "five_hour", utilization: 10, resetsAt: reset, source: .official),
        ]
        XCTAssertFalse(PaceEstimator.pace(for: w, now: now)?.hitsLimitBeforeReset ?? true,
                       "the window average alone would not raise this")
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, samples: samples, now: now))
        XCTAssertEqual(pace.ratePerHour, 60, accuracy: 0.01)
        if case .measured(_, let over) = pace.basis {
            XCTAssertEqual(over, 1800, accuracy: 1)
        } else {
            XCTFail("expected a measured basis when readings are available")
        }
        XCTAssertTrue(pace.hitsLimitBeforeReset)
    }

    func testReadingsOlderThanTheLookbackAreNotTheCurrentRate() throws {
        // A five hour window looks at its recent stretch; an hour-old reading describes
        // what was happening then, not now.
        let w = window(utilization: 40, resetsIn: 3600)
        let reset = try XCTUnwrap(w.resetsAt)
        let samples = [
            LimitSample(at: now.addingTimeInterval(-3600), provider: "Claude",
                        key: "five_hour", utilization: 10, resetsAt: reset, source: .official),
        ]
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, samples: samples, now: now))
        XCTAssertEqual(pace.basis, .windowAverage)
    }

    func testSamplesFromThePreviousWindowAreIgnored() throws {
        let w = window(utilization: 12, resetsIn: 3600)
        // Same key, different reset time: this belongs to the window that already rolled over
        let stale = [
            LimitSample(at: now.addingTimeInterval(-3600), provider: "Claude",
                        key: "five_hour", utilization: 95,
                        resetsAt: now.addingTimeInterval(-1800), source: .official),
        ]
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, samples: stale, now: now))
        XCTAssertEqual(pace.basis, .windowAverage,
                       "differencing across a rollover would invent a negative rate")
    }

    func testTwoReadingsTooCloseTogetherAreNotARate() throws {
        let w = window(utilization: 40, resetsIn: 3600)
        let reset = try XCTUnwrap(w.resetsAt)
        let samples = [
            LimitSample(at: now.addingTimeInterval(-60), provider: "Claude", key: "five_hour",
                        utilization: 39, resetsAt: reset, source: .official),
        ]
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, samples: samples, now: now))
        XCTAssertEqual(pace.basis, .windowAverage)
    }

    func testAWeeklyWindowNeedsHoursOfReadingsNotMinutes() throws {
        // A busy quarter of an hour is not a week's rate. This shipped once as "23h to
        // limit" on a window 5% used with six days left, which is the sort of number that
        // teaches people to ignore the app.
        let w = window("seven_day", utilization: 5, resetsIn: 6.5 * 86400)
        let reset = try XCTUnwrap(w.resetsAt)
        let recent = [
            LimitSample(at: now.addingTimeInterval(-900), provider: "Claude", key: "seven_day",
                        utilization: 4, resetsAt: reset, source: .official),
        ]
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, samples: recent, now: now))
        XCTAssertEqual(pace.basis, .windowAverage)
        XCTAssertFalse(pace.hitsLimitBeforeReset)

        // Seven hours of readings is long enough to mean something about a week
        let longer = [
            LimitSample(at: now.addingTimeInterval(-8 * 3600), provider: "Claude",
                        key: "seven_day", utilization: 1, resetsAt: reset, source: .official),
        ]
        let measured = try XCTUnwrap(PaceEstimator.pace(for: w, samples: longer, now: now))
        if case .measured = measured.basis {} else { XCTFail("expected a measured basis") }
    }

    func testUnknownWindowLengthSaysNothing() {
        let w = LimitWindow(provider: "Claude", key: "nimbus_quill", utilization: 50,
                            resetsAt: now.addingTimeInterval(3600))
        XCTAssertNil(PaceEstimator.pace(for: w, now: now))
    }

    func testAWindowWithNoUsageYetSaysNothing() {
        let w = window(utilization: 0, resetsIn: 4 * 3600)
        XCTAssertNil(PaceEstimator.pace(for: w, now: now))
    }

    func testWeeklyWindowLengthIsRecognised() throws {
        let w = window("seven_day_opus", utilization: 50, resetsIn: 3.5 * 86400)
        let pace = try XCTUnwrap(PaceEstimator.pace(for: w, now: now))
        XCTAssertEqual(pace.elapsedFraction ?? 0, 0.5, accuracy: 0.001)
    }

    func testShortFormatting() {
        XCTAssertEqual(Pace.short(40 * 60), "40m")
        XCTAssertEqual(Pace.short(2 * 3600 + 10 * 60), "2h 10m")
        XCTAssertEqual(Pace.short(3 * 86400 + 4 * 3600), "3d 4h")
        XCTAssertEqual(Pace.short(20), "under a minute")
    }

    func testWorstFirstOrdering() {
        let soon = window(utilization: 90, resetsIn: 3600)
        let later = window("seven_day", utilization: 20, resetsIn: 3 * 86400)
        let paces = PaceEstimator.paces(for: [later, soon], now: now)
        XCTAssertEqual(paces.first?.key, "five_hour")
    }
}
