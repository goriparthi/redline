import XCTest
@testable import RedlineCore

final class AvailabilityTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-home-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func make(_ path: String) throws {
        try FileManager.default.createDirectory(at: home.appendingPathComponent(path),
                                                withIntermediateDirectories: true)
    }

    func testNothingInstalledIsDetectedAsEmpty() {
        let a = ProviderAvailability.detect(home: home)
        XCTAssertTrue(a.isEmpty)
        XCTAssertFalse(a.hasChoice)
    }

    // A claude.ai user with no CLI has nothing under ~/.claude, but a signed-in account
    // still has rate limits worth showing
    func testClaudeAccountCountsWithNothingLocal() {
        let a = ProviderAvailability.detect(home: home, claudeAccount: true)
        XCTAssertTrue(a.has("Claude"))
        XCTAssertFalse(a.has("Codex"))
    }

    func testNoClaudeAccountAndNothingLocalMeansNoClaude() {
        let a = ProviderAvailability.detect(home: home, claudeAccount: false)
        XCTAssertFalse(a.has("Claude"))
    }

    func testSingleProviderNeedsNoAllOption() throws {
        try make(".codex/sessions")
        let a = ProviderAvailability.detect(home: home)
        XCTAssertEqual(a.installed, ["Codex"])
        XCTAssertFalse(a.hasChoice, "one track means there is nothing to choose between")
        XCTAssertEqual(a.trackChoices, ["Codex"],
                       "an 'all providers' entry would be meaningless here")
    }

    func testSeveralProvidersKeepTheAllOption() throws {
        try make(".claude/projects")
        try make(".codex/sessions")
        let a = ProviderAvailability.detect(home: home)
        XCTAssertEqual(a.installed, ["Claude", "Codex"], "canonical order, not filesystem order")
        XCTAssertTrue(a.hasChoice)
        XCTAssertEqual(a.trackChoices.first, Config.autoProvider)
        XCTAssertEqual(a.trackChoices.count, 3)
    }

    func testOllamaCountsWhenRunningEvenWithNoDataDirectory() {
        let a = ProviderAvailability.detect(home: home, ollamaReachable: true)
        XCTAssertEqual(a.installed, ["Ollama"],
                       "a fresh Ollama install may not have written anything yet")
    }

    func testOllamaCountsFromTheWrapperLogAlone() throws {
        try make(".local/share/redline")
        FileManager.default.createFile(
            atPath: home.appendingPathComponent(".local/share/redline/ollama.jsonl").path,
            contents: Data())
        XCTAssertTrue(ProviderAvailability.detect(home: home).has("Ollama"))
    }

    func testMatchingIsCaseInsensitive() throws {
        try make(".claude")
        XCTAssertTrue(ProviderAvailability.detect(home: home).has("claude"))
    }
}

final class FirstRunTests: XCTestCase {
    func testFirstRunIsTrueUntilAConfigExists() throws {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-fr-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        XCTAssertTrue(Config.isFirstRun(at: url))
        try Data("{}".utf8).write(to: url)
        XCTAssertFalse(Config.isFirstRun(at: url), "setup must not reappear on every launch")
    }
}
