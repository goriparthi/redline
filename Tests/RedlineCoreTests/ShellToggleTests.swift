// The shapes the Windows shell reads for the settings that are commands rather than values.
// Asserted literally, because C# switches on these words and a rename there is a control that
// stops working with nothing to say about it.
import XCTest
@testable import RedlineCore

final class ShellToggleTests: XCTestCase {
    func testAStateIsReportedWithoutSayingAnythingChanged() {
        XCTAssertEqual(
            NSDictionary(dictionary: ShellToggle.status(key: "autostart", on: true,
                                                        extras: ["name": "Run key"])),
            ["outcome": "status", "key": "autostart", "on": true, "name": "Run key"])
    }

    func testOnAndOffAreChangesAndSayingSoTwiceIsNot() {
        XCTAssertEqual(
            NSDictionary(dictionary: ShellToggle.changed(key: "usageFeed", on: true)),
            ["outcome": "changed", "key": "usageFeed", "on": true])
        XCTAssertEqual(
            NSDictionary(dictionary: ShellToggle.unchanged(key: "usageFeed", on: false)),
            ["outcome": "unchanged", "key": "usageFeed", "on": false])
    }

    func testAFailureCarriesTheReasonAndNoState() {
        let row = ShellToggle.failed(key: "usageFeed", message: "settings.json is read only")
        XCTAssertEqual(NSDictionary(dictionary: row),
                       ["outcome": "failed", "key": "usageFeed",
                        "message": "settings.json is read only"])
        // No "on": a failure has no state to report, and reporting one would move a switch
        XCTAssertNil(row["on"])
    }

    /// The keys are the contract too: the shell matches an answer to the control that asked.
    func testTheKeysAreTheOnesTheShellAsksBy() {
        XCTAssertEqual(ShellToggle.autostartKey, "autostart")
        XCTAssertEqual(ShellToggle.usageFeedKey, "usageFeed")
    }

    /// These are printed, so every shape has to survive being written as JSON.
    func testEveryShapeSerialises() throws {
        for row in [ShellToggle.status(key: "a", on: true, extras: ["name": "n"]),
                    ShellToggle.changed(key: "a", on: false),
                    ShellToggle.unchanged(key: "a", on: true),
                    ShellToggle.failed(key: "a", message: "why")] {
            XCTAssertFalse(try JSONSerialization.data(withJSONObject: row).isEmpty)
        }
    }
}
