// The history store. Its whole reason to exist is that transcripts get pruned, so what
// survives a shrinking scan is the thing worth testing hardest.
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

    func testAPrunedTranscriptCannotEraseTheDay() {
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.merge(entries: [entry(day, input: 5000)], config: config)
        // The transcript that record came from is gone, so a later pass brings nothing for
        // that day at all. The day still has to read the same afterwards.
        warehouse.merge(entries: [entry(day.addingTimeInterval(86400), input: 10)],
                        config: config)
        let stored = warehouse.records(since: day, until: day)
        XCTAssertEqual(stored.count, 1)
        XCTAssertEqual(stored[0].input, 5000, "a day already recorded must not shrink")
    }

    func testTwoRecordsOnOneDayAddUpRatherThanCompete() {
        // Distinct records of the same day are both facts about it. The old file store had
        // to guess which scan was fuller; entries are deduped, so they simply sum.
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.merge(entries: [entry(day, input: 100)], config: config)
        warehouse.merge(entries: [entry(day, input: 900)], config: config)
        XCTAssertEqual(warehouse.load().first?.input, 1000)
    }

    func testTheSameRecordTwiceIsCountedOnce() {
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        let e = entry(day, input: 500)
        warehouse.merge(entries: [e], config: config)
        warehouse.merge(entries: [e], config: config)
        XCTAssertEqual(warehouse.load().first?.input, 500)
        XCTAssertEqual(warehouse.entryCount, 1)
    }

    func testRecordsWithNoIdDedupeOnTheirOrigin() {
        // Codex and Ollama records carry no id. Re-reading the same byte of the same file
        // must not double the day.
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        let e = Entry(provider: "Codex", key: nil, ts: day, model: "gpt-5", input: 100,
                      output: 10, cacheRead: 0, cache5m: 0, cache1h: 0,
                      origin: "/tmp/session.jsonl#4096")
        XCTAssertEqual(warehouse.ingest([e]), 1)
        XCTAssertEqual(warehouse.ingest([e]), 0, "same file position, same record")
        XCTAssertEqual(warehouse.entryCount, 1)
    }

    func testDailyRowOutlivesTheEntriesItWasBuiltFrom() {
        // The seam the two tables exist for: entries age out on retention, the day does not.
        let day = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.merge(entries: [entry(day, input: 4000)], config: config)
        let removed = warehouse.pruneEntries(
            now: day.addingTimeInterval(Double(Warehouse.entryRetentionDays + 2) * 86400))
        XCTAssertEqual(removed, 1)
        XCTAssertEqual(warehouse.entryCount, 0)
        XCTAssertEqual(warehouse.records(since: day, until: day).first?.input, 4000,
                       "the rollup is the long memory and must survive its own entries")
    }

    func testEntriesComeBackInsideTheirWindow() {
        let base = Date(timeIntervalSince1970: 1_760_000_000)
        warehouse.ingest([entry(base, input: 1), entry(base.addingTimeInterval(7200), input: 2)])
        XCTAssertEqual(warehouse.entries().count, 2)
        XCTAssertEqual(warehouse.entries(since: base.addingTimeInterval(3600)).count, 1)
        XCTAssertEqual(warehouse.entries(provider: "Codex").count, 0)
    }

    func testIngestMarksTrackWhereReadingStopped() {
        let mark = IngestMark(path: "/tmp/a.jsonl", provider: "Claude", size: 900,
                              byteOffset: 880, mtime: Date(timeIntervalSince1970: 1_760_000_000))
        warehouse.setIngestMark(mark)
        XCTAssertEqual(warehouse.ingestMark(path: "/tmp/a.jsonl")?.byteOffset, 880)
        warehouse.forgetIngestMarks(notIn: [], provider: "Claude")
        XCTAssertNil(warehouse.ingestMark(path: "/tmp/a.jsonl"),
                     "a transcript that is gone should not leave a mark behind")
    }

    func testLastKnownLimitsSurviveAQuietPoll() {
        let now = Date(timeIntervalSince1970: 1_760_000_000)
        let w = LimitWindow(provider: "Codex", key: "five_hour", utilization: 33,
                            resetsAt: now.addingTimeInterval(3600), source: .official)
        warehouse.recordLimits([w], at: now)
        let stored = warehouse.latestLimits(provider: "Codex")
        XCTAssertEqual(stored?.windows.count, 1)
        XCTAssertEqual(stored?.windows.first?.utilization, 33)
        XCTAssertEqual(stored?.at, now)
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
