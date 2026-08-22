// The version exists in three places by necessity: Info.plist is what the app bundle reports,
// project.yml is what Xcode stamps, and RedlineVersion is what a build with no bundle uses.
// These tests are what stops a release from bumping one and forgetting another.
import XCTest
@testable import RedlineCore

final class VersionTests: XCTestCase {
    private var root: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // RedlineCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // repo root
    }

    private func firstMatch(_ pattern: String, in text: String) -> String? {
        guard let re = try? NSRegularExpression(pattern: pattern),
              let m = re.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let r = Range(m.range(at: 1), in: text) else { return nil }
        return String(text[r])
    }

    func testInfoPlistAgreesWithTheCompiledVersion() throws {
        let plist = try String(contentsOf: root.appendingPathComponent("Resources/Info.plist"),
                               encoding: .utf8)
        let found = firstMatch(
            "<key>CFBundleShortVersionString</key>\\s*<string>([^<]+)</string>", in: plist)
        XCTAssertEqual(found, RedlineVersion.current,
                       "Info.plist and RedlineVersion.current have drifted")
    }

    func testProjectYmlAgreesWithTheCompiledVersion() throws {
        let yml = try String(contentsOf: root.appendingPathComponent("project.yml"),
                             encoding: .utf8)
        XCTAssertEqual(firstMatch("MARKETING_VERSION: \"([^\"]+)\"", in: yml),
                       RedlineVersion.current, "project.yml has drifted")
        XCTAssertEqual(firstMatch("CURRENT_PROJECT_VERSION: \"([^\"]+)\"", in: yml),
                       RedlineVersion.current, "project.yml has drifted")
    }
}
