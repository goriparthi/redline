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

final class VersionCompareTests: XCTestCase {
    // Mirrors Updates.isNewer, which lives in the app target and so cannot be imported here.
    private func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    func testNumericComparisonNotStringComparison() {
        XCTAssertTrue(isNewer("0.10.0", than: "0.9.0"),
                      "string comparison would call 0.10.0 older than 0.9.0")
        XCTAssertTrue(isNewer("1.0.0", than: "0.99.9"))
        XCTAssertFalse(isNewer("0.2.0", than: "0.2.0"))
        XCTAssertFalse(isNewer("0.1.9", than: "0.2.0"))
    }

    func testShorterVersionsPadWithZero() {
        XCTAssertTrue(isNewer("0.3", than: "0.2.9"))
        XCTAssertFalse(isNewer("0.2", than: "0.2.0"))
    }
}
