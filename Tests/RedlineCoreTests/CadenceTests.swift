// Cues about how the work is spread out. The rules worth pinning are the ones that keep it
// from becoming a nag: once per stretch, once per night, and nothing at all when the setting
// is off or when the data is old enough that the claim would be about the past.
import XCTest
@testable import RedlineCore

final class CadenceTests: XCTestCase {
    private var calendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    private func entry(_ ts: Date) -> Entry {
        Entry(provider: "Claude", key: UUID().uuidString, ts: ts, model: "claude-sonnet-5",
              input: 100, output: 10, cacheRead: 0, cache5m: 0, cache1h: 0)
    }

    /// 2026-08-18 09:00:00 UTC
    private let base = Date(timeIntervalSince1970: 1_787_043_600)

    private func run(minutes: [Double]) -> [Entry] {
        minutes.map { entry(base.addingTimeInterval($0 * 60)) }
    }

    // MARK: - Shapes

    func testAGapLongerThanTheBreakSplitsTheStretch() {
        let entries = run(minutes: [0, 5, 10, 40, 45])
        let stretches = Cadence.stretches(entries)
        XCTAssertEqual(stretches.count, 2)
        XCTAssertEqual(stretches[0].length, 600)
        XCTAssertEqual(stretches[1].length, 300)
    }

    func testAShortPauseDoesNotSplitAStretch() {
        // A slow turn or a build is not a break.
        let entries = run(minutes: [0, 12, 24])
        XCTAssertEqual(Cadence.stretches(entries).count, 1)
    }

    func testStretchLengthIsMeasuredToTheLastActivityNotToNow() {
        let entries = run(minutes: [0, 10, 20, 30])
        let stretch = Cadence.stretches(entries).last
        XCTAssertEqual(stretch?.length, 1800,
                       "a counter that climbs while nothing happens is not a measurement")
    }

    func testCurrentStretchClosesOnceTheGapHasPassed() {
        let entries = run(minutes: [0, 10, 20, 30])
        XCTAssertNotNil(Cadence.current(entries, now: base.addingTimeInterval(35 * 60)))
        XCTAssertNil(Cadence.current(entries, now: base.addingTimeInterval(60 * 60)))
    }

    func testStreakCountsBackFromTodayAndStopsAtTheFirstGap() {
        var entries: [Entry] = []
        for day in [0, 1, 2, 4, 5] {
            entries.append(entry(base.addingTimeInterval(-Double(day) * 86400)))
        }
        XCTAssertEqual(Cadence.streak(entries, endingOn: base, calendar: calendar), 3)
    }

    func testStreakIsZeroWhenTodayHasNothing() {
        let entries = [entry(base.addingTimeInterval(-2 * 86400))]
        XCTAssertEqual(Cadence.streak(entries, endingOn: base, calendar: calendar), 0)
    }

    func testHourHistogramBucketsByLocalHour() {
        let entries = run(minutes: [0, 1, 120])
        let hours = Cadence.byHourOfDay(entries, calendar: calendar)
        XCTAssertEqual(hours[9], 220)
        XCTAssertEqual(hours[11], 110)
        XCTAssertEqual(hours.reduce(0, +), 330)
    }

    func testNightRollsAtFiveNotAtMidnight() {
        // 01:30 belongs to the evening that ran into it.
        let lateEvening = base.addingTimeInterval(14 * 3600)      // 23:00
        let afterMidnight = base.addingTimeInterval(16.5 * 3600)  // 01:30 next day
        XCTAssertEqual(Cadence.nightKey(for: lateEvening, calendar: calendar),
                       Cadence.nightKey(for: afterMidnight, calendar: calendar))
    }

    // MARK: - Cues

    private func config() -> Config {
        var c = Config()
        c.mindfulCues = true
        c.stretchMinutes = 90
        c.lateHour = 23
        c.streakDays = 7
        return c
    }

    func testCuesAreSilentWhenTheSettingIsOff() {
        var state = CadenceState()
        var off = config()
        off.mindfulCues = false
        let entries = run(minutes: Array(stride(from: 0, through: 240, by: 10)).map(Double.init))
        let cues = CadenceRules.evaluate(entries: entries, config: off,
                                         now: base.addingTimeInterval(240 * 60),
                                         calendar: calendar, state: &state)
        XCTAssertTrue(cues.isEmpty)
    }

    func testAStretchIsAnnouncedOnceAndThenAtTheNextMultiple() {
        var state = CadenceState()
        let minutes = Array(stride(from: 0.0, through: 200.0, by: 10.0))
        // At 100 minutes: one threshold reached
        let atFirst = CadenceRules.evaluate(
            entries: run(minutes: minutes.filter { $0 <= 100 }), config: config(),
            now: base.addingTimeInterval(105 * 60), calendar: calendar, state: &state)
        XCTAssertEqual(atFirst.count, 1)
        if case .stretch = atFirst[0].kind {} else { XCTFail("expected a stretch cue") }

        // Ten minutes later, still the same run: nothing new to say
        let again = CadenceRules.evaluate(
            entries: run(minutes: minutes.filter { $0 <= 110 }), config: config(),
            now: base.addingTimeInterval(115 * 60), calendar: calendar, state: &state)
        XCTAssertTrue(again.isEmpty, "a stretch says something once per threshold, not per poll")

        // Past three hours: the second multiple
        let atSecond = CadenceRules.evaluate(
            entries: run(minutes: minutes), config: config(),
            now: base.addingTimeInterval(205 * 60), calendar: calendar, state: &state)
        XCTAssertEqual(atSecond.count, 1)
    }

    func testANewStretchRearmsTheCue() {
        var state = CadenceState()
        let first = Array(stride(from: 0.0, through: 100.0, by: 10.0))
        _ = CadenceRules.evaluate(entries: run(minutes: first), config: config(),
                                  now: base.addingTimeInterval(105 * 60),
                                  calendar: calendar, state: &state)
        // A real break, then a second long run
        let second = first + Array(stride(from: 200.0, through: 300.0, by: 10.0))
        let cues = CadenceRules.evaluate(entries: run(minutes: second), config: config(),
                                         now: base.addingTimeInterval(305 * 60),
                                         calendar: calendar, state: &state)
        XCTAssertEqual(cues.count, 1)
    }

    func testLateCueFiresOncePerNight() {
        var state = CadenceState()
        var cfg = config()
        cfg.stretchMinutes = 600  // keep the stretch rule out of this test
        let lateOne = base.addingTimeInterval(14 * 3600 + 600)   // 23:10
        let lateTwo = base.addingTimeInterval(15 * 3600)         // 00:00, same night
        var entries = [entry(lateOne)]
        let first = CadenceRules.evaluate(entries: entries, config: cfg,
                                          now: lateOne.addingTimeInterval(60),
                                          calendar: calendar, state: &state)
        XCTAssertEqual(first.count, 1)
        if case .late = first[0].kind {} else { XCTFail("expected a late cue") }

        entries.append(entry(lateTwo))
        let second = CadenceRules.evaluate(entries: entries, config: cfg,
                                           now: lateTwo.addingTimeInterval(60),
                                           calendar: calendar, state: &state)
        XCTAssertTrue(second.isEmpty, "the same night is one cue")
    }

    func testLateCueNeedsRecentActivityNotAMemoryOfIt() {
        var state = CadenceState()
        var cfg = config()
        cfg.stretchMinutes = 600
        let lateLastNight = base.addingTimeInterval(14 * 3600)
        let cues = CadenceRules.evaluate(entries: [entry(lateLastNight)], config: cfg,
                                         now: lateLastNight.addingTimeInterval(4 * 3600),
                                         calendar: calendar, state: &state)
        XCTAssertTrue(cues.isEmpty, "an old reading is not news about tonight")
    }

    func testStreakIsAnnouncedAtItsThresholdAndNotEveryDayAfter() {
        var cfg = config()
        cfg.stretchMinutes = 600
        var state = CadenceState()
        func entries(days: Int) -> [Entry] {
            (0..<days).map { entry(base.addingTimeInterval(-Double($0) * 86400)) }
        }
        let atSeven = CadenceRules.evaluate(entries: entries(days: 7), config: cfg, now: base,
                                            calendar: calendar, state: &state)
        XCTAssertEqual(atSeven.count, 1)
        if case .streak(let days) = atSeven[0].kind { XCTAssertEqual(days, 7) }
        else { XCTFail("expected a streak cue") }

        let atEight = CadenceRules.evaluate(entries: entries(days: 8), config: cfg, now: base,
                                            calendar: calendar, state: &state)
        XCTAssertTrue(atEight.isEmpty, "an eighth day is not a second announcement")

        let atFourteen = CadenceRules.evaluate(entries: entries(days: 14), config: cfg,
                                               now: base, calendar: calendar, state: &state)
        XCTAssertEqual(atFourteen.count, 1, "the next multiple is worth saying")
    }

    func testNoEntriesMeansNoCues() {
        var state = CadenceState()
        XCTAssertTrue(CadenceRules.evaluate(entries: [], config: config(), now: base,
                                            calendar: calendar, state: &state).isEmpty)
    }

    func testStateRoundTripsThroughItsFile() {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("cadence-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        var state = CadenceState()
        state.stretchID = "stretch|123"
        state.stretchFired = 2
        state.lastLateNight = "2026-08-18"
        state.streakFired = 1
        XCTAssertTrue(CadenceStore.save(state, to: url))
        XCTAssertEqual(CadenceStore.load(from: url), state)
    }
}
