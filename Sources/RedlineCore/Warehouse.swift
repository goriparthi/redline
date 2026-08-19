// Local history that outlives the transcripts it came from. Claude Code prunes its own
// projects directory, so a chart built only from what is still on disk quietly gets
// shallower every week. This rolls each day up once and keeps the rollup.
//
// Two files, both under ~/.local/share/redline/history:
//   daily.jsonl   one record per (day, provider, model), rewritten on merge
//   limits.jsonl  one sample per limit reading, appended, pruned by age
import Foundation

/// One provider's usage of one model on one UTC day. Days are UTC because a record that
/// silently means a different span depending on where it was written cannot be summed.
public struct DailyRecord: Codable, Equatable {
    public var day: String            // yyyy-MM-dd, UTC
    public var provider: String
    public var model: String
    public var input: Int
    public var output: Int
    public var cacheRead: Int
    public var cacheWrite: Int
    public var cost: Double
    public var priced: Bool
    /// Token counts come from the provider; the dollar figure is ours. One record can be
    /// both, so the field describes the cost, which is the part that can mislead.
    public var costBasis: Provenance

    public var io: Int { input + output }

    public init(day: String, provider: String, model: String, input: Int, output: Int,
                cacheRead: Int, cacheWrite: Int, cost: Double, priced: Bool,
                costBasis: Provenance = .localEstimate) {
        self.day = day
        self.provider = provider
        self.model = model
        self.input = input
        self.output = output
        self.cacheRead = cacheRead
        self.cacheWrite = cacheWrite
        self.cost = cost
        self.priced = priced
        self.costBasis = costBasis
    }

    public var key: String { "\(day)|\(provider)|\(model)" }
}

/// One reading of one limit window. Kept so pace and burn rate have something to measure
/// against, and so "how did last week actually go" stops being unanswerable.
public struct LimitSample: Codable, Equatable {
    public var at: Date
    public var provider: String
    public var key: String
    public var utilization: Double
    public var resetsAt: Date?
    public var source: Provenance

    public init(at: Date, provider: String, key: String, utilization: Double,
                resetsAt: Date?, source: Provenance) {
        self.at = at
        self.provider = provider
        self.key = key
        self.utilization = utilization
        self.resetsAt = resetsAt
        self.source = source
    }

    /// Samples belong to the same window instance only when they share a reset time. A new
    /// reset means the window rolled over and the two readings must never be differenced.
    public func sameWindowInstance(as other: LimitSample) -> Bool {
        guard provider == other.provider, key == other.key else { return false }
        switch (resetsAt, other.resetsAt) {
        case (nil, nil): return true
        case let (a?, b?): return abs(a.timeIntervalSince(b)) < 60
        default: return false
        }
    }
}

public final class Warehouse {
    public static let dayFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        f.timeZone = TimeZone(identifier: "UTC")
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    /// How long limit samples are kept. Long enough to see a month of weekly windows,
    /// short enough that the file stays a few hundred kilobytes.
    public static let sampleRetentionDays = 60

    private let root: URL
    private let fm = FileManager.default

    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/redline/history")
    }

    public var dailyURL: URL { root.appendingPathComponent("daily.jsonl") }
    public var limitsURL: URL { root.appendingPathComponent("limits.jsonl") }

    public static func day(for date: Date) -> String { dayFormatter.string(from: date) }

    // MARK: - Daily rollups

    /// Folds a scan into the stored history. Returns how many records changed.
    ///
    /// The merge rule is "the fullest reading of a day wins": a record is replaced only when
    /// the incoming one carries at least as many tokens. Transcripts are pruned, so a later
    /// scan of an old day legitimately returns less than an earlier one did, and a plain
    /// overwrite would erase history a week at a time.
    @discardableResult
    public func merge(entries: [Entry], config: Config, now: Date = Date()) -> Int {
        let incoming = Warehouse.rollup(entries: entries, config: config)
        guard !incoming.isEmpty else { return 0 }
        var stored = Dictionary(uniqueKeysWithValues: load().map { ($0.key, $0) })
        var changed = 0
        for rec in incoming {
            if let existing = stored[rec.key], existing.io + existing.cacheRead
                >= rec.io + rec.cacheRead { continue }
            stored[rec.key] = rec
            changed += 1
        }
        guard changed > 0 else { return 0 }
        write(stored.values.sorted { $0.key < $1.key })
        return changed
    }

    public static func rollup(entries: [Entry], config: Config) -> [DailyRecord] {
        var acc: [String: DailyRecord] = [:]
        for e in entries {
            let day = Warehouse.day(for: e.ts)
            let key = "\(day)|\(e.provider)|\(e.model)"
            let price = config.price(for: e.model)
            var rec = acc[key] ?? DailyRecord(day: day, provider: e.provider, model: e.model,
                                              input: 0, output: 0, cacheRead: 0, cacheWrite: 0,
                                              cost: 0, priced: price != nil)
            rec.input += e.input
            rec.output += e.output
            rec.cacheRead += e.cacheRead
            rec.cacheWrite += e.cache5m + e.cache1h
            if let p = price {
                var total = Double(e.input) * p.input
                total += Double(e.output) * p.output
                total += Double(e.cacheRead) * p.cacheRead
                total += Double(e.cache5m) * p.input * 1.25
                total += Double(e.cache1h) * p.input * 2.0
                rec.cost += total / 1_000_000
            } else {
                rec.priced = false
                // An unpriced model has no cost to state, so the basis says so rather than
                // letting a zero read as free.
                rec.costBasis = .unknown
            }
            acc[key] = rec
        }
        return acc.values.sorted { $0.key < $1.key }
    }

    public func load() -> [DailyRecord] {
        guard let text = try? String(contentsOf: dailyURL, encoding: .utf8) else { return [] }
        let decoder = JSONDecoder()
        var out: [DailyRecord] = []
        text.enumerateLines { line, _ in
            guard let data = line.data(using: .utf8),
                  let rec = try? decoder.decode(DailyRecord.self, from: data) else { return }
            out.append(rec)
        }
        return out
    }

    public func records(since: Date? = nil, until: Date? = nil,
                        provider: String? = nil) -> [DailyRecord] {
        let from = since.map(Warehouse.day(for:))
        let to = until.map(Warehouse.day(for:))
        return load().filter { rec in
            if let from, rec.day < from { return false }
            if let to, rec.day > to { return false }
            if let provider, rec.provider.caseInsensitiveCompare(provider) != .orderedSame {
                return false
            }
            return true
        }.sorted { $0.key < $1.key }
    }

    /// One row per day, provider folded together. What a history chart or an export wants.
    public static func byDay(_ records: [DailyRecord]) -> [(day: String, io: Int,
                                                            cost: Double, priced: Bool)] {
        var acc: [String: (io: Int, cost: Double, priced: Bool)] = [:]
        for r in records {
            var slot = acc[r.day] ?? (0, 0, true)
            slot.io += r.io
            slot.cost += r.cost
            slot.priced = slot.priced && r.priced
            acc[r.day] = slot
        }
        return acc.map { (day: $0.key, io: $0.value.io, cost: $0.value.cost,
                          priced: $0.value.priced) }
            .sorted { $0.day < $1.day }
    }

    private func write(_ records: [DailyRecord]) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        var text = ""
        for rec in records {
            guard let data = try? encoder.encode(rec),
                  let line = String(data: data, encoding: .utf8) else { continue }
            text += line + "\n"
        }
        atomicWrite(text, to: dailyURL)
    }

    // MARK: - Limit samples

    /// Records a reading, skipping the ones that say nothing new. A sample is kept when the
    /// percentage moved, the window rolled over, or `minInterval` has passed since the last
    /// one, so a five minute poll does not write a file full of identical rows.
    @discardableResult
    public func recordLimits(_ windows: [LimitWindow], at: Date = Date(),
                             minInterval: TimeInterval = 900) -> Int {
        guard !windows.isEmpty else { return 0 }
        let existing = limitSamples()
        var appended: [LimitSample] = []
        for w in windows where !w.isUninformative {
            let sample = LimitSample(at: at, provider: w.provider, key: w.key,
                                     utilization: w.utilization, resetsAt: w.resetsAt,
                                     source: w.source)
            let last = existing.last { $0.provider == w.provider && $0.key == w.key }
            if let last, last.sameWindowInstance(as: sample),
               last.utilization == sample.utilization,
               at.timeIntervalSince(last.at) < minInterval { continue }
            // A reading older than the newest stored one would break the ordering every
            // reader assumes, and a clock that jumped is not evidence of usage.
            if let last, sample.at <= last.at { continue }
            appended.append(sample)
        }
        guard !appended.isEmpty else { return 0 }
        append(appended, pruningBefore: at.addingTimeInterval(
            -Double(Warehouse.sampleRetentionDays) * 86400), existing: existing)
        return appended.count
    }

    public func limitSamples(provider: String? = nil, key: String? = nil,
                             since: Date? = nil) -> [LimitSample] {
        guard let text = try? String(contentsOf: limitsURL, encoding: .utf8) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        var out: [LimitSample] = []
        text.enumerateLines { line, _ in
            guard let data = line.data(using: .utf8),
                  let s = try? decoder.decode(LimitSample.self, from: data) else { return }
            if let provider, s.provider.caseInsensitiveCompare(provider) != .orderedSame {
                return
            }
            if let key, s.key != key { return }
            if let since, s.at < since { return }
            out.append(s)
        }
        return out.sorted { $0.at < $1.at }
    }

    private func append(_ samples: [LimitSample], pruningBefore cutoff: Date,
                        existing: [LimitSample]) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        func line(_ s: LimitSample) -> String? {
            guard let data = try? encoder.encode(s) else { return nil }
            return String(data: data, encoding: .utf8)
        }
        let stale = existing.contains { $0.at < cutoff }
        if stale {
            // Rewrite rather than append when there is anything to drop; appending forever
            // is how a sidecar file becomes a problem nobody notices until it is one.
            let kept = (existing.filter { $0.at >= cutoff } + samples)
            atomicWrite(kept.compactMap(line).joined(separator: "\n") + "\n", to: limitsURL)
            return
        }
        let text = samples.compactMap(line).joined(separator: "\n") + "\n"
        guard let data = text.data(using: .utf8) else { return }
        ensureDirectory()
        if let handle = try? FileHandle(forWritingTo: limitsURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            atomicWrite(text, to: limitsURL)
        }
    }

    // MARK: - Files

    private func ensureDirectory() {
        try? fm.createDirectory(at: root, withIntermediateDirectories: true)
    }

    private func atomicWrite(_ text: String, to url: URL) {
        ensureDirectory()
        guard let data = text.data(using: .utf8) else { return }
        try? data.write(to: url, options: .atomic)
        // Usage and cost are nobody else's business on a shared machine, same as the snapshot
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    /// Bytes on disk, for the settings row that says what this is costing you.
    public var sizeBytes: Int64 {
        [dailyURL, limitsURL].reduce(Int64(0)) { total, url in
            let size = (try? fm.attributesOfItem(atPath: url.path)[.size]) as? Int64 ?? 0
            return total + size
        }
    }

    public func removeAll() {
        try? fm.removeItem(at: dailyURL)
        try? fm.removeItem(at: limitsURL)
    }
}
