// What the watcher writes for a shell to deliver, and the staleness rule both of them ask.
import XCTest
@testable import RedlineCore

final class AlertFeedTests: XCTestCase {
    private var home: URL!

    override func setUp() {
        super.setUp()
        home = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-alert-feed-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: home)
        super.tearDown()
    }

    private var feed: URL { home.appendingPathComponent("alert-feed.json") }

    private func event(_ kind: AlertEvent.Kind, id: String = "a") -> AlertEvent {
        AlertEvent(kind: kind, provider: "Claude", key: "five_hour",
                   title: "Claude · Session", body: "86% used", id: id)
    }

    private func read() throws -> [String: Any] {
        let data = try Data(contentsOf: feed)
        return try XCTUnwrap(
            try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    /// The words are the contract: a shell may treat a limit differently from a threshold,
    /// and it must not have to read the wording to tell them apart.
    func testEveryKindHasItsOwnWord() {
        XCTAssertEqual(AlertFeed.name(of: .threshold(80)), "threshold")
        XCTAssertEqual(AlertFeed.name(of: .limitReached), "limit_reached")
        XCTAssertEqual(AlertFeed.name(of: .projection), "projection")
        XCTAssertEqual(AlertFeed.name(of: .reset), "reset")
    }

    /// A limit reached is the only one that makes a noise, and that is decided once here
    /// rather than separately by every shell.
    func testOnlyAReachedLimitMakesASound() {
        XCTAssertTrue(AlertFeed.makesSound(.limitReached))
        XCTAssertFalse(AlertFeed.makesSound(.threshold(95)))
        XCTAssertFalse(AlertFeed.makesSound(.projection))
        XCTAssertFalse(AlertFeed.makesSound(.reset))
    }

    func testAThresholdCarriesThePercentItCrossed() {
        let row = AlertFeed.row(event(.threshold(80)))
        XCTAssertEqual(row["percent"] as? Int, 80)
        XCTAssertEqual(row["kind"] as? String, "threshold")
        XCTAssertEqual(row["sound"] as? Bool, false)
        // Nothing else has a percent to report, and a zero would be one
        XCTAssertNil(AlertFeed.row(event(.reset))["percent"])
    }

    /// The sequence is how a shell tells a new batch from the one it has already posted, so
    /// it has to keep climbing rather than restart with the process.
    func testTheSequenceClimbsAndSurvivesBeingReadBack() throws {
        XCTAssertEqual(AlertFeed.publish([event(.threshold(60), id: "a")], to: feed), 1)
        XCTAssertEqual(AlertFeed.publish([event(.threshold(80), id: "b")], to: feed), 2)
        XCTAssertEqual(AlertFeed.sequence(at: feed), 2)

        let root = try read()
        XCTAssertEqual(root["seq"] as? Int, 2)
        let events = try XCTUnwrap(root["events"] as? [[String: Any]])
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events.first?["id"] as? String, "b")
    }

    /// An empty batch would move the sequence for nothing and wake every reader watching
    /// the file, so it is not written at all.
    func testNothingToSayWritesNothing() {
        XCTAssertNil(AlertFeed.publish([], to: feed))
        XCTAssertFalse(FileManager.default.fileExists(atPath: feed.path))
        XCTAssertEqual(AlertFeed.sequence(at: feed), 0)
    }

    // MARK: - staleness

    func testAFeedThatHasGoneQuietIsStale() {
        var config = Config()
        config.pollIntervalSeconds = 300
        let now = Date()
        // Two polls or ten minutes, whichever is longer
        XCTAssertFalse(Alerting.claudeIsStale(asOf: now.addingTimeInterval(-599),
                                              config: config, now: now))
        XCTAssertTrue(Alerting.claudeIsStale(asOf: now.addingTimeInterval(-601),
                                             config: config, now: now))
    }

    func testALongPollIntervalWidensTheWindowRatherThanTheFloor() {
        var config = Config()
        config.pollIntervalSeconds = 3600
        let now = Date()
        XCTAssertFalse(Alerting.claudeIsStale(asOf: now.addingTimeInterval(-7199),
                                              config: config, now: now))
        XCTAssertTrue(Alerting.claudeIsStale(asOf: now.addingTimeInterval(-7201),
                                             config: config, now: now))
    }

    /// Never having read the feed is not the same as having read it long ago: there is no
    /// reading to call stale, and calling it stale would silence a first alert forever.
    func testNoReadingAtAllIsNotStale() {
        XCTAssertFalse(Alerting.claudeIsStale(asOf: nil, config: Config()))
    }

    /// Only Claude's windows come from a feed that can go quiet. Codex is rewritten from
    /// disk every poll, so staleness there would silence real news.
    func testOnlyClaudeWindowsGoStale() {
        var config = Config()
        config.pollIntervalSeconds = 300
        let stale = Alerting.staleness(claudeLimitsAsOf: Date().addingTimeInterval(-3600),
                                       config: config)
        XCTAssertTrue(stale(LimitWindow(provider: UsageStore.provider, key: "five_hour",
                                        utilization: 90, resetsAt: nil, source: .unknown)))
        XCTAssertFalse(stale(LimitWindow(provider: "Codex", key: "five_hour",
                                         utilization: 90, resetsAt: nil, source: .unknown)))
    }
}
