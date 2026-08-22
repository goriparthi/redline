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

    static let months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun",
                         "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]

    /// The day a bucket belongs to, unambiguously. Built by hand rather than with a
    /// DateFormatter, which is where the cost formatting went wrong on Linux.
    public static func key(for start: Date, calendar: Calendar = .current) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: start)
        return String(format: "%04d-%02d-%02d",
                      parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }

    /// What an axis prints. Published by the engine so two shells cannot label one day two
    /// ways, and English on purpose, like every other string RedLine shows.
    public static func label(for start: Date, calendar: Calendar = .current) -> String {
        let parts = calendar.dateComponents([.month, .day], from: start)
        let month = min(max((parts.month ?? 1) - 1, 0), 11)
        return "\(months[month]) \(parts.day ?? 0)"
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

    /// Everything a chart in another language needs, in one shape.
    ///
    /// Pure on purpose: the command gathers the data, this decides what is published, and a
    /// test can pin every value without a warehouse or a clock. See SettingsContractTests'
    /// twin for trends, which both languages read.
    public static func report(days: Int, series: [UsagePoint], providers: [ProviderTrend],
                              models: [ModelShare],
                              calendar: Calendar = .current) -> [String: Any] {
        [
            "days": days,
            "label_every_days": DailyAxis.strideDays(for: days),
            "day_basis": "local",
            "tokens": models.reduce(0) { $0 + $1.io },
            "cost_usd": models.reduce(0.0) { $0 + $1.cost },
            // One unpriced model makes every total a floor, and saying so is the whole
            // difference between a number and a guess
            "has_unpriced": models.contains { !$0.priced },
            "series": series.map { point($0, calendar: calendar) },
            "providers": providers.map {
                ["provider": $0.provider, "tokens": $0.totalIO, "cost_usd": $0.totalCost,
                 "points": $0.points.map { point($0, calendar: calendar) }]
            },
            "models": models.map {
                ["model": $0.model, "provider": $0.provider, "tokens": $0.io,
                 "cost_usd": $0.cost, "priced": $0.priced]
            },
        ]
    }

    static func point(_ spot: UsagePoint, calendar: Calendar) -> [String: Any] {
        ["day": DailyAxis.key(for: spot.start, calendar: calendar),
         "label": DailyAxis.label(for: spot.start, calendar: calendar),
         "tokens": spot.io, "cost_usd": spot.cost]
    }

    /// Every provider's buckets added together, for a chart that draws one series. The
    /// buckets are the same starts in the same order for every provider, so this lines up.
    public static func combine(_ trends: [ProviderTrend]) -> [UsagePoint] {
        guard let first = trends.first else { return [] }
        return first.points.indices.map { i in
            var io = 0
            var cost = 0.0
            var cacheRead = 0
            var cacheWrite = 0
            for trend in trends where i < trend.points.count {
                io += trend.points[i].io
                cost += trend.points[i].cost
                cacheRead += trend.points[i].cacheRead
                cacheWrite += trend.points[i].cacheWrite
            }
            return UsagePoint(start: first.points[i].start, io: io, cost: cost,
                              cacheRead: cacheRead, cacheWrite: cacheWrite)
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
