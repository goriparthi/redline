// Exercises the Claude Code session registry reader against synthetic records in a temp dir.
import XCTest
@testable import RedlineCore

final class ClaudeFleetStoreTests: XCTestCase {
    private var dir: URL!
    /// A fixed start time every fixture claims, so a record and the probe agree by default
    private let started = Date(timeIntervalSince1970: 1_787_067_508)

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-fleet-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func ctime(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "EEE MMM d HH:mm:ss yyyy"
        return f.string(from: d)
    }

    private func write(_ obj: [String: Any], pid: Int) throws {
        let data = try JSONSerialization.data(withJSONObject: obj)
        try data.write(to: dir.appendingPathComponent("\(pid).json"))
    }

    private func record(pid: Int, status: String, statusAt: Date,
                        extra: [String: Any] = [:]) -> [String: Any] {
        var obj: [String: Any] = [
            "pid": pid,
            "sessionId": "s-\(pid)",
            "cwd": "/Users/x/work/proj-\(pid)",
            "startedAt": Int(started.timeIntervalSince1970 * 1000),
            "procStart": ctime(started),
            "version": "2.1.234",
            "kind": "interactive",
            "entrypoint": "cli",
            "name": "proj-\(pid)",
            "status": status,
            "updatedAt": Int(statusAt.timeIntervalSince1970 * 1000),
            "statusUpdatedAt": Int(statusAt.timeIntervalSince1970 * 1000),
            "bridgeSessionId": "session_\(pid)",
        ]
        for (k, v) in extra { obj[k] = v }
        return obj
    }

    /// Every PID alive, all claiming the same start the fixtures write
    private var allAlive: ProcessProbe {
        ProcessProbe { [started] _ in started }
    }

    private func store(_ probe: ProcessProbe? = nil) -> ClaudeFleetStore {
        ClaudeFleetStore(root: dir, probe: probe ?? allAlive)
    }

    func testReadsAWaitingSessionWithWaitingFor() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 100, status: "waiting",
                         statusAt: now.addingTimeInterval(-840),
                         extra: ["waitingFor": "input needed"]), pid: 100)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.count, 1)
        let s = snap.sessions[0]
        XCTAssertEqual(s.state, .waiting)
        XCTAssertEqual(s.waitingFor, "input needed")
        XCTAssertEqual(s.folder, "proj-100")
        XCTAssertEqual(s.label, "proj-100")
        XCTAssertEqual(s.timeInStatus(now: now).map { Int($0) }, 840)
        XCTAssertEqual(s.claudeURL?.absoluteString,
                       "https://claude.ai/code/session_100")
        XCTAssertEqual(snap.waiting.count, 1)
    }

    func testUnknownEntrypointAndUnknownFieldsSurvive() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 101, status: "busy", statusAt: now,
                         extra: ["entrypoint": "desktop",
                                 "kind": "headless",
                                 "someFutureField": ["nested": true],
                                 "peerProtocol": 7]), pid: 101)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.count, 1)
        XCTAssertEqual(snap.sessions[0].entrypoint, "desktop")
        XCTAssertEqual(snap.sessions[0].kind, "headless")
        XCTAssertEqual(snap.sessions[0].state, .busy)
    }

    func testAnUnknownStatusStillShowsTheSession() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 102, status: "compacting", statusAt: now), pid: 102)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.count, 1)
        XCTAssertEqual(snap.sessions[0].state, .unknown)
        XCTAssertEqual(snap.sessions[0].status, "compacting")
    }

    func testMalformedRecordDoesNotTakeOutItsNeighbours() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 200, status: "idle", statusAt: now), pid: 200)
        try "{not json at all".write(to: dir.appendingPathComponent("201.json"),
                                    atomically: true, encoding: .utf8)
        // Valid JSON, but missing the two fields a row cannot be drawn without
        try write(["status": "busy"], pid: 202)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.map { $0.pid }, [200])
    }

    func testDeadPidIsTreatedAsAbsentAndTheFileIsLeftAlone() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 300, status: "waiting", statusAt: now), pid: 300)
        let snap = ClaudeFleetStore(root: dir, probe: ProcessProbe { _ in nil }).scan(now: now)
        XCTAssertTrue(snap.isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: dir.appendingPathComponent("300.json").path))
    }

    /// Claude Code writes procStart in UTC while the field names no zone, so reading it as
    /// local rejected every live session on a machine west of Greenwich. Both readings count.
    func testProcStartWrittenInUTCIsAccepted() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        var obj = record(pid: 302, status: "busy", statusAt: now)
        let utc = DateFormatter()
        utc.locale = Locale(identifier: "en_US_POSIX")
        utc.dateFormat = "EEE MMM d HH:mm:ss yyyy"
        utc.timeZone = TimeZone(secondsFromGMT: 0)
        obj["procStart"] = utc.string(from: started)
        try write(obj, pid: 302)
        XCTAssertEqual(store().scan(now: now).sessions.map { $0.pid }, [302])
    }

    /// PIDs are reused, so a record whose claimed start does not match the live process is a
    /// leftover pointing at somebody else's process
    func testRecycledPidIsRejected() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 301, status: "busy", statusAt: now), pid: 301)
        let other = started.addingTimeInterval(3600)
        let snap = ClaudeFleetStore(root: dir,
                                    probe: ProcessProbe { _ in other }).scan(now: now)
        XCTAssertTrue(snap.isEmpty)
    }

    func testSortsWaitingFirstThenLongestInStatus() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 1, status: "idle", statusAt: now.addingTimeInterval(-9000)),
                  pid: 1)
        try write(record(pid: 2, status: "busy", statusAt: now.addingTimeInterval(-60)), pid: 2)
        try write(record(pid: 3, status: "waiting", statusAt: now.addingTimeInterval(-300)),
                  pid: 3)
        try write(record(pid: 4, status: "waiting", statusAt: now.addingTimeInterval(-3600)),
                  pid: 4)
        try write(record(pid: 5, status: "busy", statusAt: now.addingTimeInterval(-600)), pid: 5)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.map { $0.pid }, [4, 3, 5, 2, 1])
    }

    func testKeyFilesAreNeverRead() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        try write(record(pid: 400, status: "idle", statusAt: now), pid: 400)
        try "supersecret".write(
            to: dir.appendingPathComponent("400.abc123.key"),
            atomically: true, encoding: .utf8)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions.count, 1)
    }

    func testMissingDirectoryIsNotAnError() {
        let gone = dir.appendingPathComponent("nope")
        XCTAssertTrue(ClaudeFleetStore(root: gone).scan().isEmpty)
    }

    func testNameFallsBackToTheFolder() throws {
        let now = Date(timeIntervalSince1970: 1_787_070_000)
        var obj = record(pid: 500, status: "busy", statusAt: now)
        obj.removeValue(forKey: "name")
        obj.removeValue(forKey: "statusUpdatedAt")
        obj.removeValue(forKey: "updatedAt")
        try write(obj, pid: 500)
        let snap = store().scan(now: now)
        XCTAssertEqual(snap.sessions[0].label, "proj-500")
        XCTAssertNil(snap.sessions[0].timeInStatus(now: now))
    }

    /// The real probe, against this very process: a start time that exists and is not absurd
    func testLiveProbeAnswersForThisProcess() {
        let start = ProcessProbe.live.startTime(getpid())
        XCTAssertNotNil(start)
        if let start {
            XCTAssertLessThan(abs(start.timeIntervalSinceNow), 86400)
        }
        XCTAssertNil(ProcessProbe.live.startTime(Int32.max))
    }
}
