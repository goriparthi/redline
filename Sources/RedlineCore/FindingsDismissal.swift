// Which findings the user has already dealt with, and until when.
//
// A finding restates a habit that is still in the transcripts, so left alone it sits on screen
// for weeks after it has been read. Dismissing one hides it for a while; if it is still true
// when the snooze expires it comes back, because that is the only honest way to stop nagging
// without quietly dropping something real.
import Foundation

public struct FindingsDismissals: Codable, Equatable {
    /// Finding id to the moment it was dismissed. Ids are stable strings such as
    /// `mcp-unused`, so a dismissal survives a rescan.
    public private(set) var dismissed: [String: Date]

    public init(dismissed: [String: Date] = [:]) {
        self.dismissed = dismissed
    }

    public var isEmpty: Bool { dismissed.isEmpty }

    public func isHidden(_ id: String, snoozeDays: Int, now: Date = Date()) -> Bool {
        guard let at = dismissed[id] else { return false }
        // A snooze in the future means a clock that moved backwards, not a longer snooze
        guard at <= now else { return true }
        return now.timeIntervalSince(at) < Double(snoozeDays) * 86400
    }

    public mutating func dismiss(_ id: String, at: Date = Date()) {
        dismissed[id] = at
    }

    public mutating func restore(_ id: String) {
        dismissed[id] = nil
    }

    public mutating func restoreAll() {
        dismissed.removeAll()
    }

    /// Drops snoozes that have run out, so the file does not grow for every finding the
    /// checks have ever produced.
    public mutating func prune(snoozeDays: Int, now: Date = Date()) {
        dismissed = dismissed.filter { isHidden($0.key, snoozeDays: snoozeDays, now: now) }
    }
}

public enum FindingsDismissalStore {
    public static func url(home: URL? = nil) -> URL {
        (home ?? RedlineHome.url)
            .appendingPathComponent(".local/share/redline/findings-dismissed.json")
    }

    public static func load(from url: URL? = nil) -> FindingsDismissals {
        let url = url ?? FindingsDismissalStore.url()
        guard let data = try? Data(contentsOf: url) else { return FindingsDismissals() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // A file that exists but will not decode means dismissals silently came back.
        do {
            return try decoder.decode(FindingsDismissals.self, from: data)
        } catch {
            Diag.log.error("findings.dismissals_corrupt",
                           "dismissal file did not decode; starting over",
                           ["path": url.path, "error": String(describing: error)])
            return FindingsDismissals()
        }
    }

    @discardableResult
    public static func save(_ state: FindingsDismissals, to url: URL? = nil) -> Bool {
        let url = url ?? FindingsDismissalStore.url()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(state) else {
            Diag.log.error("findings.dismissals_encode_failed", "could not encode dismissals")
            return false
        }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try data.write(to: url, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: url.path)
            return true
        } catch {
            Diag.log.error("findings.dismissals_save_failed", "could not write dismissals",
                           ["path": url.path, "error": String(describing: error)])
            return false
        }
    }
}

public extension FindingsReport {
    /// The same report with dismissed findings removed, and a count of what was hidden.
    ///
    /// Every surface reads its counts off this rather than off the raw report, so dismissing
    /// a finding quiets the menu bar line too. A panel that empties while the menu still
    /// claims four findings is worse than no dismissal at all.
    func visible(_ dismissals: FindingsDismissals, snoozeDays: Int,
                 now: Date = Date()) -> FindingsReport {
        let kept = findings.filter {
            !dismissals.isHidden($0.id, snoozeDays: snoozeDays, now: now)
        }
        return FindingsReport(generatedAt: generatedAt, windowDays: windowDays,
                              sessionsScanned: sessionsScanned, findings: kept,
                              hidden: findings.count - kept.count)
    }
}
