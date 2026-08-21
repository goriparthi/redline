// Changing settings from outside the app. The rule being protected is that the engine's own
// validation decides what is legal, so a shell cannot write a value the engine would refuse
// to load and then wonder why nothing happened.
import XCTest
@testable import RedlineCore

final class ConfigEditorTests: XCTestCase {
    private var url: URL!

    override func setUpWithError() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-config-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("config.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
    }

    private func raw() throws -> [String: Any] {
        let data = try Data(contentsOf: url)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    // MARK: - Setting

    func testANumberIsStoredAndReadBack() {
        XCTAssertEqual(ConfigEditor.set("limitYellowPct", to: "70", at: url),
                       .changed(key: "limitYellowPct", from: "60", to: "70"))
        XCTAssertEqual(ConfigEditor.value(of: "limitYellowPct", at: url), "70")
    }

    func testABooleanAcceptsTheWordsPeopleActuallyType() {
        for word in ["false", "no", "off", "0"] {
            _ = ConfigEditor.set("alerts", to: "true", at: url)
            XCTAssertEqual(ConfigEditor.set("alerts", to: word, at: url),
                           .changed(key: "alerts", from: "true", to: "false"),
                           "\(word) was not understood")
        }
    }

    func testAListIsMatchedCaseInsensitivelyAndDeduplicated() {
        XCTAssertEqual(ConfigEditor.set("providers", to: "claude, CODEX, claude", at: url),
                       .changed(key: "providers", from: "Claude,Codex,Ollama", to: "Claude,Codex"))
    }

    func testSettingSomethingToWhatItAlreadyIsChangesNothing() {
        XCTAssertEqual(ConfigEditor.set("alerts", to: "true", at: url),
                       .unchanged(key: "alerts", value: "true"))
    }

    // MARK: - Refusing

    /// The engine clamps or rejects on load, and this must refuse the same values rather than
    /// writing something that silently will not take.
    func testAValueTheEngineWouldRefuseIsNotWritten() throws {
        _ = ConfigEditor.set("limitYellowPct", to: "70", at: url)
        for bad in ["0", "101", "-5", "abc", ""] {
            let outcome = ConfigEditor.set("limitYellowPct", to: bad, at: url)
            guard case .rejected = outcome else {
                return XCTFail("\(bad) was accepted: \(outcome)")
            }
        }
        XCTAssertEqual(ConfigEditor.value(of: "limitYellowPct", at: url), "70",
                       "a refused value still changed the file")
    }

    func testAPollIntervalBelowTheFloorIsRefused() {
        guard case .rejected = ConfigEditor.set("pollIntervalSeconds", to: "5", at: url) else {
            return XCTFail("the engine's ten second floor was not applied")
        }
    }

    func testAnUnknownChoiceIsRefused() {
        guard case .rejected = ConfigEditor.set("updateChannel", to: "nightly", at: url) else {
            return XCTFail("an unknown channel was accepted")
        }
    }

    func testAnUnknownProviderIsRefusedRatherThanSilentlyDropped() {
        guard case .rejected = ConfigEditor.set("providers", to: "Claude,Gemini", at: url) else {
            return XCTFail("an unknown provider was accepted")
        }
    }

    func testAnUnknownKeyIsReportedRatherThanWritten() throws {
        XCTAssertEqual(ConfigEditor.set("nonsense", to: "1", at: url), .unknownKey("nonsense"))
        XCTAssertNil(try? raw()["nonsense"])
    }

    // MARK: - The file

    /// Settings this build does not know about belong to some other build, and dropping them
    /// on the next write would silently reset them.
    func testUnrecognisedKeysInTheFileSurviveAWrite() throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try JSONSerialization
            .data(withJSONObject: ["somethingNew": "keep me", "alerts": true])
            .write(to: url)

        _ = ConfigEditor.set("limitRedPct", to: "90", at: url)
        XCTAssertEqual(try raw()["somethingNew"] as? String, "keep me")
    }

    func testEverySettingCanBeReadBack() {
        for (setting, value) in ConfigEditor.current(at: url) {
            XCTAssertFalse(value.isEmpty, "\(setting.key) read back as nothing")
            XCTAssertFalse(setting.summary.isEmpty, "\(setting.key) has no description")
        }
    }

    /// A shell renders a control per kind, so every setting has to declare one it can honour.
    func testEverySettingRoundTripsItsOwnCurrentValue() {
        for (setting, value) in ConfigEditor.current(at: url) {
            let outcome = ConfigEditor.set(setting.key, to: value, at: url)
            XCTAssertEqual(outcome, .unchanged(key: setting.key, value: value),
                           "\(setting.key) could not be set to what it already was: \(outcome)")
        }
    }
}
