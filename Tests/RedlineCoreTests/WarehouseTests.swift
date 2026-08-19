// The history file. Its whole reason to exist is that transcripts get pruned, so the merge
// rule that survives a shrinking scan is the thing worth testing hardest.
import XCTest
@testable import RedlineCore

final class WarehouseTests: XCTestCase {
    private var dir: URL!
    private var warehouse: Warehouse!
    private let config = Config()

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-warehouse-\(UUID().uuidString)")
        warehouse = Warehouse(root: dir)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func entry(_ ts: Date, model: String = "claude-sonnet-5",
                       input: Int = 1000, output: Int = 100) -> Entry {
        Entry(provider: "Claude", key: UUID().uuidString, ts: ts, model: model,
              input: input, output: output, cacheRead: 0, cache5m: 0, cache1h: 0)
    }

    func testRollupGroupsByUTCDay() {
        // 23:30 UTC and 00:30 UTC the next day are different days regardless of where the
        // machine is, which is the point of fixing the basis.
        let a = Date(timeIntervalSince1970: 1_760_000_000)
        let b = a.addingTimeInterval(86400)
        let records = Warehouse.rollup(entries: [entry(a), entry(b)], config: config)
        XCTAssertEqual(records.count, 2)
        XCTAssertNotEqual(records[0].day, records[1].day)
        XCTAssertEqual(records[0].input, 1000)
    }

    func testMergeKeepsTheFullestReadingOfADay() {
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.merge(entries: [entry(day, input: 5000)], config: config)
        // A later scan sees less of the same day, because Claude Code pruned a transcript
        warehouse.merge(entries: [entry(day, input: 10)], config: config)
        let stored = warehouse.load()
        XCTAssertEqual(stored.count, 1)
        XCTAssertEqual(stored[0].input, 5000, "a shrinking scan must not erase history")
    }

    func testMergeAcceptsAGrowingDay() {
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.merge(entries: [entry(day, input: 100)], config: config)
        warehouse.merge(entries: [entry(day, input: 900)], config: config)
        XCTAssertEqual(warehouse.load().first?.input, 900)
    }

    func testUnpricedModelIsCountedButNotCosted() {
        let records = Warehouse.rollup(
            entries: [entry(Date(), model: "some-unlisted-model")], config: config)
        XCTAssertEqual(records.first?.io, 1100)
        XCTAssertEqual(records.first?.cost, 0)
        XCTAssertFalse(records.first?.priced ?? true)
        XCTAssertEqual(records.first?.costBasis, .unknown)
    }

    func testLimitSamplesSkipUnchangedReadings() {
        let now = Date()
        let reset = now.addingTimeInterval(3600)
        let w = LimitWindow(provider: "Claude", key: "five_hour", utilization: 40,
                            resetsAt: reset, source: .official)
        XCTAssertEqual(warehouse.recordLimits([w], at: now), 1)
        XCTAssertEqual(warehouse.recordLimits([w], at: now.addingTimeInterval(60)), 0,
                       "an identical reading a minute later says nothing new")
        let moved = LimitWindow(provider: "Claude", key: "five_hour", utilization: 41,
                                resetsAt: reset, source: .official)
        XCTAssertEqual(warehouse.recordLimits([moved], at: now.addingTimeInterval(120)), 1)
        XCTAssertEqual(warehouse.limitSamples().count, 2)
    }

    func testLimitSamplesRefuseToGoBackwards() {
        let now = Date()
        let w = LimitWindow(provider: "Codex", key: "seven_day", utilization: 10,
                            resetsAt: now.addingTimeInterval(86400), source: .official)
        warehouse.recordLimits([w], at: now)
        let older = LimitWindow(provider: "Codex", key: "seven_day", utilization: 9,
                                resetsAt: now.addingTimeInterval(86400), source: .official)
        XCTAssertEqual(warehouse.recordLimits([older], at: now.addingTimeInterval(-3600)), 0)
    }

    func testSamplesFromDifferentWindowInstancesAreNotTheSame() {
        let now = Date()
        let a = LimitSample(at: now, provider: "Claude", key: "five_hour", utilization: 90,
                            resetsAt: now.addingTimeInterval(60), source: .official)
        let b = LimitSample(at: now.addingTimeInterval(120), provider: "Claude",
                            key: "five_hour", utilization: 2,
                            resetsAt: now.addingTimeInterval(18000), source: .official)
        XCTAssertFalse(a.sameWindowInstance(as: b))
    }

    func testByDayFoldsProvidersTogether() {
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        var codex = entry(day)
        codex = Entry(provider: "Codex", key: "x", ts: day, model: "gpt-5", input: 5,
                      output: 5, cacheRead: 0, cache5m: 0, cache1h: 0)
        let records = Warehouse.rollup(entries: [entry(day), codex], config: config)
        let byDay = Warehouse.byDay(records)
        XCTAssertEqual(byDay.count, 1)
        XCTAssertEqual(byDay[0].io, 1110)
        XCTAssertFalse(byDay[0].priced, "one unpriced model makes the day's total partial")
    }
}
