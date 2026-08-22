// The settings catalogue is a contract with the Windows shell, which is written in another
// language and cannot be caught by the compiler. The fixture is real output from this engine,
// and both sides assert against the same file: this suite proves the engine still publishes
// what the fixture holds, and RedLine.Core.Tests proves C# reads the same thing.
//
// Add or rename a setting and this fails, saying to regenerate the fixture.
import XCTest
@testable import RedlineCore

final class SettingsContractTests: XCTestCase {
    private var fixture: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // RedlineCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // repo root
            .appendingPathComponent("windows/RedLine.Core.Tests/fixtures/config-settings.json")
    }

    /// A config file that is not there, so the catalogue reads the defaults rather than
    /// whatever the machine running the tests happens to have set.
    private var defaults: URL {
        URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-settings-contract-\(UUID().uuidString)")
            .appendingPathComponent("config.json")
    }

    private func fixtureRows() throws -> [[String: Any]] {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: fixture))
        let root = try XCTUnwrap(json as? [String: Any])
        return try XCTUnwrap(root["settings"] as? [[String: Any]])
    }

    func testThePublishedCatalogueIsStillWhatTheFixtureHolds() throws {
        let published = ConfigEditor.catalog(at: defaults)
        let recorded = try fixtureRows()
        XCTAssertEqual(NSArray(array: published), NSArray(array: recorded),
                       "the settings the Windows shell reads have moved; regenerate "
                       + "windows/RedLine.Core.Tests/fixtures/config-settings.json with "
                       + "REDLINE_HOME=<empty dir> redline config --json")
    }

    /// The words themselves are the contract: C# switches on these strings, and a rename
    /// there is a control that silently stops rendering.
    func testTheKindNamesAreTheOnesTheShellSwitchesOn() throws {
        let kinds = Set(try fixtureRows().compactMap { $0["kind"] as? String })
        XCTAssertEqual(kinds, ["bool", "number", "choice", "list"])
    }

    func testEveryRowCarriesWhatAControlNeeds() throws {
        for row in try fixtureRows() {
            let key = try XCTUnwrap(row["key"] as? String)
            let summary = try XCTUnwrap(row["summary"] as? String, key)
            XCTAssertFalse(summary.isEmpty, key)
            XCTAssertNotNil(row["value"] as? String, key)
            switch try XCTUnwrap(row["kind"] as? String, key) {
            case "number":
                let min = try XCTUnwrap(row["min"] as? Double, key)
                let max = try XCTUnwrap(row["max"] as? Double, key)
                XCTAssertLessThan(min, max, key)
            case "choice", "list":
                let allowed = try XCTUnwrap(row["allowed"] as? [String], key)
                XCTAssertFalse(allowed.isEmpty, key)
            default:
                break
            }
        }
    }

    /// Each answer a change can give, in the shape the shell reads. Only the engine knows
    /// what a value has to be, so the reason it refused one travels with the refusal.
    func testEveryOutcomeIsReportedInTheShapeTheShellReads() {
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.json(for: .changed(key: "limitYellowPct",
                                                                     from: "60", to: "70"))),
            ["outcome": "changed", "key": "limitYellowPct", "from": "60", "to": "70"])
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.json(for: .unchanged(key: "alerts",
                                                                       value: "true"))),
            ["outcome": "unchanged", "key": "alerts", "value": "true"])
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.json(for: .rejected(key: "limitYellowPct",
                                                                      reason: "a number"))),
            ["outcome": "rejected", "key": "limitYellowPct", "expected": "a number"])
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.json(for: .unknownKey("nosuch"))),
            ["outcome": "unknownKey", "key": "nosuch"])
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.json(for: .failed("could not write"))),
            ["outcome": "failed", "message": "could not write"])
        XCTAssertEqual(
            NSDictionary(dictionary: ConfigEditor.readJSON(key: "alerts", value: "true")),
            ["outcome": "read", "key": "alerts", "value": "true"])
    }

    /// The catalogue is what the CLI prints, so it has to survive being written as JSON.
    func testTheCatalogueSerialises() throws {
        let data = try JSONSerialization.data(withJSONObject:
            ["settings": ConfigEditor.catalog(at: defaults)])
        XCTAssertFalse(data.isEmpty)
    }
}
