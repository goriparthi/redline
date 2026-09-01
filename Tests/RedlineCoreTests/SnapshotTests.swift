import XCTest
@testable import RedlineCore

final class SnapshotTests: XCTestCase {
    private var dir: URL!
    private var file: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-snap-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        file = dir.appendingPathComponent("snapshot.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func sample(updatedAt: Date = Date()) -> Snapshot {
        var agg = Agg()
        agg.io = 6044
        agg.cost = 1.25
        agg.hasUnpriced = true
        let limits = [
            LimitWindow(provider: "Claude", key: "five_hour", utilization: 12,
                        resetsAt: Date(timeIntervalSince1970: 4_000_000_000)),
            LimitWindow(provider: "Codex", key: "five_hour", utilization: 41, resetsAt: nil),
            LimitWindow(provider: "Claude", key: "seven_day", utilization: 8, resetsAt: nil),
        ]
        return Snapshot(updatedAt: updatedAt, limits: limits, today: agg, week: agg)
    }

    func testRoundTripsThroughDisk() {
        let snap = sample()
        XCTAssertTrue(SnapshotStore.write(snap, to: file))
        let back = SnapshotStore.read(from: file)
        XCTAssertEqual(back, snap, "dates must survive the ISO8601 round trip")
    }

    /// Claude's windows age on their own clock: the feed writes only while Claude Code runs,
    /// so their stamp must survive the trip and drive staleness independently of updatedAt.
    func testClaudeLimitsAsOfRoundTripsAndAges() throws {
        let now = Date(timeIntervalSince1970: 1_755_400_000)
        var agg = Agg()
        agg.io = 1
        let snap = Snapshot(updatedAt: now,
                            limits: [LimitWindow(provider: "Claude", key: "seven_day",
                                                 utilization: 30, resetsAt: nil)],
                            today: agg, week: agg,
                            claudeLimitsAsOf: now.addingTimeInterval(-1200))
        XCTAssertTrue(SnapshotStore.write(snap, to: file))
        let back = try XCTUnwrap(SnapshotStore.read(from: file))
        XCTAssertEqual(back.claudeLimitsAsOf, snap.claudeLimitsAsOf)
        XCTAssertFalse(back.isStale(now: now), "the snapshot itself is current")
        XCTAssertTrue(back.claudeLimitsAreStale(now: now),
                      "while its Claude windows are twenty minutes old")
        XCTAssertFalse(back.claudeLimitsAreStale(now: now, tolerance: 1800))
    }

    /// Snapshots written before the field existed decode with no stamp, and no stamp must
    /// read as "nothing to age" rather than stale.
    func testOlderSnapshotsWithoutTheStampStillDecode() throws {
        let snap = sample()
        XCTAssertTrue(SnapshotStore.write(snap, to: file))
        var json = try JSONSerialization.jsonObject(
            with: Data(contentsOf: file)) as! [String: Any]
        json.removeValue(forKey: "claudeLimitsAsOf")
        try JSONSerialization.data(withJSONObject: json).write(to: file)
        let back = try XCTUnwrap(SnapshotStore.read(from: file))
        XCTAssertNil(back.claudeLimitsAsOf)
        XCTAssertFalse(back.claudeLimitsAreStale())
    }

    func testTimestampsAreTruncatedToWholeSeconds() {
        // ISO8601 cannot carry sub-second precision, so the initializer normalizes rather
        // than leaving an instance that fails to equal its own round trip.
        let odd = Date(timeIntervalSince1970: 1_700_000_000.987)
        XCTAssertEqual(sample(updatedAt: odd).updatedAt.timeIntervalSince1970,
                       1_700_000_000)
    }

    func testWorstPicksHighestUtilizationAcrossProviders() {
        XCTAssertEqual(sample().worst(prefix: "five_hour")?.provider, "Codex")
        XCTAssertEqual(sample().worst(prefix: "five_hour")?.utilization, 41)
        XCTAssertNil(sample().worst(prefix: "nope"))
    }

    func testCarriesUnpricedFlagSoTheWidgetCanMarkPartialCost() {
        XCTAssertTrue(sample().today.hasUnpriced)
    }

    func testStalenessIsDetectable() {
        let now = Date()
        let fresh = sample(updatedAt: now.addingTimeInterval(-60))
        let old = sample(updatedAt: now.addingTimeInterval(-3600))
        XCTAssertFalse(fresh.isStale(now: now))
        XCTAssertTrue(old.isStale(now: now),
                      "a widget must be able to say the reading is not current")
    }

    func testReadingAbsentFileReturnsNil() {
        XCTAssertNil(SnapshotStore.read(from: dir.appendingPathComponent("absent.json")))
    }

    func testFallsBackToUserPathWithoutAnAppGroup() {
        let url = SnapshotStore.url(appGroup: nil)
        XCTAssertTrue(url.path.hasSuffix(".local/share/redline/snapshot.json"))
    }
}

final class SnapshotLocationTests: XCTestCase {
    func testWidgetContainerPathIsInsideTheWidgetsOwnSandbox() {
        let p = SnapshotStore.widgetContainerURL.path
        XCTAssertTrue(p.contains("Library/Containers/\(SnapshotStore.widgetBundleID)/Data"),
                      "must address the widget's container, the one place it can always read")
        XCTAssertTrue(p.hasSuffix("redline/snapshot.json"))
    }

    func testOwnContainerIsTriedFirstWhenReading() {
        guard let first = SnapshotStore.readCandidates.first else {
            return XCTFail("expected at least one read candidate")
        }
        XCTAssertEqual(first, SnapshotStore.localAppSupportURL,
                       "a sandboxed widget can only rely on its own container")
    }

    func testUserPathIsAlwaysAWriteTarget() {
        XCTAssertTrue(SnapshotStore.writeTargets.contains(SnapshotStore.userURL))
    }
}

final class SnapshotProviderViewTests: XCTestCase {
    private func make() -> Snapshot {
        var today = Agg()
        today.io = 1000
        today.providers = ["Claude": ProviderUsage(io: 900, cost: 9, models: [:]),
                           "Codex": ProviderUsage(io: 100, cost: 0, models: [:])]
        var week = Agg()
        week.io = 5000
        week.providers = ["Claude": ProviderUsage(io: 5000, cost: 50, models: [:])]
        let limits = [
            LimitWindow(provider: "Claude", key: "five_hour", utilization: 10, resetsAt: nil),
            LimitWindow(provider: "Claude", key: "seven_day", utilization: 20, resetsAt: nil),
            LimitWindow(provider: "Codex", key: "seven_day", utilization: 80, resetsAt: nil),
        ]
        return Snapshot(updatedAt: Date(), limits: limits, today: today, week: week,
                        ollama: Snapshot.Ollama(
                            reachable: true, version: "0.5.0",
                            running: [.init(name: "qwen3:30b", sizeBytes: 100,
                                            vramShare: 0.5)],
                            downloadedCount: 3, downloadedBytes: 30_000_000_000))
    }

    func testWorstScopedToOneProvider() {
        let s = make()
        XCTAssertEqual(s.worst(prefix: "seven_day")?.utilization, 80, "unscoped takes the max")
        XCTAssertEqual(s.worst(prefix: "seven_day", provider: "Claude")?.utilization, 20,
                       "scoped must ignore other providers")
    }

    func testWindowsForProvider() {
        XCTAssertEqual(make().windows(for: "Codex").count, 1)
        XCTAssertTrue(make().windows(for: "Ollama").isEmpty)
    }

    func testPerProviderTotals() {
        let s = make()
        XCTAssertEqual(s.today(for: "Claude").io, 900)
        XCTAssertEqual(s.today(for: nil).io, 1000, "nil means every provider")
        XCTAssertEqual(s.week(for: "Codex").io, 0, "absent provider reports zero, not a crash")
    }

    func testOllamaSectionSurvivesTheRoundTrip() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertTrue(SnapshotStore.write(make(), to: dir))
        let back = SnapshotStore.read(from: dir)
        XCTAssertEqual(back?.ollama?.version, "0.5.0")
        XCTAssertEqual(back?.ollama?.running.first?.vramShare, 0.5)
        XCTAssertEqual(back?.ollama?.downloadedCount, 3)
    }

    func testOlderSnapshotWithoutNewFieldsStillDecodes() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: dir) }
        // A file written before per-provider totals and Ollama existed
        let legacy = """
        {"updatedAt":"2026-08-12T10:00:00Z","limits":[],
         "today":{"io":1,"cost":0,"hasUnpriced":false},
         "week":{"io":2,"cost":0,"hasUnpriced":false}}
        """
        try Data(legacy.utf8).write(to: dir)
        let back = SnapshotStore.read(from: dir)
        XCTAssertNotNil(back, "a widget must not go blank because the format grew")
        XCTAssertNil(back?.ollama)
        XCTAssertEqual(back?.today(for: "Claude").io, 0)
    }
}

final class SnapshotWindowIdentityTests: XCTestCase {
    func testWindowIdIncludesProvider() {
        let claude = Snapshot.Window(provider: "Claude", key: "seven_day",
                                     utilization: 5, resetsAt: nil)
        let codex = Snapshot.Window(provider: "Codex", key: "seven_day",
                                    utilization: 17, resetsAt: nil)
        XCTAssertNotEqual(claude.id, codex.id,
                          "a shared key rendered one provider twice and dropped the other")
    }

    func testWindowIdsAreUniqueAcrossATypicalSnapshot() {
        let windows = [
            Snapshot.Window(provider: "Claude", key: "five_hour", utilization: 8, resetsAt: nil),
            Snapshot.Window(provider: "Claude", key: "seven_day", utilization: 5, resetsAt: nil),
            Snapshot.Window(provider: "Codex", key: "seven_day", utilization: 17, resetsAt: nil),
        ]
        XCTAssertEqual(Set(windows.map(\.id)).count, windows.count)
    }

    func testWindowDisplayNameIsReadableAndProviderAware() {
        XCTAssertEqual(Snapshot.Window(provider: "Claude", key: "five_hour",
                                       utilization: 0, resetsAt: nil).displayName,
                       "Session · 5h")
        XCTAssertEqual(Snapshot.Window(provider: "Codex", key: "seven_day",
                                       utilization: 0, resetsAt: nil).displayName, "Week")
        XCTAssertEqual(Snapshot.Window(provider: "Claude", key: "seven_day",
                                       utilization: 0, resetsAt: nil).displayName,
                       "Week · all models")
    }
}
