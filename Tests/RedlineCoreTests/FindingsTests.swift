// The findings checks, and the transcript reading behind them. The assertions to keep an eye
// on are the ones about figures: a finding may report no saving, and must never report one it
// cannot support.
import XCTest
@testable import RedlineCore

final class FindingsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_760_000_000)
    private let config = Config()

    private func tool(_ name: String, file: String? = nil, subject: String? = nil,
                      id: String? = nil) -> ToolUse {
        ToolUse(id: id, name: name, filePath: file, subject: subject, ts: now)
    }

    private func session(tools: [ToolUse], results: [String: Int] = [:],
                         model: String? = "claude-sonnet-5", cwd: String? = "/tmp/project",
                         commands: [String] = []) -> SessionScan {
        SessionScan(path: "/tmp/session-\(UUID().uuidString).jsonl", cwd: cwd,
                    lastModel: model, start: now, end: now, tools: tools,
                    resultChars: results, commandNames: commands)
    }

    private func input(_ sessions: [SessionScan], mcp: [String] = [], skills: [String] = [],
                       agents: [String] = [], commands: [String] = [],
                       memory: [MemoryFile] = []) -> FindingsInput {
        FindingsInput(sessions: sessions, configuredMCPServers: mcp, skills: skills,
                      agents: agents, commands: commands, memoryFiles: memory,
                      windowDays: 14, now: now)
    }

    // MARK: MCP

    func testUnusedMCPServerIsReportedWithoutAFabricatedSaving() throws {
        let used = session(tools: [tool("mcp__atlassian__search")])
        let report = Findings.report(input([used], mcp: ["atlassian", "playwright"]),
                                     config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "mcp-unused" })
        XCTAssertEqual(finding.evidence.map(\.label), ["playwright"])
        XCTAssertNil(finding.estimatedUSD,
                     "the schema overhead is real but not visible from here")
        XCTAssertEqual(finding.basis, .measured)
    }

    func testServerNameSpellingDifferencesStillCountAsUsed() {
        let used = session(tools: [tool("mcp__chrome_devtools__click")])
        let report = Findings.report(input([used], mcp: ["chrome-devtools"]), config: config)
        XCTAssertFalse(report.findings.contains { $0.id == "mcp-unused" })
    }

    func testNoSessionsMeansNoMCPClaim() {
        let report = Findings.report(input([], mcp: ["atlassian"]), config: config)
        XCTAssertTrue(report.findings.isEmpty,
                      "with nothing scanned, 'never used' would be a claim about nothing")
    }

    // MARK: re-reads

    func testRepeatedReadsAreCountedFromTheSecondReadOn() throws {
        let reads = (1...4).map { tool("Read", file: "/tmp/a.ts", id: "r\($0)") }
        let chars = ["r1": 8000, "r2": 8000, "r3": 8000, "r4": 8000]
        let report = Findings.report(input([session(tools: reads, results: chars)]),
                                     config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "reread-files" })
        // Three extra reads of 8000 characters, at four characters per token
        XCTAssertEqual(finding.estimatedTokens, 6000)
        XCTAssertEqual(finding.basis, .estimated)
        XCTAssertNotNil(finding.estimatedUSD)
    }

    func testTwoReadsIsNotAPattern() {
        let reads = [tool("Read", file: "/tmp/a.ts", id: "r1"),
                     tool("Read", file: "/tmp/a.ts", id: "r2")]
        let report = Findings.report(
            input([session(tools: reads, results: ["r1": 9000, "r2": 9000])]), config: config)
        XCTAssertFalse(report.findings.contains { $0.id == "reread-files" })
    }

    func testUnpricedModelLeavesTheDollarFigureOff() throws {
        let reads = (1...3).map { tool("Read", file: "/tmp/a.ts", id: "r\($0)") }
        let report = Findings.report(
            input([session(tools: reads, results: ["r1": 4000, "r2": 4000, "r3": 4000],
                           model: "some-unlisted-model")]), config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "reread-files" })
        XCTAssertNotNil(finding.estimatedTokens)
        XCTAssertNil(finding.estimatedUSD)
    }

    // MARK: memory files

    func testHeavyMemoryFileIsReported() throws {
        let report = Findings.report(
            input([session(tools: [])],
                  memory: [MemoryFile(path: "/Users/x/.claude/CLAUDE.md", chars: 40_000,
                                      sessions: 1, global: true)]), config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "memory-heavy" })
        XCTAssertEqual(finding.estimatedTokens, 10_000)
        XCTAssertEqual(finding.kind, .fyi)
    }

    func testAProjectFileIsChargedOnlyToItsOwnSessions() throws {
        // Two files of the same size, one loaded eight times as often. Pricing both against
        // every session on the machine is how a project's CLAUDE.md gets blamed for the lot.
        let sessions = (0..<8).map { _ in session(tools: []) }
        let report = Findings.report(
            input(sessions, memory: [
                MemoryFile(path: "/Users/x/.claude/CLAUDE.md", chars: 40_000,
                           sessions: 8, global: true),
                MemoryFile(path: "/repo/CLAUDE.md", chars: 40_000, sessions: 1, global: false),
            ]), config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "memory-heavy" })
        let usd = try XCTUnwrap(finding.estimatedUSD)
        // 10k tokens at Sonnet input x1.25, nine session loads between the two files
        XCTAssertEqual(usd, 10_000.0 / 1_000_000 * 3 * 1.25 * 9, accuracy: 0.0001)
        XCTAssertTrue(finding.evidence.contains { $0.value?.contains("every project") == true })
    }

    func testSmallMemoryFileIsNotWorthMentioning() {
        let report = Findings.report(
            input([session(tools: [])],
                  memory: [MemoryFile(path: "/Users/x/.claude/CLAUDE.md", chars: 1_000,
                                      sessions: 1, global: true)]), config: config)
        XCTAssertFalse(report.findings.contains { $0.id == "memory-heavy" })
    }

    // MARK: ghosts

    func testUnusedSkillsAgentsAndCommandsAreListed() throws {
        let s = session(tools: [tool("Skill", subject: "pg-voice"),
                                tool("Task", subject: "Explore")],
                        commands: ["scrum"])
        let report = Findings.report(
            input([s], skills: ["pg-voice", "who-am-i"], agents: ["Explore", "Plan"],
                  commands: ["scrum", "deploy"]), config: config)
        let finding = try XCTUnwrap(report.findings.first { $0.id == "ghost-definitions" })
        XCTAssertEqual(finding.evidence.map(\.label), ["who-am-i", "Plan", "deploy"])
        XCTAssertEqual(finding.evidence.map(\.value), ["skill", "agent", "command"])
        XCTAssertNil(finding.estimatedUSD)
    }

    func testASkillInvokedAsASlashCommandCountsAsUsed() {
        let s = session(tools: [], commands: ["pg-voice"])
        let report = Findings.report(input([s], skills: ["pg-voice"]), config: config)
        XCTAssertFalse(report.findings.contains { $0.id == "ghost-definitions" })
    }

    // MARK: transcript parsing

    func testParsesToolUsesResultsAndCwd() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-findings-\(UUID().uuidString)/project")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir.deletingLastPathComponent()) }
        let file = dir.appendingPathComponent("session.jsonl")
        let lines = [
            """
            {"type":"assistant","cwd":"/Users/x/repo","timestamp":"2026-08-18T10:00:00.000Z",\
            "message":{"model":"claude-opus-5","content":[{"type":"tool_use","id":"t1",\
            "name":"Read","input":{"file_path":"/Users/x/repo/a.ts"}}]}}
            """,
            """
            {"type":"user","timestamp":"2026-08-18T10:00:01.000Z","message":{"content":\
            [{"type":"tool_result","tool_use_id":"t1","content":"0123456789"}]}}
            """,
            """
            {"type":"assistant","timestamp":"2026-08-18T10:00:02.000Z","message":\
            {"model":"claude-opus-5","content":[{"type":"tool_use","id":"t2",\
            "name":"mcp__atlassian__search","input":{}}]}}
            """,
        ]
        try lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)

        let scanner = TranscriptScanner(root: dir.deletingLastPathComponent())
        let sessions = scanner.scan(lookbackDays: 30, now: Date())
        let scan = try XCTUnwrap(sessions.first)
        XCTAssertEqual(scan.cwd, "/Users/x/repo")
        XCTAssertEqual(scan.lastModel, "claude-opus-5")
        XCTAssertEqual(scan.tools.count, 2)
        XCTAssertEqual(scan.tools.first?.filePath, "/Users/x/repo/a.ts")
        XCTAssertEqual(scan.resultChars["t1"], 10)
    }

    func testCommandTagsAreExtracted() {
        let body = "<command-message>x</command-message><command-name>/scrum</command-name>"
        XCTAssertEqual(TranscriptScanner.commandNames(in: body), ["scrum"])
    }

    func testSummaryCountsWhatItSays() {
        let report = FindingsReport(generatedAt: now, windowDays: 14, sessionsScanned: 3,
                                    findings: [
                                        Finding(id: "a", kind: .fixNow, basis: .measured,
                                                title: "t", detail: "d"),
                                        Finding(id: "b", kind: .fyi, basis: .measured,
                                                title: "t", detail: "d"),
                                    ])
        XCTAssertEqual(report.summary, "2 findings · 1 to fix")
    }
}
