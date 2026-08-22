// Autostart, checked against a scratch root rather than the machine's real login items.
// The shared assertions come first because the three backends disagreeing about what
// "enabled" means is the failure worth preventing.
import XCTest
@testable import RedlinePlatform
import RedlineCore

final class AutostartTests: XCTestCase {
    private var root: URL!
    private var service: Autostarting!
    private let program = URL(fileURLWithPath: "/opt/redline/redline")

    override func setUpWithError() throws {
        root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-autostart-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        #if os(Windows)
        // A scratch subkey, so a test run never edits what actually starts at login
        service = RunKeyAutostart(subkey: #"Software\RedLineTests\Run"#,
                                  valueName: "RedLineTest-\(UUID().uuidString.prefix(8))")
        #else
        service = PlatformAutostart.service(root: root)
        #endif
    }

    override func tearDownWithError() throws {
        try? service.disable()
        try? FileManager.default.removeItem(at: root)
    }

    func testItStartsDisabled() {
        XCTAssertFalse(service.isEnabled, "\(service.name) claimed to be on before anything ran")
    }

    func testEnableThenDisableRoundTrips() throws {
        try service.enable(program: program, arguments: [])
        XCTAssertTrue(service.isEnabled, "\(service.name) did not report itself enabled")
        try service.disable()
        XCTAssertFalse(service.isEnabled, "\(service.name) stayed enabled after disable")
    }

    /// Enabling twice is what a settings toggle does when someone flips it back and forth,
    /// and it must not accumulate anything or start failing.
    func testEnablingTwiceIsHarmless() throws {
        try service.enable(program: program, arguments: [])
        try service.enable(program: program, arguments: [])
        XCTAssertTrue(service.isEnabled)
    }

    func testDisablingSomethingNeverEnabledIsHarmless() {
        XCTAssertNoThrow(try service.disable())
    }

    func testTheProgramPathSurvives() throws {
        try service.enable(program: program, arguments: ["--background"])
        #if os(macOS)
        let plist = try XCTUnwrap(service as? LaunchAgentAutostart).plistURL
        let text = try String(contentsOf: plist, encoding: .utf8)
        XCTAssertTrue(text.contains("/opt/redline/redline"), text)
        XCTAssertTrue(text.contains("--background"), text)
        // Quitting on purpose has to stay quit
        XCTAssertTrue(text.contains("SuccessfulExit"), text)
        #elseif os(Linux)
        let unit = try XCTUnwrap(service as? SystemdUserAutostart).unitURL
        let text = try String(contentsOf: unit, encoding: .utf8)
        XCTAssertTrue(text.contains("ExecStart=/opt/redline/redline --background"), text)
        XCTAssertTrue(text.contains("Restart=on-failure"), text)
        #else
        XCTAssertTrue(service.isEnabled)
        #endif
    }
}
