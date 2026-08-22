// What a chart in another language reads. The fixture is this engine's own output for fixed
// inputs, so it holds still: the command's numbers move with the clock, the shape does not.
//
// RedLine.Core.Tests reads the same file. Rename a key here and one of the two fails.
import XCTest
@testable import RedlineCore

final class TrendsContractTests: XCTestCase {
    private var fixture: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("windows/RedLine.Core.Tests/fixtures/trends.json")
    }

    /// The inputs the fixture was made from. Dates are fixed and the calendar is UTC, so the
    /// day keys are the same on any machine that runs this.
    private func report() -> [String: Any] {
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let first = Date(timeIntervalSince1970: 1_787_184_000)   // 2026-08-20T00:00:00Z
        let starts = (0..<3).map { first.addingTimeInterval(Double($0) * 86400) }

        let claude = ProviderTrend(provider: "Claude", points: [
            UsagePoint(start: starts[0], io: 6200, cost: 0.021, cacheRead: 400, cacheWrite: 0),
            UsagePoint(start: starts[1], io: 4200, cost: 0.015, cacheRead: 0, cacheWrite: 120),
            UsagePoint(start: starts[2], io: 2200, cost: 0.009, cacheRead: 0, cacheWrite: 0),
        ])
        // A quiet first day, which has to survive as a zero rather than as a missing date
        let codex = ProviderTrend(provider: "Codex", points: [
            UsagePoint(start: starts[0], io: 0, cost: 0, cacheRead: 0, cacheWrite: 0),
            UsagePoint(start: starts[1], io: 1500, cost: 0, cacheRead: 0, cacheWrite: 0),
            UsagePoint(start: starts[2], io: 800, cost: 0, cacheRead: 0, cacheWrite: 0),
        ])
        let models = [
            ModelShare(model: "claude-sonnet-5", provider: "Claude", io: 12600,
                       cost: 0.045, priced: true),
            ModelShare(model: "gpt-5-codex", provider: "Codex", io: 2300,
                       cost: 0, priced: false),
        ]
        return Trends.report(days: 3, series: Trends.combine([claude, codex]),
                             providers: [claude, codex], models: models, calendar: utc)
    }

    private func recorded() throws -> [String: Any] {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: fixture))
        return try XCTUnwrap(json as? [String: Any])
    }

    /// Both sides are compared after being read back as JSON, not as text and not as Swift
    /// values. Text differs by platform: Linux writes a cost as 0.045 where macOS writes
    /// 0.044999999999999998, which is the same double printed two ways, and the fixture must
    /// not fail on the machine that reads it.
    func testWhatIsPublishedIsStillWhatTheFixtureHolds() throws {
        let data = Data(RedlineCLI.encode(report()).utf8)
        let published = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(NSDictionary(dictionary: published),
                       NSDictionary(dictionary: try recorded()),
                       "the shape the Windows dashboard reads has moved; regenerate "
                       + "windows/RedLine.Core.Tests/fixtures/trends.json from this test's "
                       + "own inputs")
    }

    /// The axis is the engine's call. Two shells working it out separately would label one
    /// range two ways, and a chart whose labels disagree with its bars is worse than no chart.
    func testTheAxisCadenceTravelsWithTheSeries() throws {
        let root = try recorded()
        XCTAssertEqual(root["label_every_days"] as? Int, 1)
        XCTAssertEqual(DailyAxis.strideDays(for: 30), 5)
        XCTAssertEqual(DailyAxis.strideDays(for: 90), 14)
    }

    /// Adding the providers up is the engine's job too, so a chart drawing one line and a
    /// chart drawing three cannot disagree about the same day.
    func testTheCombinedSeriesIsEveryProviderAddedUp() throws {
        let root = try recorded()
        let series = try XCTUnwrap(root["series"] as? [[String: Any]])
        XCTAssertEqual(series.map { $0["tokens"] as? Int }, [6200, 5700, 3000])
        XCTAssertEqual(series.first?["day"] as? String, "2026-08-20")
        XCTAssertEqual(series.first?["label"] as? String, "Aug 20")
    }

    /// An unpriced model makes the total a floor. The flag is all that keeps a dashboard from
    /// drawing it as a fact.
    func testTheUnpricedFlagIsPublishedWithTheTotal() throws {
        let root = try recorded()
        XCTAssertEqual(root["has_unpriced"] as? Bool, true)
        let models = try XCTUnwrap(root["models"] as? [[String: Any]])
        XCTAssertEqual(models.filter { $0["priced"] as? Bool == false }.count, 1)
    }
}
