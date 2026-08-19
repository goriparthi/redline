// Incremental reading of append only transcripts. The failure modes worth pinning are all
// about position: a line read twice doubles a day, a line skipped loses one, and a record
// parsed from half a line is worse than either because it looks like data.
import XCTest
@testable import RedlineCore

final class TranscriptTailTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-tail-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func write(_ text: String, to name: String = "t.jsonl") -> URL {
        let url = dir.appendingPathComponent(name)
        try? text.write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    private func append(_ text: String, to url: URL) {
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data(text.utf8))
    }

    func testReadsEveryCompleteLineWithItsOffset() {
        let url = write("one\ntwo\nthree\n")
        var seen: [(String, Int)] = []
        let next = TranscriptTail.read(url: url, from: 0) { seen.append(($0, $1)) }
        XCTAssertEqual(seen.map(\.0), ["one", "two", "three"])
        XCTAssertEqual(seen.map(\.1), [0, 4, 8])
        XCTAssertEqual(next, 14)
    }

    func testResumesWhereItStopped() {
        let url = write("one\ntwo\n")
        let first = TranscriptTail.read(url: url, from: 0) { _, _ in }
        append("three\n", to: url)
        var seen: [String] = []
        let next = TranscriptTail.read(url: url, from: first) { line, _ in seen.append(line) }
        XCTAssertEqual(seen, ["three"], "already read lines must not be read again")
        XCTAssertEqual(next, 14)
    }

    func testAPartialTrailingLineIsLeftForNextTime() {
        // A transcript being written right now ends mid record. Half a JSON object parsed
        // once is a bug that hides until the day it matters.
        let url = write("one\ntwo\nthr")
        var seen: [String] = []
        let next = TranscriptTail.read(url: url, from: 0) { line, _ in seen.append(line) }
        XCTAssertEqual(seen, ["one", "two"])
        XCTAssertEqual(next, 8, "the offset must stop at the last complete line")

        append("ee\n", to: url)
        var rest: [String] = []
        TranscriptTail.read(url: url, from: next) { line, _ in rest.append(line) }
        XCTAssertEqual(rest, ["three"], "the line arrives whole on the next pass")
    }

    func testALineLongerThanAChunkSurvives() {
        let long = String(repeating: "x", count: TranscriptTail.chunkSize + 1024)
        let url = write("short\n\(long)\nafter\n")
        var seen: [String] = []
        TranscriptTail.read(url: url, from: 0) { line, _ in seen.append(line) }
        XCTAssertEqual(seen.count, 3)
        XCTAssertEqual(seen[1].count, long.count)
        XCTAssertEqual(seen[2], "after")
    }

    func testATruncatedFileIsReadFromTheStart() {
        // Truncation and rewriting both mean the offsets no longer point where we think.
        let mark = IngestMark(path: "/tmp/x", provider: "Claude", size: 900,
                              byteOffset: 880, mtime: Date())
        XCTAssertEqual(TranscriptTail.startOffset(mark: mark, size: 400), 0)
        XCTAssertEqual(TranscriptTail.startOffset(mark: mark, size: 1200), 880)
        XCTAssertEqual(TranscriptTail.startOffset(mark: nil, size: 1200), 0)
    }

    func testBlankLinesAreSkippedWithoutLosingPosition() {
        let url = write("one\n\ntwo\n")
        var seen: [(String, Int)] = []
        let next = TranscriptTail.read(url: url, from: 0) { seen.append(($0, $1)) }
        XCTAssertEqual(seen.map(\.0), ["one", "two"])
        XCTAssertEqual(seen.map(\.1), [0, 5])
        XCTAssertEqual(next, 9)
    }
}

final class ClaudeIngestTests: XCTestCase {
    private var home: URL!
    private var projects: URL!
    private var warehouse: Warehouse!
    private var store: UsageStore!

    override func setUpWithError() throws {
        home = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-ingest-\(UUID().uuidString)")
        projects = home.appendingPathComponent("projects/demo")
        try FileManager.default.createDirectory(at: projects, withIntermediateDirectories: true)
        warehouse = Warehouse(root: home.appendingPathComponent("history"))
        store = UsageStore(root: home.appendingPathComponent("projects"))
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    /// One Claude Code assistant line, in the shape the parser actually reads.
    static func line(id: String, ts: String, input: Int = 100, output: Int = 10,
                     model: String = "claude-sonnet-5") -> String {
        """
        {"timestamp":"\(ts)","requestId":"req_\(id)","message":{"id":"\(id)",\
        "model":"\(model)","usage":{"input_tokens":\(input),"output_tokens":\(output),\
        "cache_read_input_tokens":0}}}
        """
    }

    private func writeTranscript(_ lines: [String], name: String = "a.jsonl") -> URL {
        let url = projects.appendingPathComponent(name)
        try? (lines.joined(separator: "\n") + "\n").write(to: url, atomically: true,
                                                          encoding: .utf8)
        return url
    }

    private func append(_ lines: [String], to url: URL) {
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data((lines.joined(separator: "\n") + "\n").utf8))
    }

    func testFirstPassReadsEverythingAndSecondPassReadsNothing() {
        _ = writeTranscript([Self.line(id: "a", ts: "2026-08-18T10:00:00.000Z"),
                             Self.line(id: "b", ts: "2026-08-18T10:05:00.000Z")])
        XCTAssertEqual(store.ingest(into: warehouse), 2)
        XCTAssertEqual(store.ingest(into: warehouse), 0,
                       "an unchanged transcript costs a stat and nothing else")
        XCTAssertEqual(warehouse.entryCount, 2)
    }

    func testOnlyNewLinesAreIngested() {
        let url = writeTranscript([Self.line(id: "a", ts: "2026-08-18T10:00:00.000Z")])
        XCTAssertEqual(store.ingest(into: warehouse), 1)
        append([Self.line(id: "b", ts: "2026-08-18T11:00:00.000Z")], to: url)
        XCTAssertEqual(store.ingest(into: warehouse), 1)
        XCTAssertEqual(warehouse.entryCount, 2)
    }

    func testAResumedSessionCopiedIntoASecondFileIsCountedOnce() {
        // Resumed sessions copy identical message ids between transcripts. The id is the
        // dedup key, so the copy is recognised even though its byte position differs.
        let line = Self.line(id: "shared", ts: "2026-08-18T10:00:00.000Z")
        _ = writeTranscript([line], name: "one.jsonl")
        _ = writeTranscript([line], name: "two.jsonl")
        XCTAssertEqual(store.ingest(into: warehouse), 1)
        XCTAssertEqual(warehouse.entryCount, 1)
    }

    func testUsageSurvivesTheTranscriptBeingDeleted() {
        // The whole reason the store exists: Claude Code prunes its own projects directory.
        let url = writeTranscript([Self.line(id: "a", ts: "2026-08-18T10:00:00.000Z",
                                             input: 4321)])
        store.ingest(into: warehouse)
        warehouse.merge(entries: warehouse.entries(), config: Config())
        try? FileManager.default.removeItem(at: url)
        XCTAssertEqual(store.ingest(into: warehouse), 0)
        XCTAssertEqual(warehouse.entries().first?.input, 4321)
        XCTAssertNil(warehouse.ingestMark(path: url.path),
                     "the mark for a pruned transcript is dropped")
    }

    func testATranscriptRewrittenSmallerIsReadAgainFromTheStart() {
        let url = writeTranscript([Self.line(id: "a", ts: "2026-08-18T10:00:00.000Z"),
                                   Self.line(id: "b", ts: "2026-08-18T10:05:00.000Z")])
        store.ingest(into: warehouse)
        // Rewritten with different content, shorter than what was already read
        try? Data(Self.line(id: "c", ts: "2026-08-18T12:00:00.000Z").utf8 + [0x0A])
            .write(to: url)
        XCTAssertEqual(store.ingest(into: warehouse), 1)
        XCTAssertEqual(warehouse.entryCount, 3, "the earlier records are still held")
    }

    func testSyntheticModelsAndZeroUsageAreIgnored() {
        _ = writeTranscript([
            Self.line(id: "a", ts: "2026-08-18T10:00:00.000Z", model: "<synthetic>"),
            Self.line(id: "b", ts: "2026-08-18T10:01:00.000Z", input: 0, output: 0),
            Self.line(id: "c", ts: "2026-08-18T10:02:00.000Z"),
        ])
        XCTAssertEqual(store.ingest(into: warehouse), 1)
    }

    func testTailedAndWholeFileParsesAgree() {
        let lines = (0..<20).map {
            Self.line(id: "m\($0)", ts: "2026-08-18T10:\(String(format: "%02d", $0)):00.000Z")
        }
        _ = writeTranscript(lines)
        store.ingest(into: warehouse)
        let scanned = store.scan(lookbackDays: 3650,
                                 now: Date(timeIntervalSince1970: 1_787_000_000))
        XCTAssertEqual(warehouse.entryCount, scanned.count)
        XCTAssertEqual(warehouse.entries().reduce(0) { $0 + $1.input },
                       scanned.reduce(0) { $0 + $1.input })
    }
}
