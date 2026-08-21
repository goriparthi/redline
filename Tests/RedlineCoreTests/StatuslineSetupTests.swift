// Installing the feed writes into a file Claude Code owns, so the property that matters most
// is that it never destroys what was already there.
import XCTest
@testable import RedlineCore

final class StatuslineSetupTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-setup-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func settings() throws -> [String: Any] {
        let data = try Data(contentsOf: StatuslineSetup.settingsURL(home: home))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func writeSettings(_ object: [String: Any]) throws {
        let url = StatuslineSetup.settingsURL(home: home)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: object).write(to: url)
    }

    private func command() throws -> String {
        try XCTUnwrap((try settings()["statusLine"] as? [String: Any])?["command"] as? String)
    }

    // MARK: - The embedded copy

    /// The feeders are embedded so a machine that never saw this repository can still install
    /// one. That only works while the copy matches the script it was taken from.
    func testTheEmbeddedFeedersMatchTheScriptsOnDisk() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        for (embedded, name) in [(StatuslineScripts.posix, "claude-statusline.sh"),
                                 (StatuslineScripts.windows, "claude-statusline.ps1")] {
            let onDisk = try String(contentsOf: root.appendingPathComponent("scripts/\(name)"),
                                    encoding: .utf8)
            XCTAssertEqual(embedded, onDisk,
                           "\(name) has drifted; run scripts/embed-statusline.sh")
        }
    }

    // MARK: - Installing

    func testInstallWritesTheScriptAndPointsSettingsAtIt() throws {
        guard case let .installed(script, chained) = StatuslineSetup.install(home: home) else {
            return XCTFail("install did not report success")
        }
        XCTAssertNil(chained, "nothing was configured, so nothing should have been chained")
        XCTAssertTrue(FileManager.default.fileExists(atPath: script.path))
        let written = try command()
        XCTAssertTrue(written.contains(StatuslineSetup.scriptName), written)
        XCTAssertTrue(StatuslineSetup.isInstalled(home: home))
        XCTAssertTrue(StatuslineSetup.isWanted(home: home))
    }

    /// Someone else's statusline is not ours to delete. It has to keep drawing.
    func testAnExistingStatuslineIsCarriedForwardRatherThanReplaced() throws {
        try writeSettings(["statusLine": ["type": "command", "command": "~/bin/my-prompt.sh"]])

        guard case let .installed(_, chained) = StatuslineSetup.install(home: home) else {
            return XCTFail("install did not report success")
        }
        XCTAssertEqual(chained, "~/bin/my-prompt.sh")
        let written = try command()
        XCTAssertTrue(written.contains("my-prompt.sh"), "the existing command was lost: \(written)")
        XCTAssertEqual(StatuslineSetup.chainedCommand(in: written), "~/bin/my-prompt.sh")
    }

    /// Everything else in that file belongs to Claude Code and must survive untouched.
    func testUnrelatedSettingsAreLeftAlone() throws {
        try writeSettings(["model": "opus", "permissions": ["allow": ["Bash(ls:*)"]]])
        _ = StatuslineSetup.install(home: home)
        let after = try settings()
        XCTAssertEqual(after["model"] as? String, "opus")
        XCTAssertNotNil(after["permissions"])
    }

    /// Re-running is how the feeder gets updated, so it must not chain to itself and recurse
    /// on every draw.
    func testInstallingTwiceDoesNotWrapItself() throws {
        _ = StatuslineSetup.install(home: home)
        let first = try command()
        let outcome = StatuslineSetup.install(home: home)
        XCTAssertEqual(outcome, .alreadyInstalled(script: StatuslineSetup.scriptURL(home: home)))
        let written = try command()
        XCTAssertEqual(written, first)
        let occurrences = written.components(separatedBy: StatuslineSetup.scriptName).count - 1
        XCTAssertEqual(occurrences, 1, "the wrapper wrapped itself: \(written)")
    }

    func testTheScriptOnDiskIsTheRealFeeder() throws {
        _ = StatuslineSetup.install(home: home)
        let text = try String(contentsOf: StatuslineSetup.scriptURL(home: home), encoding: .utf8)
        XCTAssertTrue(text.contains("rate_limits"), "that is not the feeder")
        XCTAssertTrue(text.contains("REDLINE_CLAUDE_USAGE"))
    }

    // MARK: - Removing

    func testUninstallPutsTheOriginalStatuslineBack() throws {
        try writeSettings(["statusLine": ["type": "command", "command": "~/bin/my-prompt.sh"]])
        _ = StatuslineSetup.install(home: home)

        XCTAssertEqual(StatuslineSetup.uninstall(home: home), .removed)
        XCTAssertEqual(try command(), "~/bin/my-prompt.sh",
                       "the statusline we wrapped was not restored")
        XCTAssertFalse(StatuslineSetup.isWanted(home: home))
    }

    func testUninstallWithNothingChainedRemovesTheEntry() throws {
        _ = StatuslineSetup.install(home: home)
        XCTAssertEqual(StatuslineSetup.uninstall(home: home), .removed)
        XCTAssertNil(try settings()["statusLine"])
    }

    func testUninstallingWhenNothingIsInstalledIsHarmless() {
        XCTAssertEqual(StatuslineSetup.uninstall(home: home), .notInstalled)
    }

    /// A statusline someone set after we installed is theirs, not ours to remove.
    func testUninstallLeavesSomeoneElsesStatuslineAlone() throws {
        _ = StatuslineSetup.install(home: home)
        try writeSettings(["statusLine": ["type": "command", "command": "~/bin/theirs.sh"]])
        XCTAssertEqual(StatuslineSetup.uninstall(home: home), .notInstalled)
        XCTAssertEqual(try command(), "~/bin/theirs.sh")
    }
}
