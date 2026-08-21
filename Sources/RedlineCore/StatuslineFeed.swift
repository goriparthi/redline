// Claude's rate-limit windows as Claude Code itself reports them. Claude Code passes a JSON
// payload on stdin to any configured statusLine command, and that payload already carries the
// five_hour, seven_day and model-scoped windows. A feeder writes them to a sidecar file; this
// parses it. No token, no Keychain, no network.
import Foundation

public struct StatuslineSnapshot: Equatable {
    public let windows: [LimitWindow]
    /// When the feeder last wrote. Nil when the file omits or mangles it, which makes the
    /// snapshot unusable rather than merely old: age is the only thing that qualifies it.
    public let updatedAt: Date?

    public init(windows: [LimitWindow], updatedAt: Date?) {
        self.windows = windows
        self.updatedAt = updatedAt
    }

    public var isEmpty: Bool { windows.isEmpty }

    /// Fresh enough to stand alone. The sidecar is rewritten on every statusline draw, so a
    /// quiet quarter hour means Claude Code is idle and a live source should take over.
    public func isFresh(now: Date = Date()) -> Bool {
        guard let updatedAt else { return false }
        return now.timeIntervalSince(updatedAt) <= StatuslineFeed.freshFor
    }
}

public enum StatuslineFeed {
    public static let provider = "Claude"

    /// How long a sidecar reading may stand in for a live one. Past this the caller should
    /// prefer a fetch, and anything still shown from the feed is rendered as stale.
    public static let freshFor: TimeInterval = 900

    /// Default sidecar location. Kept under the app's own directory rather than ~/.claude so
    /// RedLine never writes into a tree another tool owns.
    public static func defaultPath(home: URL? = nil) -> URL {
        let root = home ?? RedlineHome.url
        return root.appendingPathComponent(".local/share/redline/claude-usage.json")
    }

    /// Reads and parses the sidecar. A missing file is not an error: it means the feeder is
    /// not installed yet, which the caller reports differently from a broken one.
    public static func read(path: URL, now: Date = Date()) -> StatuslineSnapshot? {
        guard let data = try? Data(contentsOf: path) else {
            // A missing file is the feeder not being installed, which the caller reports
            // differently, so this is only worth a debug line.
            Diag.log.debug("feed.unreadable", "sidecar not readable", ["path": path.path])
            return nil
        }
        return parse(data: data, now: now)
    }

    public static func parse(data: Data, now: Date = Date()) -> StatuslineSnapshot? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            // The file is there and unreadable as JSON, which is a broken feeder rather than
            // a missing one. That distinction is the whole reason this is an error.
            Diag.log.error("feed.parse_failed", "sidecar is not valid JSON",
                           ["bytes": "\(data.count)"])
            return nil
        }
        return parse(json, now: now)
    }

    /// Accepts both the shape ClaudeHUD writes for `display.externalUsageWritePath` and the
    /// raw `rate_limits` block from the statusline payload, so a user already running that
    /// tool needs no second feeder.
    public static func parse(_ json: [String: Any], now: Date = Date()) -> StatuslineSnapshot {
        let root = (json["rate_limits"] as? [String: Any]) ?? json
        var out: [LimitWindow] = []

        for key in ["five_hour", "seven_day"] {
            guard let d = root[key] as? [String: Any] else { continue }
            // used_percentage is the statusline spelling; utilization is the usage endpoint's.
            // Both are 0-100, and accepting either keeps one parser for both sources.
            guard let pct = LimitParser.number(d["used_percentage"])
                ?? LimitParser.number(d["utilization"]) else { continue }
            out.append(LimitWindow(provider: provider, key: key, utilization: pct,
                                   resetsAt: date(d["resets_at"]), source: .official))
        }

        // Model-scoped weekly windows (Fable, Opus). Claude Code names them at runtime, so the
        // key is derived from the display name rather than guessed from a fixed list.
        for entry in (root["model_scoped"] as? [[String: Any]]) ?? [] {
            guard let pct = LimitParser.number(entry["utilization"])
                ?? LimitParser.number(entry["used_percentage"]) else { continue }
            let name = (entry["display_name"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !name.isEmpty else { continue }
            out.append(LimitWindow(provider: provider, key: scopedKey(for: name),
                                   utilization: pct, resetsAt: date(entry["resets_at"]),
                                   source: .official))
        }

        let stamp = date(json["updated_at"])
        // A window whose reset has already passed reports a percentage that no longer exists,
        // which matters more here than elsewhere: the sidecar is written only while Claude
        // Code runs, so between sessions it is expected to be old.
        return StatuslineSnapshot(windows: LimitParser.sorted(LimitParser.unexpired(out, now: now)),
                                  updatedAt: stamp)
    }

    /// "Fable" -> "seven_day_fable", so it sorts and displays beside the other weekly windows
    /// instead of falling through to the unrecognized bucket.
    static func scopedKey(for displayName: String) -> String {
        let slug = displayName.lowercased()
            .replacingOccurrences(of: " ", with: "_")
            .filter { $0.isLetter || $0.isNumber || $0 == "_" }
        return slug.isEmpty ? "seven_day_scoped" : "seven_day_\(slug)"
    }

    /// resets_at arrives as ISO8601 with or without fractional seconds, or as epoch seconds or
    /// milliseconds. Every producer in the chain spells it differently.
    static func date(_ value: Any?) -> Date? {
        if let s = value as? String, !s.isEmpty {
            let frac = ISO8601DateFormatter()
            frac.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let d = frac.date(from: s) { return d }
            return ISO8601DateFormatter().date(from: s)
        }
        guard let n = LimitParser.number(value), n > 0 else { return nil }
        // Anything past the year 2286 in seconds is milliseconds instead
        return Date(timeIntervalSince1970: n > 1e11 ? n / 1000 : n)
    }
}
