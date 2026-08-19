// When RedLine is allowed to interrupt. Most of these assert silence, which is the harder
// half: a monitor that cries wolf gets its notifications switched off and then it is a
// monitor nobody hears.
import XCTest
@testable import RedlineCore

final class AlertingTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_760_000_000)
    private var config: Config = {
        var c = Config()
        c.alerts = true
        return c
    }()

    private func window(_ utilization: Double, resetsIn: TimeInterval = 3600,
                        key: String = "five_hour") -> LimitWindow {
        LimitWindow(provider: "Claude", key: key, utilization: utilization,
                    resetsAt: now.addingTimeInterval(resetsIn), source: .official)
    }

    func testFiresOncePerThresholdPerWindow() {
        var state = AlertState()
        var events = Alerting.evaluate(windows: [window(88)], config: config, now: now,
                                       state: &state)
        XCTAssertEqual(events.count, 2, "60 and 85 are both newly crossed")
        events = Alerting.evaluate(windows: [window(89)], config: config,
                                   now: now.addingTimeInterval(300), state: &state)
        XCTAssertTrue(events.isEmpty, "still past the same thresholds, so still not news")
    }

    func testStaleReadingsNeverFire() {
        var state = AlertState()
        let events = Alerting.evaluate(windows: [window(99)], config: config, now: now,
                                       isStale: { _ in true }, state: &state)
        XCTAssertTrue(events.isEmpty)
        XCTAssertEqual(state.windows.count, 1,
                       "the reading is still recorded, so the next fresh one is not a reset")
    }

    func testAlertsOffMeansSilence() {
        var off = Config()
        off.alerts = false
        var state = AlertState()
        XCTAssertTrue(Alerting.evaluate(windows: [window(99)], config: off, now: now,
                                        state: &state).isEmpty)
    }

    func testLimitReachedIsItsOwnEvent() {
        var state = AlertState()
        let events = Alerting.evaluate(windows: [window(100)], config: config, now: now,
                                       state: &state)
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events.first?.kind, .limitReached)
    }

    func testResetIsAnnouncedOnlyForAWindowThatWasBeingUsed() {
        var state = AlertState()
        _ = Alerting.evaluate(windows: [window(70)], config: config, now: now, state: &state)
        // Same window key, new reset time, back down to nothing
        let rolled = LimitWindow(provider: "Claude", key: "five_hour", utilization: 1,
                                 resetsAt: now.addingTimeInterval(18000), source: .official)
        let events = Alerting.evaluate(windows: [rolled], config: config,
                                       now: now.addingTimeInterval(3600), state: &state)
        XCTAssertTrue(events.contains { $0.kind == .reset })
    }

    func testResetOfAnUntouchedWindowIsNotNews() {
        var state = AlertState()
        _ = Alerting.evaluate(windows: [window(3)], config: config, now: now, state: &state)
        let rolled = LimitWindow(provider: "Claude", key: "five_hour", utilization: 0,
                                 resetsAt: now.addingTimeInterval(18000), source: .official)
        let events = Alerting.evaluate(windows: [rolled], config: config,
                                       now: now.addingTimeInterval(3600), state: &state)
        XCTAssertFalse(events.contains { $0.kind == .reset })
    }

    func testNewWindowInstanceRearmsThresholds() {
        var state = AlertState()
        _ = Alerting.evaluate(windows: [window(90)], config: config, now: now, state: &state)
        let next = LimitWindow(provider: "Claude", key: "five_hour", utilization: 90,
                               resetsAt: now.addingTimeInterval(18000), source: .official)
        let events = Alerting.evaluate(windows: [next], config: config,
                                       now: now.addingTimeInterval(7200), state: &state)
        XCTAssertTrue(events.contains { $0.kind == .threshold(85) },
                      "a fresh window that is already deep in is worth saying again")
    }

    func testProjectionFiresOnlyInsideTheHorizon() {
        var state = AlertState()
        let w = window(50, resetsIn: 4 * 3600)
        // Racing: half the window gone in the first hour, so the cap lands well before reset
        let samples = [
            LimitSample(at: now.addingTimeInterval(-3600), provider: "Claude",
                        key: "five_hour", utilization: 10, resetsAt: w.resetsAt,
                        source: .official),
        ]
        let paces = PaceEstimator.paces(for: [w], samples: samples, now: now)
        XCTAssertTrue(paces.first?.hitsLimitBeforeReset ?? false)
        let events = Alerting.evaluate(windows: [w], paces: paces, config: config, now: now,
                                       state: &state)
        XCTAssertTrue(events.contains { $0.kind == .projection })
        // And only once
        let again = Alerting.evaluate(windows: [w], paces: paces, config: config,
                                      now: now.addingTimeInterval(60), state: &state)
        XCTAssertFalse(again.contains { $0.kind == .projection })
    }

    func testThresholdsFollowTheConfiguredColours() {
        var custom = Config()
        custom.limitYellowPct = 50
        custom.limitRedPct = 75
        XCTAssertEqual(Alerting.thresholds(for: custom), [50, 75, 95])
    }

    func testStateFileRoundTrips() throws {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-alerts-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        var state = AlertState()
        _ = Alerting.evaluate(windows: [window(88)], config: config, now: now, state: &state)
        XCTAssertTrue(AlertStore.save(state, to: url))
        XCTAssertEqual(AlertStore.load(from: url), state)
    }
}
