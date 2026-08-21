// The usage sidecar, written as well as read.
//
// Three separate projects (ClaudeHUD, claude-monitor, CodexBar) converged on the same idea:
// one small JSON file holding the current rate-limit windows, written by whoever has them and
// read by whoever needs them. RedLine already reads that shape. This publishes it too, so a
// status line, a tmux strip, or another monitor can take RedLine's reading instead of every
// tool racing for the same source.
//
// Standard keys first, in the shape the other tools already parse. Anything RedLine-specific
// lives under a "redline" object that a foreign reader can ignore without losing the windows.
import Foundation

public enum Sidecar {
    /// Where RedLine publishes. Deliberately not the file the statusline feeder writes:
    /// reading our own output back would be a loop, and the feeder's file is Claude Code's
    /// word rather than ours.
    public static func publishURL(home: URL? = nil) -> URL {
        return AppPaths.data("usage-snapshot.json", in: home)
    }

    /// How old an external sidecar may be before it is ignored. Same tolerance RedLine
    /// applies to its own feed, since it is the same kind of file.
    public static let defaultFreshness: TimeInterval = 900

    public struct Totals {
        public let io: Int
        public let cost: Double
        public let priced: Bool

        public init(io: Int, cost: Double, priced: Bool) {
            self.io = io
            self.cost = cost
            self.priced = priced
        }
    }

    /// Builds the payload. Kept separate from writing so the shape can be asserted in tests
    /// without touching the filesystem.
    public static func payload(windows: [LimitWindow], updatedAt: Date, producer: String,
                               today: Totals? = nil, week: Totals? = nil,
                               limitsAsOf: Date? = nil) -> [String: Any] {
        let iso = ISO8601DateFormatter()
        var out: [String: Any] = [
            "updated_at": iso.string(from: updatedAt),
            "source": "redline",
            "producer": producer,
        ]

        let claude = windows.filter {
            $0.provider.caseInsensitiveCompare("Claude") == .orderedSame
        }
        for w in claude where w.key == "five_hour" || w.key == "seven_day" {
            var block: [String: Any] = [
                // Both spellings on purpose: the statusline payload says used_percentage,
                // the usage endpoint says utilization, and readers exist for each.
                "used_percentage": w.utilization,
                "utilization": w.utilization,
            ]
            if let r = w.resetsAt { block["resets_at"] = iso.string(from: r) }
            out[w.key] = block
        }

        let scoped = claude.filter { $0.key.hasPrefix("seven_day_") }
        if !scoped.isEmpty {
            out["model_scoped"] = scoped.map { w -> [String: Any] in
                var entry: [String: Any] = [
                    "display_name": displayName(forScopedKey: w.key),
                    "utilization": w.utilization,
                ]
                if let r = w.resetsAt { entry["resets_at"] = iso.string(from: r) }
                return entry
            }
        }

        var extra: [String: Any] = [
            "windows": windows.filter { !$0.isUninformative }.map { w -> [String: Any] in
                var entry: [String: Any] = [
                    "provider": w.provider,
                    "key": w.key,
                    "utilization": w.utilization,
                    "source": w.source.rawValue,
                ]
                if let r = w.resetsAt { entry["resets_at"] = iso.string(from: r) }
                return entry
            },
        ]
        if let limitsAsOf { extra["claude_limits_as_of"] = iso.string(from: limitsAsOf) }
        if let today { extra["today"] = totalsBlock(today) }
        if let week { extra["week"] = totalsBlock(week) }
        out["redline"] = extra
        return out
    }

    private static func totalsBlock(_ t: Totals) -> [String: Any] {
        [
            "tokens": t.io,
            "cost_usd": t.cost,
            // Tokens are counted by the provider; the dollar figure is arithmetic over a
            // pricing table, and an unpriced model means even that is incomplete.
            "tokens_basis": Provenance.official.rawValue,
            "cost_basis": Provenance.localEstimate.rawValue,
            "cost_partial": !t.priced,
        ]
    }

    /// "seven_day_opus" -> "Opus". The key was derived from the display name in the first
    /// place, so this puts back what the round trip took out.
    static func displayName(forScopedKey key: String) -> String {
        let slug = key.hasPrefix("seven_day_") ? String(key.dropFirst(10)) : key
        return slug.split(separator: "_")
            .map { $0.prefix(1).uppercased() + $0.dropFirst() }
            .joined(separator: " ")
    }

    @discardableResult
    public static func publish(windows: [LimitWindow], updatedAt: Date = Date(),
                               producer: String, today: Totals? = nil, week: Totals? = nil,
                               limitsAsOf: Date? = nil, to url: URL? = nil) -> Bool {
        let url = url ?? publishURL()
        let json = payload(windows: windows, updatedAt: updatedAt, producer: producer,
                           today: today, week: week, limitsAsOf: limitsAsOf)
        guard let data = try? JSONSerialization.data(withJSONObject: json,
                                                     options: [.prettyPrinted, .sortedKeys])
        else {
            Diag.log.error("sidecar.encode_failed", "could not encode sidecar payload")
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
            Diag.log.error("sidecar.write_failed", "could not publish sidecar",
                           ["path": url.path, "error": String(describing: error)])
            return false
        }
    }

    public static func remove(at url: URL? = nil) {
        try? FileManager.default.removeItem(at: url ?? publishURL())
    }

    /// Reads someone else's sidecar, when the user has pointed at one. Same parser as the
    /// feed, since it is the same contract; the extra rules here are about trusting a file
    /// this app did not write: absolute path, .json, and fresh enough to be about now.
    public static func readExternal(path: String, freshness: TimeInterval = defaultFreshness,
                                    now: Date = Date()) -> StatuslineSnapshot? {
        guard let url = validExternalPath(path) else { return nil }
        guard let snap = StatuslineFeed.read(path: url, now: now) else { return nil }
        guard let at = snap.updatedAt, now.timeIntervalSince(at) <= freshness,
              !snap.isEmpty else { return nil }
        return snap
    }

    /// Nil for anything that is not an absolute path to a .json file. A relative path would
    /// resolve against whatever directory the process happened to start in.
    public static func validExternalPath(_ path: String) -> URL? {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let expanded = (trimmed as NSString).expandingTildeInPath
        guard expanded.hasPrefix("/"), expanded.lowercased().hasSuffix(".json") else {
            return nil
        }
        return URL(fileURLWithPath: expanded)
    }
}
