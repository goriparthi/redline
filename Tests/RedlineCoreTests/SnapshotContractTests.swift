// The snapshot is a contract with the Windows shell, which is written in another language and
// cannot be caught by the compiler. The fixture is real output from this engine, and both
// sides assert against the same file: this suite proves the engine still reads what it wrote,
// and RedLine.Core.Tests proves C# reads the same thing.
//
// If a field is renamed here, this fails first and says so.
import XCTest
@testable import RedlineCore

final class SnapshotContractTests: XCTestCase {
    private var fixture: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // RedlineCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // repo root
            .appendingPathComponent("windows/RedLine.Core.Tests/fixtures/snapshot-headless.json")
    }

    private func load() throws -> Snapshot {
        try XCTUnwrap(SnapshotStore.read(from: fixture),
                      "the engine can no longer read its own published format")
    }

    func testTheEngineStillReadsItsOwnPublishedFormat() throws {
        let snapshot = try load()
        XCTAssertEqual(snapshot.limits.count, 3)
        XCTAssertNotNil(snapshot.claudeLimitsAsOf)
    }

    /// Codex publishes a window with no reset time. Losing it is a real regression: it used
    /// to vanish from the snapshot after the first incremental pass had consumed the file.
    func testTheCodexWindowIsPresentAndHasNoReset() throws {
        let snapshot = try load()
        let codex = try XCTUnwrap(snapshot.limits.first { $0.provider == "Codex" },
                                  "the Codex window is missing from the published snapshot")
        XCTAssertEqual(codex.key, "five_hour")
        XCTAssertEqual(codex.utilization, 31)
        XCTAssertNil(codex.resetsAt)
    }

    func testPerProviderTotalsAddUpToTheWhole() throws {
        let snapshot = try load()
        XCTAssertEqual(snapshot.today.io, 13_740)
        XCTAssertEqual(snapshot.todayByProvider?["Claude"]?.io, 12_900)
        XCTAssertEqual(snapshot.todayByProvider?["Codex"]?.io, 840)
        XCTAssertEqual(snapshot.todayByProvider?.values.reduce(0) { $0 + $1.io },
                       snapshot.today.io)
    }

    /// An unpriced model makes the cost a floor rather than an answer, and this flag is all
    /// that keeps a shell from presenting it as fact.
    func testTheUnpricedFlagSurvivesTheRoundTrip() throws {
        XCTAssertEqual(try load().today.hasUnpriced, true)
    }

    /// The field names themselves are the contract, so they are asserted literally rather
    /// than only through a successful decode.
    func testTheKeysTheWindowsShellReadsAreStillTheKeysWeWrite() throws {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: fixture))
        let root = try XCTUnwrap(json as? [String: Any])
        for key in ["updatedAt", "limits", "today", "week",
                    "todayByProvider", "weekByProvider", "claudeLimitsAsOf"] {
            XCTAssertNotNil(root[key], "the shell reads \(key) and it is gone")
        }
        let window = try XCTUnwrap((root["limits"] as? [[String: Any]])?.first)
        for key in ["provider", "key", "utilization"] {
            XCTAssertNotNil(window[key], "the shell reads limits[].\(key) and it is gone")
        }
        let totals = try XCTUnwrap(root["today"] as? [String: Any])
        for key in ["io", "cost", "hasUnpriced"] {
            XCTAssertNotNil(totals[key], "the shell reads today.\(key) and it is gone")
        }
    }
}
