// The feeder script itself, run the way Claude Code runs it. StatuslineFeedTests covers the
// parser against a payload typed by hand; this covers the half that writes the file.
import XCTest
@testable import RedlineCore

// The feeder is a bash script, so this whole suite is POSIX only. Windows needs a PowerShell
// equivalent before any of it can run there.
#if !os(Windows)

final class StatuslineFeederTests: XCTestCase {
    private var dir: URL!
    private var out: URL!

    private static let script = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // RedlineCoreTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // repo root
        .appendingPathComponent("scripts/claude-statusline.sh")

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-feeder-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        out = dir.appendingPathComponent("claude-usage.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    /// Runs the script over one statusline payload and returns the sidecar afterwards, nil
    /// while no sidecar exists.
    @discardableResult
    private func draw(_ payload: String) throws -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [Self.script.path]
        process.environment = [
            "REDLINE_CLAUDE_USAGE": out.path,
            "HOME": dir.path,
            "PATH": ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin",
        ]
        let stdin = Pipe()
        process.standardInput = stdin
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        stdin.fileHandleForWriting.write(Data(payload.utf8))
        try stdin.fileHandleForWriting.close()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0, "the statusline must never fail a draw")
        return try? String(contentsOf: out, encoding: .utf8)
    }

    private func windows(_ sidecar: String?) throws -> [LimitWindow] {
        let text = try XCTUnwrap(sidecar)
        return try XCTUnwrap(StatuslineFeed.parse(data: Data(text.utf8))).windows
    }

    private let live = """
    {"session_id":"s","cwd":"/tmp","rate_limits":{
      "five_hour":{"used_percentage":42,"resets_at":4102444800},
      "seven_day":{"used_percentage":7,"resets_at":4102444800}}}
    """
    private let windowless = """
    {"session_id":"s","rate_limits":{"five_hour":null,"seven_day":null,"model_scoped":null}}
    """

    /// The whole point of the file: what the script writes is what the parser reads.
    func testAPayloadWithWindowsRoundTripsThroughTheParser() throws {
        let parsed = try windows(draw(live))
        XCTAssertEqual(parsed.map(\.key), ["five_hour", "seven_day"])
        XCTAssertEqual(parsed[0].utilization, 42)
        XCTAssertEqual(parsed[1].utilization, 7)
        XCTAssertEqual(parsed[0].provider, "Claude")
    }

    /// The regression. Claude Code sends rate_limits with every window null on draws that made
    /// no API call, and writing that blanked the menu until the next real reading.
    func testAWindowlessPayloadLeavesTheLastReadingAlone() throws {
        let good = try XCTUnwrap(draw(live))
        XCTAssertEqual(try draw(windowless), good)
        XCTAssertEqual(try windows(draw(windowless)).count, 2)
    }

    func testAPayloadWithoutRateLimitsLeavesTheLastReadingAlone() throws {
        let good = try XCTUnwrap(draw(live))
        XCTAssertEqual(try draw(#"{"session_id":"s"}"#), good)
    }

    /// Nothing to report and nothing reported before: a sidecar that never appears reads as
    /// "no reading yet", where an all-null one read as a broken source.
    func testAWindowlessPayloadWritesNoSidecarAtAll() throws {
        XCTAssertNil(try draw(windowless))
        XCTAssertNil(try draw(#"{"rate_limits":{"five_hour":null,"model_scoped":[]}}"#))
        XCTAssertFalse(FileManager.default.fileExists(atPath: out.path))
    }

    /// Model-scoped weeks arrive without the other two on some plans, and they are a reading.
    func testModelScopedAloneCountsAsAReading() throws {
        let sidecar = try draw("""
        {"rate_limits":{"five_hour":null,"seven_day":null,
          "model_scoped":[{"display_name":"Fable","utilization":12}]}}
        """)
        XCTAssertEqual(try windows(sidecar).map(\.key), ["seven_day_fable"])
    }

    /// A payload the script cannot make sense of must not take the previous reading with it.
    func testMalformedPayloadsLeaveTheLastReadingAlone() throws {
        let good = try XCTUnwrap(draw(live))
        XCTAssertEqual(try draw("not json{"), good)
        XCTAssertEqual(try draw(#"{"rate_limits":"nope"}"#), good)
        XCTAssertEqual(try draw(""), good)
    }
}
#endif
