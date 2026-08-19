// Rate-limit windows, normalized across providers so one display path serves all of them.
import Foundation

public struct LimitWindow: Equatable, Identifiable {
    // Provider must be part of the identity: several providers use the same window keys, and
    // a colliding id makes SwiftUI render duplicates with the wrong data.
    public var id: String { "\(provider)|\(key)" }

    public let provider: String
    public let key: String
    public let utilization: Double
    public let resetsAt: Date?
    /// Which kind of source produced this percentage. Defaulted so every existing caller
    /// keeps compiling, and so an unlabelled window says "unknown" rather than implying more.
    public let source: Provenance

    public init(provider: String, key: String, utilization: Double, resetsAt: Date?,
                source: Provenance = .unknown) {
        self.provider = provider
        self.key = key
        self.utilization = utilization
        self.resetsAt = resetsAt
        self.source = source
    }

    /// The nominal length of this window, used for pace and projection. Nil when the key
    /// does not name a duration, in which case nothing here should be inferred.
    public var length: TimeInterval? {
        if key == "five_hour" { return 5 * 3600 }
        if key.hasPrefix("seven_day") { return 7 * 86400 }
        if key.hasPrefix("window_"), key.hasSuffix("m"),
           let m = Double(key.dropFirst(7).dropLast()) { return m * 60 }
        if key.hasPrefix("window_"), key.hasSuffix("d"),
           let d = Double(key.dropFirst(7).dropLast()) { return d * 86400 }
        return nil
    }

    public var displayName: String {
        switch key {
        case "five_hour":        return "Session (5h)"
        // Only Claude splits the week by model, so "all models" would mislead elsewhere
        case "seven_day":        return provider == "Claude" ? "Week (all models)" : "Week"
        case "seven_day_opus":   return "Week (Opus)"
        case "seven_day_sonnet": return "Week (Sonnet)"
        default: return key.replacingOccurrences(of: "_", with: " ")
        }
    }

    // Providers return undocumented window keys (Claude has emitted internal codenames such
    // as nimbus_quill). Parsing keeps them, but the UI can treat them differently.
    public var isRecognized: Bool {
        key == "five_hour" || key.hasPrefix("seven_day")
    }

    // An unrecognized window at zero with no reset time says nothing actionable, so it is
    // clutter. One that is actually being consumed is real news and must still be shown.
    public var isUninformative: Bool {
        !isRecognized && utilization == 0 && resetsAt == nil
    }
}

public enum LimitParser {
    static let order = ["five_hour": 0, "seven_day": 1,
                        "seven_day_sonnet": 2, "seven_day_opus": 3]

    public static func sorted(_ windows: [LimitWindow]) -> [LimitWindow] {
        windows.sorted {
            let a = order[$0.key] ?? 9
            let b = order[$1.key] ?? 9
            return a == b ? $0.key < $1.key : a < b
        }
    }

    // Drops windows whose reset time has passed. Limits read from disk can be days old, and
    // a rolled-over window's old percentage is meaningless rather than merely stale.
    public static func unexpired(_ windows: [LimitWindow],
                                now: Date = Date()) -> [LimitWindow] {
        windows.filter { w in
            guard let resets = w.resetsAt else { return true }
            return resets > now
        }
    }

    // The Claude usage endpoint is undocumented; scan defensively for {utilization,
    // resets_at} shapes rather than trusting a fixed key path.
    public static func claudeUsage(_ json: [String: Any]) -> [LimitWindow] {
        let iso = ISO8601DateFormatter()
        let isoFrac = ISO8601DateFormatter()
        isoFrac.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var out: [LimitWindow] = []
        func walk(_ dict: [String: Any], depth: Int) {
            for (k, v) in dict {
                guard let d = v as? [String: Any] else { continue }
                let util = (d["utilization"] as? Double)
                    ?? (d["utilization"] as? Int).map(Double.init)
                if let util {
                    let resets = (d["resets_at"] as? String)
                        .flatMap { isoFrac.date(from: $0) ?? iso.date(from: $0) }
                    // The usage endpoint is undocumented, and every number read from it
                    // carries that fact with it rather than passing as official.
                    out.append(LimitWindow(provider: "Claude", key: k,
                                           utilization: util, resetsAt: resets,
                                           source: .experimental))
                } else if depth < 2 {
                    walk(d, depth: depth + 1)
                }
            }
        }
        walk(json, depth: 0)
        return sorted(out)
    }

    // Codex reports windows by length in minutes, not by name, and resets_at is epoch
    // seconds rather than the ISO8601 string the Claude endpoint returns.
    public static func codexRateLimits(_ dict: [String: Any]) -> [LimitWindow] {
        var out: [LimitWindow] = []
        for slot in ["primary", "secondary"] {
            guard let d = dict[slot] as? [String: Any],
                  let pct = number(d["used_percent"]) else { continue }
            let minutes = number(d["window_minutes"]) ?? 0
            let resets = number(d["resets_at"]).map { Date(timeIntervalSince1970: $0) }
            // Codex writes these itself, into its own session files
            out.append(LimitWindow(provider: "Codex", key: key(forWindowMinutes: minutes),
                                   utilization: pct, resetsAt: resets, source: .official))
        }
        return sorted(out)
    }

    static func key(forWindowMinutes m: Double) -> String {
        switch m {
        case 300:   return "five_hour"
        case 10080: return "seven_day"
        case 0:     return "window"
        default:
            // Keep unknown windows visible rather than dropping them silently
            return m < 1440 ? "window_\(Int(m))m" : "window_\(Int(m / 1440))d"
        }
    }

    static func number(_ v: Any?) -> Double? {
        if let d = v as? Double { return d }
        if let i = v as? Int { return Double(i) }
        return nil
    }
}
