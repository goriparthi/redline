// A dismissal has to hide a finding everywhere at once, and has to expire. Both are the point.
import XCTest
@testable import RedlineCore

final class FindingsDismissalTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_787_000_000)

    private func finding(_ id: String, kind: Finding.Kind = .fixNow) -> Finding {
        Finding(id: id, kind: kind, basis: .measured, title: id, detail: "d", evidence: [])
    }

    private func report(_ ids: [String]) -> FindingsReport {
        FindingsReport(generatedAt: now, windowDays: 14, sessionsScanned: 3,
                       findings: ids.map { finding($0) })
    }

    func testDismissedFindingIsHiddenUntilTheSnoozeExpires() {
        var d = FindingsDismissals()
        d.dismiss("mcp-unused", at: now)
        XCTAssertTrue(d.isHidden("mcp-unused", snoozeDays: 14, now: now))
        // A day short of the window is still hidden; a day past it is not
        XCTAssertTrue(d.isHidden("mcp-unused", snoozeDays: 14,
                                 now: now.addingTimeInterval(13 * 86400)))
        XCTAssertFalse(d.isHidden("mcp-unused", snoozeDays: 14,
                                  now: now.addingTimeInterval(15 * 86400)))
        XCTAssertFalse(d.isHidden("reread-files", snoozeDays: 14, now: now))
    }

    /// The menu line and the panel both read `visible`, so this is what keeps them agreeing
    func testVisibleFiltersAndCountsWhatItHid() {
        var d = FindingsDismissals()
        d.dismiss("a", at: now)
        let v = report(["a", "b", "c"]).visible(d, snoozeDays: 14, now: now)
        XCTAssertEqual(v.findings.map(\.id), ["b", "c"])
        XCTAssertEqual(v.hidden, 1)
        XCTAssertEqual(v.summary, "2 findings · 2 to fix")
        // Everything else about the report survives the filter
        XCTAssertEqual(v.sessionsScanned, 3)
        XCTAssertEqual(v.windowDays, 14)
    }

    /// A finding that is still true after the snooze must come back, not vanish quietly
    func testAStillTrueFindingReturnsAfterTheSnooze() {
        var d = FindingsDismissals()
        d.dismiss("a", at: now)
        let later = now.addingTimeInterval(20 * 86400)
        let v = report(["a", "b"]).visible(d, snoozeDays: 14, now: later)
        XCTAssertEqual(v.findings.map(\.id), ["a", "b"])
        XCTAssertEqual(v.hidden, 0)
    }

    func testEmptyAfterDismissingEverythingStillReportsWhatItHid() {
        var d = FindingsDismissals()
        d.dismiss("a", at: now)
        d.dismiss("b", at: now)
        let v = report(["a", "b"]).visible(d, snoozeDays: 14, now: now)
        XCTAssertTrue(v.isEmpty)
        XCTAssertEqual(v.hidden, 2)
        XCTAssertEqual(v.summary, "no findings")
    }

    func testRestoreAllBringsThemBackNow() {
        var d = FindingsDismissals()
        d.dismiss("a", at: now)
        d.restoreAll()
        XCTAssertTrue(d.isEmpty)
        XCTAssertEqual(report(["a"]).visible(d, snoozeDays: 14, now: now).findings.count, 1)
    }

    /// The file must not accumulate a row for every finding the checks ever produced
    func testPruneDropsExpiredSnoozesOnly() {
        var d = FindingsDismissals()
        d.dismiss("old", at: now.addingTimeInterval(-30 * 86400))
        d.dismiss("fresh", at: now)
        d.prune(snoozeDays: 14, now: now)
        XCTAssertFalse(d.isHidden("old", snoozeDays: 14, now: now))
        XCTAssertTrue(d.isHidden("fresh", snoozeDays: 14, now: now))
        XCTAssertEqual(d.dismissed.count, 1)
    }

    /// A clock that moved backwards must not read as a snooze that has already run out
    func testAFutureDismissalIsTreatedAsHidden() {
        var d = FindingsDismissals()
        d.dismiss("a", at: now.addingTimeInterval(86400))
        XCTAssertTrue(d.isHidden("a", snoozeDays: 14, now: now))
    }

    func testRoundTripsThroughDiskAndSurvivesAMissingFile() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-dismiss-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("findings-dismissed.json")

        // Absent is empty, not an error
        XCTAssertTrue(FindingsDismissalStore.load(from: url).isEmpty)

        var d = FindingsDismissals()
        d.dismiss("mcp-unused", at: now)
        XCTAssertTrue(FindingsDismissalStore.save(d, to: url))
        XCTAssertEqual(FindingsDismissalStore.load(from: url), d)

        // Garbage on disk degrades to empty rather than taking the panel out
        try "not json".write(to: url, atomically: true, encoding: .utf8)
        XCTAssertTrue(FindingsDismissalStore.load(from: url).isEmpty)
    }

    func testSnoozeDaysIsValidatedAndOutOfRangeFallsBack() {
        XCTAssertEqual(Config().findingsSnoozeDays, 14)
        XCTAssertEqual(Config.apply(["findingsSnoozeDays": 30], to: Config())
                        .findingsSnoozeDays, 30)
        for bad: Any in [0, -1, 400, "sometimes"] {
            XCTAssertEqual(Config.apply(["findingsSnoozeDays": bad], to: Config())
                            .findingsSnoozeDays, 14, "\(bad) should have been rejected")
        }
    }
}
