// The watch loop, checked on the two things it actually has to get right: which directories
// it decides to watch, and that a change turns into exactly one pass rather than none or ten.
import XCTest
@testable import RedlinePlatform
import RedlineCore

private let oneRecord = Ingest.Outcome(added: 1, byProvider: ["Claude": 1], total: 1)

final class WatchLoopTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-loop-\(UUID().uuidString)")
        try FileManager.default.createDirectory(
            at: home.appendingPathComponent(".claude/projects/demo"),
            withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func loop(debounce: TimeInterval = 0.2, sweep: TimeInterval = 3600,
                      ingest: @escaping () -> Ingest.Outcome? = { oneRecord },
                      report: @escaping (WatchLoop.Event) -> Void = { _ in }) -> WatchLoop {
        WatchLoop(options: .init(sweepSeconds: sweep, debounceSeconds: debounce,
                                 home: home, ingest: ingest),
                  report: report)
    }



    // MARK: - Which directories

    /// The whole reason this exists: a transcript lives one directory below the root, so
    /// watching only the root sees a session appear and never sees it written to.
    func testItWatchesBelowTheRootAndNotJustTheRoot() {
        let found = loop().directoriesToWatch().map(\.path)
        XCTAssertTrue(found.contains { $0.hasSuffix("/.claude/projects") }, "\(found)")
        XCTAssertTrue(found.contains { $0.hasSuffix("/.claude/projects/demo") },
                      "the project directory itself was not watched: \(found)")
    }

    /// Codex nests a year, a month and a day, so three levels has to reach the transcripts.
    func testItReachesTheBottomOfTheCodexDateTree() throws {
        let day = home.appendingPathComponent(".codex/sessions/2026/08/21")
        try FileManager.default.createDirectory(at: day, withIntermediateDirectories: true)
        let found = loop().directoriesToWatch().map(\.path)
        XCTAssertTrue(found.contains { $0.hasSuffix("/2026/08/21") },
                      "never reached the day directory: \(found)")
    }

    func testARootThatDoesNotExistIsSkippedRatherThanFailing() {
        let found = loop().directoriesToWatch()
        XCTAssertFalse(found.contains { $0.path.contains(".codex") })
        XCTAssertFalse(found.isEmpty)
    }

    /// A background process that opens a descriptor per directory in someone else's tree
    /// needs a ceiling, or a large enough tree takes the machine down with it.
    func testTheWatchCountIsCapped() throws {
        for i in 0..<40 {
            try FileManager.default.createDirectory(
                at: home.appendingPathComponent(".claude/projects/p\(i)"),
                withIntermediateDirectories: true)
        }
        XCTAssertEqual(loop().directoriesToWatch(limit: 12).count, 12)
    }

    // MARK: - When it runs

    func testItIngestsOnceOnStartWithoutWaitingForAChange() {
        let started = expectation(description: "ingested on start")
        started.assertForOverFulfill = false
        let subject = loop(report: { event in
            if case .ingested(_, let reason) = event, reason == "start" { started.fulfill() }
        })
        Thread.detachNewThread { subject.run() }
        wait(for: [started], timeout: 10)
        subject.stop()
    }

    /// A transcript is appended line by line, so a burst has to collapse into one pass.
    func testABurstOfWritesCollapsesIntoASinglePass() throws {
        let counter = PassCounter()
        let subject = loop(debounce: 0.6, report: { event in
            if case .ingested(_, let reason) = event, reason == "change" { counter.bump() }
        })
        Thread.detachNewThread { subject.run() }
        Thread.sleep(forTimeInterval: 1.0)

        let file = home.appendingPathComponent(".claude/projects/demo/session.jsonl")
        for i in 0..<8 {
            try "line \(i)\n".appendOrCreate(at: file)
            Thread.sleep(forTimeInterval: 0.05)
        }
        Thread.sleep(forTimeInterval: 2.5)
        subject.stop()

        XCTAssertGreaterThanOrEqual(counter.value, 1, "the burst was never noticed")
        XCTAssertLessThanOrEqual(counter.value, 3,
                                 "eight appends caused \(counter.value) passes, so the "
                                 + "debounce is not collapsing them")
    }

    // MARK: - Not waking itself up

    /// Publishing writes into the same directory the loop watches, so without this guard
    /// every publish would trigger the pass that produced it, forever.
    func testOurOwnOutputDoesNotCountAsAChange() throws {
        let subject = loop()
        let data = subject.dataDirectory
        try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        let feed = data.appendingPathComponent("claude-usage.json")
        try "{}".write(to: feed, atomically: true, encoding: .utf8)

        XCTAssertTrue(subject.dataDirectoryHasNewInput(), "the first look must see the input")
        XCTAssertFalse(subject.dataDirectoryHasNewInput(), "nothing moved, so nothing changed")

        // What publishing does
        try "{\"limits\":[]}".write(to: data.appendingPathComponent("snapshot.json"),
                                     atomically: true, encoding: .utf8)
        XCTAssertFalse(subject.dataDirectoryHasNewInput(),
                       "writing our own snapshot registered as an input change")
    }

    func testAReplacedFeedFileDoesCountAsAChange() throws {
        let subject = loop()
        let data = subject.dataDirectory
        try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        let feed = data.appendingPathComponent("claude-usage.json")
        try "{}".write(to: feed, atomically: true, encoding: .utf8)
        _ = subject.dataDirectoryHasNewInput()

        // mtime has one second of resolution on some filesystems, so the write has to land
        // in a different one for the comparison to mean anything
        Thread.sleep(forTimeInterval: 1.1)
        try "{\"five_hour\":{}}".write(to: feed, atomically: true, encoding: .utf8)
        XCTAssertTrue(subject.dataDirectoryHasNewInput(),
                      "a genuine feed update was mistaken for our own write")
    }

    func testStoppingIsIdempotent() {
        let subject = loop()
        Thread.detachNewThread { subject.run() }
        Thread.sleep(forTimeInterval: 0.5)
        subject.stop()
        XCTAssertNoThrow(subject.stop())
    }

    func testHistoryBeingOffIsReportedRatherThanIgnored() {
        let told = expectation(description: "said history is off")
        told.assertForOverFulfill = false
        let subject = loop(ingest: { nil }, report: { event in
            if case .historyOff = event { told.fulfill() }
        })
        Thread.detachNewThread { subject.run() }
        wait(for: [told], timeout: 10)
        subject.stop()
    }
}

private final class PassCounter {
    private let lock = NSLock()
    private var count = 0
    func bump() { lock.lock(); count += 1; lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return count }
}

private extension String {
    func appendOrCreate(at url: URL) throws {
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(utf8))
        } else {
            try write(to: url, atomically: true, encoding: .utf8)
        }
    }
}
