// Time-bucketed usage for the dashboard charts. Every entry already carries a timestamp, so
// history comes from the transcripts themselves; nothing extra is recorded.
import Foundation

public struct UsagePoint: Equatable {
    public let start: Date
    public let io: Int
    public let cost: Double
    public let cacheRead: Int
    public let cacheWrite: Int

    public init(start: Date, io: Int, cost: Double, cacheRead: Int, cacheWrite: Int) {
        self.start = start
        self.io = io
        self.cost = cost
        self.cacheRead = cacheRead
        self.cacheWrite = cacheWrite
    }
}

public struct ProviderTrend: Equatable {
    public let provider: String
    public let points: [UsagePoint]

    public var totalIO: Int { points.reduce(0) { $0 + $1.io } }
    public var totalCost: Double { points.reduce(0) { $0 + $1.cost } }
    public var peak: UsagePoint? { points.max(by: { $0.io < $1.io }) }
}

public struct ModelShare: Equatable {
    public let model: String
    public let provider: String
    public let io: Int
    public let cost: Double
    public let priced: Bool
}

public enum Bucketing: Equatable {
    case day
    case hour

    var component: Calendar.Component { self == .day ? .day : .hour }
}

/// How far apart the dated labels on a daily axis should sit.
///
/// Lives here rather than in the chart so it can be tested: the whole point is that a quarter
/// does not print a label every five days and turn the axis into a smear, and that both daily
/// charts use one cadence so the same range cannot read as two different spans stacked up.
public enum DailyAxis {
    /// Roughly six to eight labels at any range RedLine offers.
    public static func strideDays(for range: Int) -> Int {
        switch range {
        case ...7:   return 1
        case ...14:  return 2
        case ...30:  return 5
        case ...60:  return 10
        default:     return 14
        }
    }
}

public enum Trends {
    // Buckets are pre-filled with zeros across the whole range so a chart shows a quiet day
    // as a gap at the baseline rather than skipping the date entirely.
    public static func trend(_ entries: [Entry],
                             by bucketing: Bucketing,
                             count: Int,
                             now: Date = Date(),
                             calendar: Calendar = .current,
                             config: Config) -> [ProviderTrend] {
        guard count > 0 else { return [] }
        let starts = bucketStarts(by: bucketing, count: count, now: now, calendar: calendar)
        guard let first = starts.first else { return [] }

        // provider -> bucket start -> running totals
        var acc: [String: [Date: (io: Int, cost: Double, cr: Int, cw: Int)]] = [:]
        for e in entries where e.ts >= first {
            guard let start = calendar.dateInterval(of: bucketing.component, for: e.ts)?.start
            else { continue }
            let cost = self.cost(of: e, config: config)
            var slot = acc[e.provider]?[start] ?? (0, 0, 0, 0)
            slot.io += e.input + e.output
            slot.cost += cost
            slot.cr += e.cacheRead
            slot.cw += e.cache5m + e.cache1h
            acc[e.provider, default: [:]][start] = slot
        }

        return acc.keys.sorted().map { provider in
            let byStart = acc[provider] ?? [:]
            let points = starts.map { start -> UsagePoint in
                let s = byStart[start] ?? (0, 0, 0, 0)
                return UsagePoint(start: start, io: s.io, cost: s.cost,
                                  cacheRead: s.cr, cacheWrite: s.cw)
            }
            return ProviderTrend(provider: provider, points: points)
        }
    }

    static func bucketStarts(by bucketing: Bucketing, count: Int,
                             now: Date, calendar: Calendar) -> [Date] {
        guard let current = calendar.dateInterval(of: bucketing.component, for: now)?.start
        else { return [] }
        // Oldest first, ending with the bucket now falls in
        return (0..<count).reversed().compactMap {
            calendar.date(byAdding: bucketing.component, value: -$0, to: current)
        }
    }

    public static func byModel(_ entries: [Entry], since: Date,
                               config: Config) -> [ModelShare] {
        var acc: [String: (provider: String, io: Int, cost: Double, priced: Bool)] = [:]
        for e in entries where e.ts >= since {
            let priced = config.price(for: e.model) != nil
            var slot = acc[e.model] ?? (e.provider, 0, 0, priced)
            slot.io += e.input + e.output
            slot.cost += cost(of: e, config: config)
            slot.priced = priced
            acc[e.model] = slot
        }
        return acc.map {
            ModelShare(model: $0.key, provider: $0.value.provider, io: $0.value.io,
                       cost: $0.value.cost, priced: $0.value.priced)
        }
        // Largest first, with a stable tiebreak so the order does not jitter between refreshes
        .sorted { $0.io == $1.io ? $0.model < $1.model : $0.io > $1.io }
    }

    static func cost(of e: Entry, config: Config) -> Double {
        guard let p = config.price(for: e.model) else { return 0 }
        var total = Double(e.input) * p.input
        total += Double(e.output) * p.output
        total += Double(e.cacheRead) * p.cacheRead
        total += Double(e.cache5m) * p.input * 1.25
        total += Double(e.cache1h) * p.input * 2.0
        return total / 1_000_000
    }
}
