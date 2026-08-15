// Wire format between the menu bar app and the widget. A widget cannot poll or parse
// transcripts inside its time budget, so the app writes this and the widget only renders it.
import Foundation

public struct Snapshot: Codable, Equatable {
    public struct Window: Codable, Equatable, Identifiable {
        // Providers share window keys, so identity must include the provider or SwiftUI
        // renders one row twice and drops the other.
        public var id: String { "\(provider)|\(key)" }

        public let provider: String
        public let key: String
        public let utilization: Double
        public let resetsAt: Date?

        public var displayName: String {
            switch key {
            case "five_hour":        return "Session · 5h"
            case "seven_day":        return provider == "Claude" ? "Week · all models" : "Week"
            case "seven_day_opus":   return "Week · Opus"
            case "seven_day_sonnet": return "Week · Sonnet"
            default: return key.replacingOccurrences(of: "_", with: " ")
            }
        }

        public init(provider: String, key: String, utilization: Double, resetsAt: Date?) {
            self.provider = provider
            self.key = key
            self.utilization = utilization
            self.resetsAt = resetsAt
        }
    }

    public struct Totals: Codable, Equatable {
        public let io: Int
        public let cost: Double
        public let hasUnpriced: Bool

        public init(io: Int, cost: Double, hasUnpriced: Bool) {
            self.io = io
            self.cost = cost
            self.hasUnpriced = hasUnpriced
        }

        public init(_ agg: Agg) {
            self.init(io: agg.io, cost: agg.cost, hasUnpriced: agg.hasUnpriced)
        }
    }

    /// Live Ollama state, carried in the snapshot so the sandboxed widget never needs network
    /// access of its own.
    public struct Ollama: Codable, Equatable {
        public struct Running: Codable, Equatable {
            public let name: String
            public let sizeBytes: Int64
            public let vramShare: Double

            public init(name: String, sizeBytes: Int64, vramShare: Double) {
                self.name = name
                self.sizeBytes = sizeBytes
                self.vramShare = vramShare
            }
        }

        public let reachable: Bool
        public let version: String?
        public let running: [Running]
        public let downloadedCount: Int
        public let downloadedBytes: Int64

        public init(reachable: Bool, version: String?, running: [Running],
                    downloadedCount: Int, downloadedBytes: Int64) {
            self.reachable = reachable
            self.version = version
            self.running = running
            self.downloadedCount = downloadedCount
            self.downloadedBytes = downloadedBytes
        }
    }

    public let updatedAt: Date
    public let limits: [Window]
    public let today: Totals
    public let week: Totals
    // Optional so an older snapshot on disk still decodes rather than leaving the widget blank
    public let todayByProvider: [String: Totals]?
    public let weekByProvider: [String: Totals]?
    public let ollama: Ollama?

    // Timestamps are stored at whole-second precision, since the JSON encoding is ISO8601
    // and nothing here is finer-grained than a poll interval.
    /// A provider's own reported health, from its public status feed. Optional end to end
    /// so snapshots written before this field existed still decode.
    public struct Service: Codable, Equatable {
        public let provider: String
        public let indicator: String
        public let description: String

        public init(provider: String, indicator: String, description: String) {
            self.provider = provider
            self.indicator = indicator
            self.description = description
        }

        public var isOperational: Bool { indicator == "none" || indicator == "local" }

        /// Calm and factual: report what the operator reports, never alarm. The two local
        /// indicators are Ollama's, probed directly rather than read from a status page.
        public var phrase: String {
            switch indicator {
            case "none":  return "service ok"
            case "minor": return "minor incident reported"
            case "major", "critical": return "outage reported"
            case "local": return "local server running"
            case "local-down": return "local server not reachable"
            default: return "status unknown"
            }
        }
    }

    public var services: [Service]?

    public init(updatedAt: Date, limits: [LimitWindow], today: Agg, week: Agg,
                ollama: Ollama? = nil, services: [Service]? = nil) {
        self.updatedAt = Snapshot.truncate(updatedAt)
        self.todayByProvider = today.providers.mapValues {
            Totals(io: $0.io, cost: $0.cost, hasUnpriced: today.hasUnpriced)
        }
        self.weekByProvider = week.providers.mapValues {
            Totals(io: $0.io, cost: $0.cost, hasUnpriced: week.hasUnpriced)
        }
        self.ollama = ollama
        self.limits = limits.map {
            Window(provider: $0.provider, key: $0.key, utilization: $0.utilization,
                   resetsAt: $0.resetsAt.map(Snapshot.truncate))
        }
        self.today = Totals(today)
        self.week = Totals(week)
        self.services = services
    }

    static func truncate(_ d: Date) -> Date {
        Date(timeIntervalSince1970: d.timeIntervalSince1970.rounded(.down))
    }

    // Widgets refresh on the system's schedule, not ours, so a reading can be minutes old.
    // Callers must surface staleness rather than implying the number is live.
    public func isStale(now: Date = Date(), tolerance: TimeInterval = 900) -> Bool {
        now.timeIntervalSince(updatedAt) > tolerance
    }

    public func worst(prefix: String, provider: String? = nil) -> Window? {
        limits
            .filter { $0.key.hasPrefix(prefix) }
            .filter { provider == nil
                      || $0.provider.caseInsensitiveCompare(provider!) == .orderedSame }
            .max(by: { $0.utilization < $1.utilization })
    }

    public func windows(for provider: String) -> [Window] {
        limits.filter { $0.provider.caseInsensitiveCompare(provider) == .orderedSame }
    }

    public func today(for provider: String?) -> Totals {
        guard let provider else { return today }
        return todayByProvider?[provider] ?? Totals(io: 0, cost: 0, hasUnpriced: false)
    }

    public func week(for provider: String?) -> Totals {
        guard let provider else { return week }
        return weekByProvider?[provider] ?? Totals(io: 0, cost: 0, hasUnpriced: false)
    }
}

// Reads and writes the snapshot in a container both processes can reach. Falls back to a
// per-user path when no App Group is configured, so the app works before the widget exists.
public enum SnapshotStore {
    public static let appGroup = "group.com.goriparthi.redline"
    public static let fileName = "snapshot.json"

    public static let widgetBundleID = "com.goriparthi.redline.widget"

    /// Inside a sandboxed extension this resolves to its own container, which it can always
    /// read whatever the signing situation. In the app it resolves to the normal location.
    public static var localAppSupportURL: URL? {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent("redline/\(fileName)")
    }

    /// The widget's container, addressed absolutely so the app can write into it. This is the
    /// only route that works for an ad-hoc signed sandboxed widget: App Group containers need
    /// a real Team ID, and a path exception outside the container is not granted.
    public static var widgetContainerURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(
                "Library/Containers/\(widgetBundleID)/Data/Library/Application Support/redline/\(fileName)")
    }

    // Always available, and readable by any non-sandboxed process of this user
    public static var userURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/redline/\(fileName)")
    }

    // Only resolves for code signed with a real Team ID. An ad-hoc signed sandboxed
    // extension gets nil here, which is why the user path exists as the primary.
    public static func groupURL(appGroup group: String? = appGroup) -> URL? {
        guard let group,
              let container = FileManager.default
                .containerURL(forSecurityApplicationGroupIdentifier: group) else { return nil }
        return container.appendingPathComponent(fileName)
    }

    public static func url(appGroup group: String? = appGroup) -> URL {
        groupURL(appGroup: group) ?? userURL
    }

    // Every location the snapshot is written to. The widget container is included only when
    // it already exists, so nothing is created for a widget that was never installed.
    public static var writeTargets: [URL] {
        var out = [userURL]
        let container = widgetContainerURL
        if FileManager.default.fileExists(
            atPath: container.deletingLastPathComponent()
                .deletingLastPathComponent().path) {
            out.append(container)
        }
        if let g = groupURL() { out.append(g) }
        return out
    }

    // Own container first: in the widget that is the one guaranteed readable location
    public static var readCandidates: [URL] {
        var out: [URL] = []
        if let local = localAppSupportURL { out.append(local) }
        if let g = groupURL() { out.append(g) }
        out.append(userURL)
        return out
    }

    @discardableResult
    public static func writeEverywhere(_ snapshot: Snapshot) -> Bool {
        // Succeed if any target takes it; a missing App Group must not fail the write
        writeTargets.map { write(snapshot, to: $0) }.contains(true)
    }

    public static func readAny() -> Snapshot? {
        for url in readCandidates {
            if let s = read(from: url) { return s }
        }
        return nil
    }

    private static var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }

    private static var decoder: JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }

    @discardableResult
    public static func write(_ snapshot: Snapshot, to url: URL = url()) -> Bool {
        guard let data = try? encoder.encode(snapshot) else { return false }
        do {
            try FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: url, options: .atomic)
            // Usage and cost figures are nobody else's business on a shared machine
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: url.path)
            return true
        } catch {
            return false
        }
    }

    public static func read(from url: URL = url()) -> Snapshot? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? decoder.decode(Snapshot.self, from: data)
    }
}
