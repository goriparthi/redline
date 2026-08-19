// How fast a limit window is being spent, and whether that runs out before it resets.
//
// A percentage answers "how much is gone". The question a person actually has is "will this
// stop me before it rolls over", and that needs a rate. Two rates are available and they are
// not equally good, so which one was used travels with the answer.
import Foundation

public struct Pace: Equatable {
    /// Which rate the projection used. Measured beats assumed, and the caller says which
    /// one it got rather than presenting both as the same kind of claim.
    public enum Basis: Equatable {
        /// Differenced from stored readings inside this same window instance.
        case measured(samples: Int, over: TimeInterval)
        /// Utilization divided by time elapsed in the window. Always available once a
        /// window has run for a while, and blind to a burst that just started.
        case windowAverage
    }

    public let provider: String
    public let key: String
    public let utilization: Double
    public let resetsAt: Date?
    /// Utilization points per hour.
    public let ratePerHour: Double
    public let basis: Basis
    /// How far through the window we are, 0 to 1. Nil when the window's length is unknown.
    public let elapsedFraction: Double?
    /// When utilization reaches 100 at this rate. Nil when the rate is zero or already there.
    public let exhaustsAt: Date?

    /// Ahead of pace by this much, as a fraction. Positive means spending faster than the
    /// clock, which is the whole point of showing it next to a bar.
    public var paceDelta: Double? {
        guard let elapsedFraction else { return nil }
        return utilization / 100 - elapsedFraction
    }

    /// True when the cap arrives before the reset does, which is the one case worth
    /// interrupting someone about.
    public var hitsLimitBeforeReset: Bool {
        guard let exhaustsAt, let resetsAt else { return false }
        return exhaustsAt < resetsAt
    }

    public func timeToLimit(now: Date = Date()) -> TimeInterval? {
        guard let exhaustsAt, exhaustsAt > now else { return nil }
        return exhaustsAt.timeIntervalSince(now)
    }

    public func timeToReset(now: Date = Date()) -> TimeInterval? {
        guard let resetsAt, resetsAt > now else { return nil }
        return resetsAt.timeIntervalSince(now)
    }

    /// One line for a menu row or a rail subtitle. Says the cap only when the cap is the
    /// news; otherwise it says how the window is tracking against its own clock.
    public func summary(now: Date = Date()) -> String? {
        if utilization >= 100 { return "limit reached" }
        if hitsLimitBeforeReset, let toLimit = timeToLimit(now: now) {
            var line = "~\(Pace.short(toLimit)) to limit"
            if let toReset = timeToReset(now: now), toReset > toLimit {
                line += ", \(Pace.short(toReset - toLimit)) before reset"
            }
            return line
        }
        guard let delta = paceDelta else { return nil }
        // Inside five points of the clock is not a signal, it is noise
        if abs(delta) < 0.05 { return "on pace" }
        // Named against the clock rather than "pace" on its own: ahead of the clock means
        // spending faster than the window refills, which is the direction that costs you.
        return delta > 0 ? "\(Int((delta * 100).rounded())) points ahead of the clock"
                         : "\(Int((-delta * 100).rounded())) points to spare"
    }

    /// The same reading with the second clause dropped, for a place where the row already
    /// says when the window resets. "42m before reset" is arithmetic the reader can do from
    /// a reset time they are already looking at.
    public func compact(now: Date = Date()) -> String? {
        if utilization >= 100 { return "limit reached" }
        if hitsLimitBeforeReset, let toLimit = timeToLimit(now: now) {
            return "~\(Pace.short(toLimit)) to limit"
        }
        return summary(now: now)
    }

    public var basisNote: String {
        switch basis {
        case .measured(let samples, let over):
            return "measured from \(samples) readings over \(Pace.short(over))"
        case .windowAverage:
            return "averaged across this window so far"
        }
    }

    /// "2h 10m", "40m", "3d 4h". Compact enough for a menu row, never rounded to nothing.
    public static func short(_ interval: TimeInterval) -> String {
        let total = max(0, Int(interval.rounded()))
        let days = total / 86400
        let hours = (total % 86400) / 3600
        let minutes = (total % 3600) / 60
        if days > 0 { return hours > 0 ? "\(days)d \(hours)h" : "\(days)d" }
        if hours > 0 { return minutes > 0 ? "\(hours)h \(minutes)m" : "\(hours)h" }
        if minutes > 0 { return "\(minutes)m" }
        return "under a minute"
    }
}

public enum PaceEstimator {
    /// Minimum span between the readings a measured rate is differenced from, scaled to the
    /// window. Ten minutes of readings describes a five hour window usefully and says
    /// nothing about a weekly one: a busy quarter of an hour extrapolated across seven days
    /// produced "23h to limit" on a window that was 5% used and a week from its reset.
    public static func minMeasuredSpan(forLength length: TimeInterval) -> TimeInterval {
        max(600, length / 24)
    }
    /// Minimum time a window must have run before its average says anything.
    public static let minElapsed: TimeInterval = 900

    /// How far back a measured rate looks, scaled to the window: a five hour window wants
    /// the last stretch, a weekly one would be dominated by a single afternoon.
    public static func lookback(forLength length: TimeInterval) -> TimeInterval {
        min(max(length / 8, 1800), 12 * 3600)
    }

    /// Nil when nothing honest can be said: no length, no elapsed time, or no consumption.
    public static func pace(for window: LimitWindow, samples: [LimitSample] = [],
                            now: Date = Date()) -> Pace? {
        guard let length = window.length, let resetsAt = window.resetsAt else { return nil }
        let start = resetsAt.addingTimeInterval(-length)
        let elapsed = now.timeIntervalSince(start)
        guard elapsed > 0, elapsed <= length + 3600 else { return nil }
        let fraction = min(1, max(0, elapsed / length))

        var rate: Double?
        var basis = Pace.Basis.windowAverage

        // Measured first: readings inside this same window instance, recent enough to
        // describe what is happening now rather than what happened this morning.
        let current = LimitSample(at: now, provider: window.provider, key: window.key,
                                  utilization: window.utilization, resetsAt: window.resetsAt,
                                  source: window.source)
        let cutoff = now.addingTimeInterval(-lookback(forLength: length))
        let recent = samples
            .filter { $0.provider == window.provider && $0.key == window.key }
            .filter { $0.at >= cutoff && $0.at <= now }
            .filter { $0.sameWindowInstance(as: current) }
            .sorted { $0.at < $1.at }
        if let first = recent.first {
            let span = now.timeIntervalSince(first.at)
            let climb = window.utilization - first.utilization
            if span >= minMeasuredSpan(forLength: length), climb > 0 {
                rate = climb / (span / 3600)
                basis = .measured(samples: recent.count + 1, over: span)
            }
        }

        if rate == nil {
            guard elapsed >= minElapsed, window.utilization > 0 else { return nil }
            rate = window.utilization / (elapsed / 3600)
        }
        guard let ratePerHour = rate, ratePerHour > 0 else { return nil }

        let remaining = max(0, 100 - window.utilization)
        let exhausts = remaining > 0
            ? now.addingTimeInterval(remaining / ratePerHour * 3600)
            : now
        return Pace(provider: window.provider, key: window.key,
                    utilization: window.utilization, resetsAt: resetsAt,
                    ratePerHour: ratePerHour, basis: basis,
                    elapsedFraction: fraction, exhaustsAt: exhausts)
    }

    /// Every window that can say something, worst first. The one that will stop you soonest
    /// is the one worth showing when there is room for only one.
    public static func paces(for windows: [LimitWindow], samples: [LimitSample] = [],
                             now: Date = Date()) -> [Pace] {
        windows.compactMap { pace(for: $0, samples: samples, now: now) }
            .sorted { a, b in
                switch (a.timeToLimit(now: now), b.timeToLimit(now: now)) {
                case let (x?, y?): return x < y
                case (nil, _?):    return false
                case (_?, nil):    return true
                default:           return a.utilization > b.utilization
                }
            }
    }
}
