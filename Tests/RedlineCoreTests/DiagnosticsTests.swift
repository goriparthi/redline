// The diagnostics file is only worth having if it survives the things that break log files:
// a truncated tail, a rotation, a secret that should never have been written.
import XCTest
@testable import RedlineCore

final class DiagnosticsTests: XCTestCase {
    private var dir: URL!
    private var url: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("diag-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("diagnostics.ndjson")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func log(_ level: DiagLevel = .debug) -> DiagnosticsLog {
        DiagnosticsLog(url: url, version: "1.2.3", minimumLevel: level)
    }

    func testAnEventRoundTripsThroughTheFile() {
        let l = log()
        l.error("feed.parse_failed", "sidecar is not valid JSON", ["bytes": "12"])
        let events = l.read()
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events[0].code, "feed.parse_failed")
        XCTAssertEqual(events[0].level, .error)
        XCTAssertEqual(events[0].context["bytes"], "12")
        XCTAssertEqual(events[0].version, "1.2.3")
    }

    func testOneEventPerLine() throws {
        let l = log()
        l.info("a.b", "first")
        l.info("c.d", "second")
        let text = try String(contentsOf: url, encoding: .utf8)
        XCTAssertEqual(text.split(separator: "\n").count, 2)
    }

    func testTimestampsAreUTC() {
        let l = log()
        l.log(.info, "a.b", "stamped", now: Date(timeIntervalSince1970: 0))
        XCTAssertEqual(l.read().first?.at, "1970-01-01T00:00:00Z")
    }

    func testLevelBelowTheMinimumIsDropped() {
        let l = log(.warn)
        l.debug("a.b", "noise")
        l.info("a.b", "noise")
        l.warn("a.b", "kept")
        XCTAssertEqual(l.read().map(\.code), ["a.b"])
        XCTAssertEqual(l.read().first?.level, .warn)
    }

    func testReadFiltersByLevel() {
        let l = log()
        l.info("quiet.one", "x")
        l.error("loud.one", "y")
        XCTAssertEqual(l.read(minimumLevel: .error).map(\.code), ["loud.one"])
    }

    /// A crash mid-write leaves half a line. The events before it must still be readable,
    /// because that is exactly when someone goes looking.
    func testATruncatedTailDoesNotHideEarlierEvents() throws {
        let l = log()
        l.error("first.event", "kept")
        l.error("second.event", "kept")
        var text = try String(contentsOf: url, encoding: .utf8)
        text += "{\"at\":\"2026-01-01T00:00:00Z\",\"lev"
        try text.write(to: url, atomically: true, encoding: .utf8)
        XCTAssertEqual(l.read().map(\.code), ["first.event", "second.event"])
    }

    func testAttemptRecordsAThrownErrorAndReturnsNil() {
        struct Boom: Error {}
        let l = log()
        let result: Int? = l.attempt("thing.failed", ["path": "/tmp/x"]) {
            throw Boom()
        }
        XCTAssertNil(result)
        let e = l.read().first
        XCTAssertEqual(e?.code, "thing.failed")
        XCTAssertEqual(e?.level, .error)
        XCTAssertEqual(e?.context["path"], "/tmp/x")
        XCTAssertNotNil(e?.context["error"])
    }

    func testAttemptPassesTheValueThroughOnSuccess() {
        let l = log()
        XCTAssertEqual(l.attempt("nope") { 42 }, 42)
        XCTAssertTrue(l.read().isEmpty)
    }

    func testTallyGroupsByCodeMostFrequentFirst() {
        let l = log()
        l.error("often", "x")
        l.error("often", "x")
        l.error("often", "x")
        l.warn("rare", "y")
        let rows = l.tally()
        XCTAssertEqual(rows.map(\.code), ["often", "rare"])
        XCTAssertEqual(rows.map(\.count), [3, 1])
    }

    func testRotationKeepsEarlierEventsReadable() {
        let l = log()
        // Past the 1 MB threshold, with each event well under it
        let filler = String(repeating: "x", count: 4_000)
        for i in 0..<300 { l.error("bulk.\(i)", filler) }
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.appendingPathExtension("1").path),
                      "expected a rotated generation")
        let codes = Set(l.read().map(\.code))
        XCTAssertTrue(codes.contains("bulk.299"), "newest event should survive rotation")
        XCTAssertGreaterThan(codes.count, 1)
    }

    func testConcurrentWritesDoNotCorruptLines() {
        let l = log()
        let group = DispatchGroup()
        for i in 0..<100 {
            DispatchQueue.global().async(group: group) { l.error("race.\(i % 5)", "hit") }
        }
        group.wait()
        XCTAssertEqual(l.read().count, 100, "every line should parse")
    }

    // MARK: - Redaction

    func testHomePathIsAbbreviated() {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        XCTAssertEqual(Redaction.scrub("\(home)/.config/redline"), "~/.config/redline")
    }

    func testATokenIsNotWrittenToTheFile() {
        let l = log()
        l.error("oauth.refresh_failed", "bearer sk-ant-oat01-abc123def456ghi789jkl012")
        let text = l.read().first?.message ?? ""
        XCTAssertFalse(text.contains("abc123def456ghi789jkl012"), "token leaked: \(text)")
        XCTAssertTrue(text.contains("<redacted>"))
    }

    func testRedactionKeepsTheUsefulPart() {
        let out = Redaction.scrub("token refresh failed with sk-ant-oat01-abc123def456ghi789")
        XCTAssertTrue(out.contains("refresh"))
        XCTAssertTrue(out.contains("failed"))
    }

    func testOrdinaryMessagesAreUntouched() {
        XCTAssertEqual(Redaction.scrub("sidecar is not valid JSON"),
                       "sidecar is not valid JSON")
    }
}
