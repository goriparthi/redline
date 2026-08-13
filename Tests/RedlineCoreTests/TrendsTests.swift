import XCTest
@testable import RedlineCore

final class TrendsTests: XCTestCase {
    // Fixed calendar and instant so bucket boundaries are deterministic
    private var cal: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()
    private let now = Date(timeIntervalSince1970: 1_700_000_000) // 2023-11-14 22:13:20 UTC

    private var cfg: Config = {
        var c = Config()
        c.pricing = ["sonnet": ModelPrice(input: 3, output: 15, cacheRead: 0.3)]
        return c
    }()

    private func entry(_ provider: String, _ model: String, at: Date,
                       input: Int = 0, output: Int = 0) -> Entry {
        Entry(provider: provider, key: nil, ts: at, model: model, input: input,
              output: output, cacheRead: 0, cache5m: 0, cache1h: 0)
    }

    func testDailyBucketsCoverTheWholeRangeIncludingQuietDays() {
        let entries = [entry("Claude", "claude-sonnet-5", at: now, input: 100, output: 10)]
        let trends = Trends.trend(entries, by: .day, count: 7, now: now,
                                  calendar: cal, config: cfg)
        XCTAssertEqual(trends.count, 1)
        XCTAssertEqual(trends[0].points.count, 7, "a quiet day is a zero, not a missing point")
        XCTAssertEqual(trends[0].points.last?.io, 110, "today is the final bucket")
        XCTAssertEqual(trends[0].points.dropLast().allSatisfy { $0.io == 0 }, true)
    }

    func testBucketsAreOrderedOldestFirst() {
        let starts = Trends.bucketStarts(by: .day, count: 5, now: now, calendar: cal)
        XCTAssertEqual(starts.count, 5)
        XCTAssertEqual(starts, starts.sorted(), "charts read left to right")
        XCTAssertEqual(cal.dateInterval(of: .day, for: now)?.start, starts.last)
    }

    func testEntriesOlderThanTheRangeAreExcluded() {
        let old = cal.date(byAdding: .day, value: -30, to: now)!
        let trends = Trends.trend([entry("Claude", "claude-sonnet-5", at: old, input: 5)],
                                  by: .day, count: 7, now: now, calendar: cal, config: cfg)
        XCTAssertTrue(trends.isEmpty, "no provider had usage inside the window")
    }

    func testSeparateProvidersGetSeparateSeries() {
        let entries = [
            entry("Claude", "claude-sonnet-5", at: now, input: 100),
            entry("Codex", "gpt-5.3-codex", at: now, input: 50),
        ]
        let trends = Trends.trend(entries, by: .day, count: 3, now: now,
                                  calendar: cal, config: cfg)
        XCTAssertEqual(trends.map(\.provider), ["Claude", "Codex"], "sorted for stable colours")
        XCTAssertEqual(trends[0].totalIO, 100)
        XCTAssertEqual(trends[1].totalIO, 50)
    }

    func testHourlyBucketing() {
        let anHourAgo = cal.date(byAdding: .hour, value: -1, to: now)!
        let entries = [
            entry("Claude", "claude-sonnet-5", at: now, input: 10),
            entry("Claude", "claude-sonnet-5", at: anHourAgo, input: 20),
        ]
        let trends = Trends.trend(entries, by: .hour, count: 3, now: now,
                                  calendar: cal, config: cfg)
        XCTAssertEqual(trends[0].points.count, 3)
        XCTAssertEqual(trends[0].points.last?.io, 10)
        XCTAssertEqual(trends[0].points[1].io, 20)
    }

    func testCostMatchesTheMenuBarAggregation() {
        let e = Entry(provider: "Claude", key: nil, ts: now, model: "claude-sonnet-5",
                      input: 1_000_000, output: 0, cacheRead: 0, cache5m: 0, cache1h: 0)
        let trend = Trends.trend([e], by: .day, count: 1, now: now,
                                 calendar: cal, config: cfg)
        let agg = aggregate([e], since: now.addingTimeInterval(-60), config: cfg)
        XCTAssertEqual(trend[0].totalCost, agg.cost, accuracy: 0.0001,
                       "charts and the menu must not disagree on spend")
    }

    func testPeakFindsTheBusiestBucket() {
        let yesterday = cal.date(byAdding: .day, value: -1, to: now)!
        let entries = [
            entry("Claude", "claude-sonnet-5", at: now, input: 10),
            entry("Claude", "claude-sonnet-5", at: yesterday, input: 900),
        ]
        let trend = Trends.trend(entries, by: .day, count: 3, now: now,
                                 calendar: cal, config: cfg)
        XCTAssertEqual(trend[0].peak?.io, 900)
    }

    func testByModelRanksAndFlagsUnpriced() {
        let entries = [
            entry("Claude", "claude-sonnet-5", at: now, input: 100, output: 0),
            entry("Codex", "gpt-5.3-codex", at: now, input: 500, output: 0),
        ]
        let shares = Trends.byModel(entries, since: now.addingTimeInterval(-3600), config: cfg)
        XCTAssertEqual(shares.map(\.model), ["gpt-5.3-codex", "claude-sonnet-5"],
                       "largest first")
        XCTAssertFalse(shares[0].priced, "no pricing entry for the Codex model")
        XCTAssertEqual(shares[0].cost, 0)
        XCTAssertTrue(shares[1].priced)
    }

    func testZeroCountReturnsNothingRatherThanCrashing() {
        XCTAssertTrue(Trends.trend([], by: .day, count: 0, now: now,
                                   calendar: cal, config: cfg).isEmpty)
    }
}
