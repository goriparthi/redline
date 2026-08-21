// The published sidecar. It is a contract with tools this project does not control, so the
// spellings other readers rely on are asserted rather than assumed.
import XCTest
@testable import RedlineCore

final class SidecarTests: XCTestCase {
    private var dir: URL!
    private let now = Date(timeIntervalSince1970: 1_760_000_000)

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-sidecar-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private var windows: [LimitWindow] {
        [
            LimitWindow(provider: "Claude", key: "five_hour", utilization: 42,
                        resetsAt: now.addingTimeInterval(3600), source: .official),
            LimitWindow(provider: "Claude", key: "seven_day", utilization: 18,
                        resetsAt: now.addingTimeInterval(86400), source: .official),
            LimitWindow(provider: "Claude", key: "seven_day_opus", utilization: 71,
                        resetsAt: now.addingTimeInterval(86400), source: .official),
            LimitWindow(provider: "Codex", key: "seven_day", utilization: 5,
                        resetsAt: nil, source: .official),
        ]
    }

    func testPayloadCarriesBothPercentageSpellings() throws {
        let json = Sidecar.payload(windows: windows, updatedAt: now, producer: "redline/test")
        let five = try XCTUnwrap(json["five_hour"] as? [String: Any])
        XCTAssertEqual(five["used_percentage"] as? Double, 42)
        XCTAssertEqual(five["utilization"] as? Double, 42)
        XCTAssertNotNil(five["resets_at"])
    }

    func testModelScopedWindowsKeepTheirDisplayName() throws {
        let json = Sidecar.payload(windows: windows, updatedAt: now, producer: "redline/test")
        let scoped = try XCTUnwrap(json["model_scoped"] as? [[String: Any]])
        XCTAssertEqual(scoped.count, 1)
        XCTAssertEqual(scoped[0]["display_name"] as? String, "Opus")
        XCTAssertEqual(scoped[0]["utilization"] as? Double, 71)
    }

    func testOtherProvidersStayOutOfTheStandardKeys() throws {
        let json = Sidecar.payload(windows: windows, updatedAt: now, producer: "redline/test")
        // The standard block is Claude-shaped; Codex would be misread as Claude's week
        let seven = try XCTUnwrap(json["seven_day"] as? [String: Any])
        XCTAssertEqual(seven["utilization"] as? Double, 18)
        let extra = try XCTUnwrap(json["redline"] as? [String: Any])
        let all = try XCTUnwrap(extra["windows"] as? [[String: Any]])
        XCTAssertEqual(all.count, 4, "every provider still travels in the namespaced block")
    }

    func testRoundTripsThroughOurOwnParser() throws {
        let url = dir.appendingPathComponent("usage-snapshot.json")
        XCTAssertTrue(Sidecar.publish(windows: windows, updatedAt: now,
                                      producer: "redline/test", to: url))
        let parsed = try XCTUnwrap(StatuslineFeed.read(path: url, now: now))
        let stamp = try XCTUnwrap(parsed.updatedAt)
        XCTAssertEqual(stamp.timeIntervalSince1970, now.timeIntervalSince1970, accuracy: 1)
        XCTAssertEqual(parsed.windows.count, 3)
        XCTAssertTrue(parsed.isFresh(now: now))
    }

    func testPublishedFileIsPrivate() throws {
        let url = dir.appendingPathComponent("usage-snapshot.json")
        Sidecar.publish(windows: windows, updatedAt: now, producer: "redline/test", to: url)
        let perms = try FileManager.default
            .attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(perms?.int16Value, 0o600)
    }

    func testExternalPathMustBeAbsoluteJSON() {
        XCTAssertNil(Sidecar.validExternalPath("usage.json"))
        XCTAssertNil(Sidecar.validExternalPath(""))
        #if os(Windows)
        // Absolute means drive-qualified or UNC here. A bare leading slash is rooted on
        // whichever drive happens to be current, which is not a path worth trusting.
        XCTAssertNil(Sidecar.validExternalPath(#"C:\tmp\usage.txt"#))
        XCTAssertNil(Sidecar.validExternalPath("/tmp/usage.json"))
        XCTAssertNotNil(Sidecar.validExternalPath(#"C:\tmp\usage.json"#))
        XCTAssertNotNil(Sidecar.validExternalPath(#"\\host\share\usage.json"#))
        #else
        XCTAssertNil(Sidecar.validExternalPath("/tmp/usage.txt"))
        XCTAssertNotNil(Sidecar.validExternalPath("/tmp/usage.json"))
        #endif
    }

    func testStaleExternalSidecarIsIgnored() throws {
        let url = dir.appendingPathComponent("other.json")
        Sidecar.publish(windows: windows, updatedAt: now.addingTimeInterval(-7200),
                        producer: "other/1.0", to: url)
        XCTAssertNil(Sidecar.readExternal(path: url.path, now: now),
                     "a file another tool stopped updating must not stand in for a live one")
        XCTAssertNotNil(Sidecar.readExternal(path: url.path,
                                             now: now.addingTimeInterval(-7200)))
    }

    func testEmptySidecarIsNotASource() throws {
        let url = dir.appendingPathComponent("empty.json")
        try Data("{\"updated_at\":\"2026-08-18T00:00:00Z\"}".utf8).write(to: url)
        XCTAssertNil(Sidecar.readExternal(path: url.path,
                                          now: ISO8601DateFormatter()
                                            .date(from: "2026-08-18T00:01:00Z")!))
    }
}
