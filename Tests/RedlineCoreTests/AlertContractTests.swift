// What a shell reads to deliver an alert. The decision is the engine's; the shell only posts,
// so every word it needs has to be in this file.
//
// RedLine.Core.Tests reads the same fixture. Rename a key here and one of the two fails.
import XCTest
@testable import RedlineCore

final class AlertContractTests: XCTestCase {
    private var fixture: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("windows/RedLine.Core.Tests/fixtures/alert-feed.json")
    }

    /// The events the fixture was published from, so the whole file can be rebuilt and
    /// compared rather than picked at key by key.
    private var events: [AlertEvent] {
        [
            AlertEvent(kind: .threshold(80), provider: "Claude", key: "five_hour",
                       title: "Claude · Session · 5h", body: "80% used, resets at 4:00 PM",
                       id: "Claude:five_hour:1787203800:80"),
            AlertEvent(kind: .limitReached, provider: "Codex", key: "five_hour",
                       title: "Codex · Session · 5h", body: "Limit reached",
                       id: "Codex:five_hour:1787203800:100"),
        ]
    }

    private func recorded() throws -> [String: Any] {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: fixture))
        return try XCTUnwrap(json as? [String: Any])
    }

    /// Rebuilt and compared after parsing, not as text: a double prints differently per
    /// platform, and the fixture must not fail on the machine that did not write it.
    func testWhatIsPublishedIsStillWhatTheFixtureHolds() throws {
        let out = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-alert-contract-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: out) }

        XCTAssertEqual(AlertFeed.publish(events,
                                         now: Date(timeIntervalSince1970: 1_787_227_200),
                                         to: out), 1)
        let published = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: Data(contentsOf: out)) as? [String: Any])
        XCTAssertEqual(NSDictionary(dictionary: published),
                       NSDictionary(dictionary: try recorded()),
                       "the shape a shell delivers from has moved; regenerate "
                       + "windows/RedLine.Core.Tests/fixtures/alert-feed.json")
    }

    /// A shell posts these words verbatim, so they have to arrive with the event rather than
    /// being assembled from a key and a percentage on the other side.
    func testEveryEventCarriesTheWordsToPost() throws {
        let events = try XCTUnwrap(try recorded()["events"] as? [[String: Any]])
        XCTAssertEqual(events.count, 2)
        for event in events {
            for key in ["id", "kind", "provider", "key", "title", "body", "sound"] {
                XCTAssertNotNil(event[key], "an event with no \(key)")
            }
            XCTAssertFalse((event["title"] as? String ?? "").isEmpty)
            XCTAssertFalse((event["body"] as? String ?? "").isEmpty)
        }
    }

    func testTheReachedLimitIsTheOnlyOneThatMakesASound() throws {
        let events = try XCTUnwrap(try recorded()["events"] as? [[String: Any]])
        let loud = events.filter { $0["sound"] as? Bool == true }
        XCTAssertEqual(loud.count, 1)
        XCTAssertEqual(loud.first?["kind"] as? String, "limit_reached")
    }
}
