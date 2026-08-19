// When to interrupt someone, and when to stay quiet.
//
// The decision lives here rather than in the app so it can be tested without posting a
// notification. Three rules govern all of it:
//   1. Never fire from a stale reading. A number nobody can vouch for is not news.
//   2. Once per window instance per threshold. A window that sits at 86% for an hour is
//      one event, not twelve.
//   3. A reset is worth saying only for a window that was actually being used.
import Foundation

public struct AlertEvent: Equatable {
    public enum Kind: Equatable {
        /// Crossed a percentage the user set.
        case threshold(Int)
        /// Reached the cap. Separate from a threshold because the wording is different and
        /// because it is the one event that is never merely advisory.
        case limitReached
        /// Projected to reach the cap before the window resets.
        case projection
        /// The window rolled over and capacity came back.
        case reset
    }

    public let kind: Kind
    public let provider: String
    public let key: String
    public let title: String
    public let body: String
    /// Stable per event, so a delivery layer can avoid posting the same thing twice.
    public let id: String

    public init(kind: Kind, provider: String, key: String, title: String, body: String,
                id: String) {
        self.kind = kind
        self.provider = provider
        self.key = key
        self.title = title
        self.body = body
        self.id = id
    }
}

/// What has already been said, keyed by window instance so a new window re-arms everything.
public struct AlertState: Codable, Equatable {
    public struct Seen: Codable, Equatable {
        public var instance: String
        public var utilization: Double
        public var at: Date
        /// Thresholds already announced for this instance, plus 100 for the cap and -1 for
        /// the projection, which is one-shot in the same way.
        public var fired: [Int]

        public init(instance: String, utilization: Double, at: Date, fired: [Int]) {
            self.instance = instance
            self.utilization = utilization
            self.at = at
            self.fired = fired
        }
    }

    public var windows: [String: Seen] = [:]

    public init() {}

    public mutating func prune(before cutoff: Date) {
        windows = windows.filter { $0.value.at >= cutoff }
    }
}

public enum Alerting {
    /// Marker slots inside `fired`, kept out of the 0-100 range the real thresholds occupy.
    static let projectionSlot = -1
    static let limitSlot = 100

    /// A reset is only news if the window was being used. Below this it is noise about a
    /// window nobody was near.
    public static let resetFloor: Double = 25

    /// Fires the projection warning only once the cap is close enough to act on. Earlier
    /// than this and a projection is a forecast, not a warning.
    public static let projectionHorizon: TimeInterval = 3600

    public static func thresholds(for config: Config) -> [Int] {
        var out = Set<Int>([Int(config.limitYellowPct), Int(config.limitRedPct), 95])
        out = out.filter { $0 > 0 && $0 < 100 }
        return out.sorted()
    }

    /// Evaluates one poll's worth of windows against what has already been said.
    ///
    /// `isStale` is the caller's judgement about whether a reading describes now, asked per
    /// window because they age separately: Claude's come from a feed that goes quiet between
    /// sessions while Codex's are rewritten from disk on every poll. A stale window still
    /// updates its recorded utilization, so the next fresh one does not mistake a gap for a
    /// reset, but it never produces an event.
    public static func evaluate(windows: [LimitWindow], paces: [Pace] = [], config: Config,
                                now: Date = Date(),
                                isStale: (LimitWindow) -> Bool = { _ in false },
                                state: inout AlertState) -> [AlertEvent] {
        var events: [AlertEvent] = []
        let levels = thresholds(for: config)
        for window in windows where !window.isUninformative {
            let stale = isStale(window)
            let id = window.id
            let instance = instanceKey(window)
            let previous = state.windows[id]
            var seen = previous.map { $0.instance == instance ? $0
                : AlertState.Seen(instance: instance, utilization: window.utilization,
                                  at: now, fired: []) }
                ?? AlertState.Seen(instance: instance, utilization: window.utilization,
                                   at: now, fired: [])

            // A rollover: same window, new instance, and the old one had been used
            let rolledOver = previous != nil && previous!.instance != instance
                && previous!.utilization >= resetFloor && window.utilization < previous!.utilization
            if rolledOver, !stale, config.alerts {
                events.append(AlertEvent(
                    kind: .reset, provider: window.provider, key: window.key,
                    title: "\(window.provider) \(window.displayName) reset",
                    body: "Back to \(Int((100 - window.utilization).rounded()))% remaining.",
                    id: "\(instance)|reset"))
            }

            if !stale, config.alerts {
                if window.utilization >= 100, !seen.fired.contains(limitSlot) {
                    seen.fired.append(limitSlot)
                    events.append(AlertEvent(
                        kind: .limitReached, provider: window.provider, key: window.key,
                        title: "\(window.provider) \(window.displayName) is at its limit",
                        body: resetPhrase(window, now: now) ?? "No reset time reported.",
                        id: "\(instance)|100"))
                } else {
                    for level in levels where window.utilization >= Double(level)
                        && !seen.fired.contains(level) {
                        seen.fired.append(level)
                        events.append(AlertEvent(
                            kind: .threshold(level), provider: window.provider,
                            key: window.key,
                            title: "\(window.provider) \(window.displayName) at "
                                 + "\(Int(window.utilization.rounded()))%",
                            body: [remainingPhrase(window), resetPhrase(window, now: now)]
                                .compactMap { $0 }.joined(separator: " · "),
                            id: "\(instance)|\(level)"))
                    }
                }

                if let pace = paces.first(where: {
                    $0.provider == window.provider && $0.key == window.key
                }), pace.hitsLimitBeforeReset, window.utilization < 100,
                   let toLimit = pace.timeToLimit(now: now), toLimit <= projectionHorizon,
                   !seen.fired.contains(projectionSlot) {
                    seen.fired.append(projectionSlot)
                    events.append(AlertEvent(
                        kind: .projection, provider: window.provider, key: window.key,
                        title: "\(window.provider) \(window.displayName) will run out first",
                        body: "About \(Pace.short(toLimit)) left at the current rate"
                            + (pace.timeToReset(now: now).map {
                                ", \(Pace.short($0)) until it resets" } ?? "")
                            + ".",
                        id: "\(instance)|projection"))
                }
            }

            seen.instance = instance
            seen.utilization = window.utilization
            seen.at = now
            state.windows[id] = seen
        }
        state.prune(before: now.addingTimeInterval(-30 * 86400))
        return events
    }

    /// Windows are the same instance while they share a reset time. Without one there is
    /// nothing to key on, so the window counts as a single ongoing instance.
    static func instanceKey(_ window: LimitWindow) -> String {
        guard let r = window.resetsAt else { return "\(window.id)|open" }
        return "\(window.id)|\(Int(r.timeIntervalSince1970))"
    }

    static func remainingPhrase(_ window: LimitWindow) -> String? {
        let left = 100 - window.utilization
        guard left > 0 else { return nil }
        return "\(Int(left.rounded()))% left"
    }

    static func resetPhrase(_ window: LimitWindow, now: Date) -> String? {
        guard let r = window.resetsAt, r > now else { return nil }
        return "resets in \(Pace.short(r.timeIntervalSince(now)))"
    }
}

/// Where the alert state lives. Its own file rather than the config, because it is a record
/// of what happened, not something anyone should hand-edit.
public enum AlertStore {
    public static func url(home: URL? = nil) -> URL {
        (home ?? RedlineHome.url)
            .appendingPathComponent(".local/share/redline/alerts.json")
    }

    public static func load(from url: URL? = nil) -> AlertState {
        let url = url ?? AlertStore.url()
        guard let data = try? Data(contentsOf: url) else { return AlertState() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode(AlertState.self, from: data)) ?? AlertState()
    }

    @discardableResult
    public static func save(_ state: AlertState, to url: URL? = nil) -> Bool {
        let url = url ?? AlertStore.url()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(state) else { return false }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try data.write(to: url, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: url.path)
            return true
        } catch {
            return false
        }
    }
}
