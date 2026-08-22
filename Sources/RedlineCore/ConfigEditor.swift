// Reading and changing config.json from outside the macOS app.
//
// The Windows shell needs settings, and the one thing it must not do is learn this schema for
// itself: two validators would eventually disagree about what a legal threshold is. So the
// rules stay here and the shell asks.
//
// Validation is not reimplemented either. A change is applied through Config.apply, the same
// path a hand-edited file takes, and kept only if it survived. Whatever the engine refuses to
// load, this refuses to write.
import Foundation

public enum ConfigEditor {
    public struct Setting {
        public let key: String
        public let summary: String
        /// What a value has to look like, for the error message and for the shell's control
        public let kind: Kind
        /// Reads the value back off a loaded config, so a set can be checked for having taken
        let read: (Config) -> String

        public enum Kind: Equatable {
            case bool
            case number(min: Double, max: Double)
            case choice([String])
            case list([String])
        }
    }

    public enum Outcome: Equatable {
        case unchanged(key: String, value: String)
        case changed(key: String, from: String, to: String)
        case rejected(key: String, reason: String)
        case unknownKey(String)
        case failed(String)
    }

    /// Everything a shell is allowed to offer. Deliberately not every field in Config: the
    /// pricing table and the OAuth endpoints are not settings, they are configuration.
    public static let settings: [Setting] = [
        .init(key: "limitYellowPct",
              summary: "percentage at which a window counts as approaching its limit",
              kind: .number(min: 1, max: 100), read: { fmt($0.limitYellowPct) }),
        .init(key: "limitRedPct",
              summary: "percentage at which a window counts as at its limit",
              kind: .number(min: 1, max: 100), read: { fmt($0.limitRedPct) }),
        .init(key: "pollIntervalSeconds",
              summary: "how often to re-read, in seconds",
              kind: .number(min: 10, max: 86_400), read: { fmt($0.pollIntervalSeconds) }),
        .init(key: "providers",
              summary: "which sources to read, comma separated",
              kind: .list(["Claude", "Codex", "Ollama"]),
              read: { $0.providers.joined(separator: ",") }),
        .init(key: "recordHistory",
              summary: "keep a daily rollup so history outlives the transcripts",
              kind: .bool, read: { String($0.recordHistory) }),
        .init(key: "alerts",
              summary: "say something when a window crosses a threshold",
              kind: .bool, read: { String($0.alerts) }),
        .init(key: "publishSidecar",
              summary: "publish the current windows for other local tools to read",
              kind: .bool, read: { String($0.publishSidecar) }),
        .init(key: "findingsScans",
              summary: "look through transcripts for setup findings",
              kind: .bool, read: { String($0.findingsScans) }),
        .init(key: "agentFleet",
              summary: "show which Claude Code sessions are running",
              kind: .bool, read: { String($0.agentFleet) }),
        .init(key: "mindfulCues",
              summary: "notice unbroken stretches, late hours and long streaks",
              kind: .bool, read: { String($0.mindfulCues) }),
        .init(key: "limitWindows",
              summary: "which windows to show",
              kind: .choice(["all", "session", "week"]), read: { $0.limitWindows }),
        .init(key: "updateChannel",
              summary: "which releases to offer",
              kind: .choice(["stable", "beta"]), read: { $0.updateChannel }),
    ]

    public static func setting(_ key: String) -> Setting? {
        settings.first { $0.key.caseInsensitiveCompare(key) == .orderedSame }
    }

    /// Every setting and its current value.
    public static func current(at url: URL? = nil) -> [(Setting, String)] {
        let config = Config.load(from: url)
        return settings.map { ($0, $0.read(config)) }
    }

    /// Every setting as another language reads it: the value, and what a control needs to
    /// render itself. The CLI prints this verbatim, so it is the contract, and it is tested.
    public static func catalog(at url: URL? = nil) -> [[String: Any]] {
        current(at: url).map { setting, value in
            var row: [String: Any] = ["key": setting.key, "value": value,
                                      "summary": setting.summary]
            switch setting.kind {
            case .bool:
                row["kind"] = "bool"
            case let .number(min, max):
                row["kind"] = "number"; row["min"] = min; row["max"] = max
            case let .choice(allowed):
                row["kind"] = "choice"; row["allowed"] = allowed
            case let .list(allowed):
                row["kind"] = "list"; row["allowed"] = allowed
            }
            return row
        }
    }

    /// What happened, in the shape a shell reads. Reported rather than left to an exit code:
    /// "already that" and "refused" are different answers and only one is worth a message.
    public static func json(for outcome: Outcome) -> [String: Any] {
        switch outcome {
        case let .changed(key, from, to):
            return ["outcome": "changed", "key": key, "from": from, "to": to]
        case let .unchanged(key, value):
            return ["outcome": "unchanged", "key": key, "value": value]
        case let .rejected(key, reason):
            return ["outcome": "rejected", "key": key, "expected": reason]
        case let .unknownKey(key):
            return ["outcome": "unknownKey", "key": key]
        case let .failed(why):
            return ["outcome": "failed", "message": why]
        }
    }

    /// Reading one setting, in the same shape as the answers above.
    public static func readJSON(key: String, value: String) -> [String: Any] {
        ["outcome": "read", "key": key, "value": value]
    }

    public static func value(of key: String, at url: URL? = nil) -> String? {
        setting(key).map { $0.read(Config.load(from: url)) }
    }

    public static func set(_ key: String, to raw: String, at url: URL? = nil) -> Outcome {
        guard let setting = setting(key) else { return .unknownKey(key) }
        let target = url ?? Config.configURL

        guard let parsed = parse(raw, as: setting.kind) else {
            return .rejected(key: setting.key, reason: expected(setting.kind))
        }

        let before = setting.read(Config.load(from: target))

        var json = readRaw(target)
        json[setting.key] = parsed

        // The engine's own rules decide. If the value does not survive being loaded, it was
        // never legal, and nothing is written.
        let after = setting.read(Config.apply(json, to: Config()))
        guard after == normalise(raw, as: setting.kind) else {
            return .rejected(key: setting.key, reason: expected(setting.kind))
        }
        if after == before { return .unchanged(key: setting.key, value: before) }

        guard write(json, to: target) else {
            return .failed("could not write \(target.path)")
        }
        return .changed(key: setting.key, from: before, to: after)
    }

    // MARK: - Parsing

    static func parse(_ raw: String, as kind: Setting.Kind) -> Any? {
        let trimmed = raw.trimmingCharacters(in: .whitespaces)
        switch kind {
        case .bool:
            switch trimmed.lowercased() {
            case "true", "yes", "on", "1":   return true
            case "false", "no", "off", "0":  return false
            default: return nil
            }
        case let .number(min, max):
            guard let n = Double(trimmed), n >= min, n <= max else { return nil }
            return n
        case let .choice(allowed):
            return allowed.first { $0.caseInsensitiveCompare(trimmed) == .orderedSame }
        case let .list(allowed):
            let parts = trimmed.split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            guard !parts.isEmpty else { return nil }
            var picked: [String] = []
            for part in parts {
                guard let match = allowed.first(where: {
                    $0.caseInsensitiveCompare(part) == .orderedSame
                }) else { return nil }
                if !picked.contains(match) { picked.append(match) }
            }
            return picked
        }
    }

    /// What the value will read back as once stored, so a set can tell "took" from "refused".
    static func normalise(_ raw: String, as kind: Setting.Kind) -> String {
        guard let parsed = parse(raw, as: kind) else { return raw }
        switch kind {
        case .bool:              return String(parsed as? Bool ?? false)
        case .number:            return fmt(parsed as? Double ?? 0)
        case .choice:            return parsed as? String ?? raw
        case .list:              return (parsed as? [String] ?? []).joined(separator: ",")
        }
    }

    public static func expected(_ kind: Setting.Kind) -> String {
        switch kind {
        case .bool:                 return "true or false"
        case let .number(min, max): return "a number from \(fmt(min)) to \(fmt(max))"
        case let .choice(allowed):  return "one of " + allowed.joined(separator: ", ")
        case let .list(allowed):    return "any of " + allowed.joined(separator: ", ")
            + ", comma separated"
        }
    }

    /// Whole numbers without a pointless decimal, which is how someone typed them
    static func fmt(_ n: Double) -> String {
        n == n.rounded() && abs(n) < 1e15 ? String(Int(n)) : String(n)
    }

    // MARK: - The file

    /// The file as written, not as interpreted: everything unrecognised is carried through
    /// untouched rather than dropped on the next write.
    static func readRaw(_ url: URL) -> [String: Any] {
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return [:] }
        return json
    }

    static func write(_ json: [String: Any], to url: URL) -> Bool {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: json,
                                                  options: [.prettyPrinted, .sortedKeys])
            try data.write(to: url, options: .atomic)
            return true
        } catch {
            return false
        }
    }
}
