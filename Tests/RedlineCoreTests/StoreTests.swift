// Exercises the on-disk scanners against synthetic transcripts in a temp dir.
import XCTest
@testable import RedlineCore

private func makeTempDir() throws -> URL {
    let dir = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("redline-tests-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}

final class CodexStoreTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = try makeTempDir()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func write(_ lines: [String], to name: String = "rollout.jsonl") throws {
        try lines.joined(separator: "\n")
            .write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8)
    }

    // resetsAt defaults far into the future so windows survive the unexpired() filter;
    // tests that care about rollover pass a past value explicitly.
    private func tokenCount(ts: String, pct: Double, lastInput: Int, lastCached: Int,
                            lastOutput: Int, reasoning: Int = 0,
                            resetsAt: Double = 4_000_000_000) -> String {
        let payload: [String: Any] = [
            "type": "token_count",
            "rate_limits": [
                "primary": ["used_percent": pct, "window_minutes": 300,
                            "resets_at": resetsAt],
            ],
            "info": [
                "last_token_usage": [
                    "input_tokens": lastInput,
                    "cached_input_tokens": lastCached,
                    "output_tokens": lastOutput,
                    "reasoning_output_tokens": reasoning,
                ],
            ],
        ]
        let obj: [String: Any] = ["type": "event_msg", "timestamp": ts, "payload": payload]
        let data = try! JSONSerialization.data(withJSONObject: obj)
        return String(data: data, encoding: .utf8)!
    }

    func testParsesTokensAndSplitsCachedOutOfInput() throws {
        let now = Date()
        let ts = ISO8601DateFormatter().string(from: now)
        try write([tokenCount(ts: ts, pct: 3, lastInput: 1000, lastCached: 900,
                              lastOutput: 50, reasoning: 5)])
        let snap = CodexStore(root: dir).scan(lookbackDays: 7, now: now)
        XCTAssertEqual(snap.entries.count, 1)
        let e = snap.entries[0]
        XCTAssertEqual(e.provider, "Codex")
        XCTAssertEqual(e.input, 100, "cached tokens must not be billed as fresh input")
        XCTAssertEqual(e.cacheRead, 900)
        XCTAssertEqual(e.output, 55, "reasoning tokens count as output")
    }

    func testSumsPerTurnDeltasRatherThanCumulativeTotals() throws {
        let now = Date()
        let f = ISO8601DateFormatter()
        try write([
            tokenCount(ts: f.string(from: now.addingTimeInterval(-60)),
                       pct: 1, lastInput: 100, lastCached: 0, lastOutput: 10),
            tokenCount(ts: f.string(from: now),
                       pct: 2, lastInput: 200, lastCached: 0, lastOutput: 20),
        ])
        let snap = CodexStore(root: dir).scan(lookbackDays: 7, now: now)
        let io = snap.entries.reduce(0) { $0 + $1.input + $1.output }
        XCTAssertEqual(io, 330, "each event is a delta, so they add rather than replace")
    }

    func testLimitsComeFromNewestEvent() throws {
        let now = Date()
        let f = ISO8601DateFormatter()
        try write([
            tokenCount(ts: f.string(from: now.addingTimeInterval(-600)),
                       pct: 11, lastInput: 1, lastCached: 0, lastOutput: 1),
            tokenCount(ts: f.string(from: now),
                       pct: 77, lastInput: 1, lastCached: 0, lastOutput: 1),
        ])
        let snap = CodexStore(root: dir).scan(lookbackDays: 7, now: now)
        XCTAssertEqual(snap.limits.first?.utilization, 77)
    }

    func testDropsLimitWindowsThatAlreadyReset() throws {
        // A 5-hour window that reset long ago must not be shown as current, which is exactly
        // what reading days-old Codex sessions off disk would otherwise do.
        let now = Date()
        let ts = ISO8601DateFormatter().string(from: now)
        try write([tokenCount(ts: ts, pct: 88, lastInput: 10, lastCached: 0, lastOutput: 1,
                              resetsAt: now.timeIntervalSince1970 - 3600)])
        let snap = CodexStore(root: dir).scan(lookbackDays: 7, now: now)
        XCTAssertTrue(snap.limits.isEmpty, "an expired window is meaningless, not just stale")
        XCTAssertEqual(snap.entries.count, 1, "token totals stay valid regardless")
    }

    func testIgnoresEventsOutsideLookback() throws {
        let now = Date()
        let old = ISO8601DateFormatter().string(from: now.addingTimeInterval(-40 * 86400))
        try write([tokenCount(ts: old, pct: 5, lastInput: 10, lastCached: 0, lastOutput: 10)])
        XCTAssertTrue(CodexStore(root: dir).scan(lookbackDays: 7, now: now).entries.isEmpty)
    }

    func testMissingDirectoryIsNotAnError() {
        let missing = dir.appendingPathComponent("nope")
        let snap = CodexStore(root: missing).scan(lookbackDays: 7)
        XCTAssertTrue(snap.entries.isEmpty)
        XCTAssertTrue(snap.limits.isEmpty)
    }

    func testSkipsMalformedLines() throws {
        let ts = ISO8601DateFormatter().string(from: Date())
        try write(["{not json", "", tokenCount(ts: ts, pct: 1, lastInput: 10,
                                               lastCached: 0, lastOutput: 1)])
        XCTAssertEqual(CodexStore(root: dir).scan(lookbackDays: 7).entries.count, 1)
    }
}

final class OllamaStoreTests: XCTestCase {
    private var dir: URL!
    private var log: URL!

    override func setUpWithError() throws {
        dir = try makeTempDir()
        log = dir.appendingPathComponent("ollama.jsonl")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testParsesEvalCounts() throws {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = #"{"ts":"\#(ts)","model":"qwen3-coder:30b","prompt_eval_count":1200,"eval_count":340}"#
        try line.write(to: log, atomically: true, encoding: .utf8)
        let out = OllamaStore(log: log).scan(lookbackDays: 7)
        XCTAssertEqual(out.count, 1)
        XCTAssertEqual(out[0].provider, "Ollama")
        XCTAssertEqual(out[0].input, 1200)
        XCTAssertEqual(out[0].output, 340)
        XCTAssertEqual(out[0].model, "qwen3-coder:30b")
    }

    func testLocalUsageStaysUnpricedSoItIsNotCountedAsSpend() throws {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = #"{"ts":"\#(ts)","model":"qwen3-coder:30b","prompt_eval_count":10,"eval_count":5}"#
        try line.write(to: log, atomically: true, encoding: .utf8)
        let entries = OllamaStore(log: log).scan(lookbackDays: 7)
        let a = aggregate(entries, since: Date().addingTimeInterval(-3600), config: Config())
        XCTAssertEqual(a.io, 15)
        XCTAssertEqual(a.cost, 0, "local inference has no dollar cost")
        XCTAssertTrue(a.hasUnpriced)
    }

    func testMissingLogIsNotAnError() {
        let store = OllamaStore(log: dir.appendingPathComponent("absent.jsonl"))
        XCTAssertFalse(store.isConfigured)
        XCTAssertTrue(store.scan(lookbackDays: 7).isEmpty)
    }

    func testZeroTokenCallsAreSkipped() throws {
        let ts = ISO8601DateFormatter().string(from: Date())
        let line = #"{"ts":"\#(ts)","model":"m","prompt_eval_count":0,"eval_count":0}"#
        try line.write(to: log, atomically: true, encoding: .utf8)
        XCTAssertTrue(OllamaStore(log: log).scan(lookbackDays: 7).isEmpty)
    }
}

final class ClaudeStoreTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = try makeTempDir()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func line(id: String, requestId: String, ts: String, model: String,
                      input: Int, output: Int, cacheRead: Int = 0) -> String {
        let obj: [String: Any] = [
            "timestamp": ts,
            "requestId": requestId,
            "message": [
                "id": id,
                "model": model,
                "usage": [
                    "input_tokens": input,
                    "output_tokens": output,
                    "cache_read_input_tokens": cacheRead,
                ],
            ],
        ]
        return String(data: try! JSONSerialization.data(withJSONObject: obj), encoding: .utf8)!
    }

    func testDedupesRepeatedMessageIdsAcrossFiles() throws {
        let now = Date()
        let ts = ISO8601DateFormatter().string(from: now)
        let l = line(id: "msg_1", requestId: "req_1", ts: ts,
                     model: "claude-sonnet-5", input: 100, output: 10)
        // A resumed session copies identical ids into a second transcript
        for name in ["a.jsonl", "b.jsonl"] {
            try l.write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8)
        }
        let out = UsageStore(root: dir).scan(lookbackDays: 7, now: now)
        XCTAssertEqual(out.count, 1, "the same message must not be counted twice")
    }

    func testWideningTheRangeRereadsCachedFiles() throws {
        let now = Date()
        let iso = ISO8601DateFormatter()
        let body = [line(id: "m1", requestId: "r1",
                         ts: iso.string(from: now.addingTimeInterval(-3 * 86400)),
                         model: "claude-sonnet-5", input: 10, output: 1),
                    line(id: "m2", requestId: "r2",
                         ts: iso.string(from: now.addingTimeInterval(-20 * 86400)),
                         model: "claude-sonnet-5", input: 20, output: 2)]
            .joined(separator: "\n")
        try body.write(to: dir.appendingPathComponent("a.jsonl"), atomically: true,
                       encoding: .utf8)
        // One store across both scans, which is what the dashboard does on a range change
        let store = UsageStore(root: dir)
        XCTAssertEqual(store.scan(lookbackDays: 7, now: now).count, 1)
        XCTAssertEqual(store.scan(lookbackDays: 30, now: now).count, 2,
                       "widening the range must re-read files cached for a narrower one")
        XCTAssertEqual(store.scan(lookbackDays: 7, now: now).count, 1,
                       "narrowing again must not leak the wider window's entries")
    }

    func testSkipsSyntheticModel() throws {
        let ts = ISO8601DateFormatter().string(from: Date())
        try line(id: "m", requestId: "r", ts: ts, model: "<synthetic>", input: 5, output: 5)
            .write(to: dir.appendingPathComponent("s.jsonl"), atomically: true, encoding: .utf8)
        XCTAssertTrue(UsageStore(root: dir).scan(lookbackDays: 7).isEmpty)
    }

    func testMissingDirectoryIsNotAnError() {
        XCTAssertTrue(UsageStore(root: dir.appendingPathComponent("nope"))
            .scan(lookbackDays: 7).isEmpty)
    }
}
