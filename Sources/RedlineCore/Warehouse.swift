// Local history that outlives the transcripts it came from. Claude Code prunes its own
// projects directory, so a chart built only from what is still on disk quietly gets
// shallower every week.
//
// One SQLite database at ~/.local/share/redline/redline.db, holding four things:
//   entries        one row per usage record, deduped, the fine grained store
//   daily          one row per (day, provider, model), the long memory
//   limit_samples  one row per limit reading, for pace and for "how did last week go"
//   ingest_state   how far into each transcript we have already read
//
// Why both entries and daily, when daily is derivable: the daily rows outlive their own
// entries. Entries age out on a retention pass, and the transcripts they came from age out
// on somebody else's schedule, but a day once recorded is never allowed to shrink. The
// derived table is the durable one on purpose.
//
// The JSONL files this replaced are imported once on first open and left on disk under a
// .migrated suffix. JSONL remains the export format (`redline history --csv`) and the
// sidecar contract; it is no longer the storage format.
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

/// How far into one transcript the ingest has read. Transcripts are append only, so the
/// next scan starts at the offset rather than re-reading megabytes to find the last line.
public struct IngestMark: Equatable {
    public var path: String
    public var provider: String
    public var size: Int
    public var byteOffset: Int
    public var mtime: Date

    public init(path: String, provider: String, size: Int, byteOffset: Int, mtime: Date) {
        self.path = path
        self.provider = provider
        self.size = size
        self.byteOffset = byteOffset
        self.mtime = mtime
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
    /// short enough that the table stays small.
    public static let sampleRetentionDays = 60
    /// How long individual entries are kept. The daily rollups are forever; this is the
    /// grain that answers hour of day and session length questions, and a year of it is a
    /// few megabytes.
    public static let entryRetentionDays = 365

    private let root: URL
    private let fm = FileManager.default
    private var db: Database?
    private var opened = false
    /// Days whose rollup is out of date because entries landed in them since the last pass.
    /// Kept here rather than returned from every ingest call so the callers stay simple and
    /// so a poll rolls up exactly the days it learned something about, and no others.
    private var pendingDays = Set<String>()

    public init(root: URL? = nil) {
        self.root = root ?? RedlineHome.url
            .appendingPathComponent(".local/share/redline/history")
    }

    public var databaseURL: URL { root.appendingPathComponent("redline.db") }
    /// The pre-0.7 files. Read once at migration, then renamed rather than deleted: the
    /// import is the only irreversible step in this change, so it stays reversible.
    public var legacyDailyURL: URL { root.appendingPathComponent("daily.jsonl") }
    public var legacyLimitsURL: URL { root.appendingPathComponent("limits.jsonl") }

    public static func day(for date: Date) -> String { dayFormatter.string(from: date) }

    // MARK: - Connection

    /// Opened lazily so constructing a Warehouse never touches the disk, and failures are
    /// survivable: a menu bar app with a broken history file still shows live percentages.
    private func connection() -> Database? {
        if opened { return db }
        opened = true
        do {
            let database = try Database(url: databaseURL)
            try migrate(database)
            db = database
        } catch {
            db = nil
        }
        return db
    }

    private func migrate(_ database: Database) throws {
        if database.userVersion < 1 {
            try database.execute("""
                CREATE TABLE IF NOT EXISTS entries (
                    dedup      TEXT PRIMARY KEY,
                    provider   TEXT NOT NULL,
                    ts         REAL NOT NULL,
                    model      TEXT NOT NULL,
                    input      INTEGER NOT NULL DEFAULT 0,
                    output     INTEGER NOT NULL DEFAULT 0,
                    cache_read INTEGER NOT NULL DEFAULT 0,
                    cache_5m   INTEGER NOT NULL DEFAULT 0,
                    cache_1h   INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS entries_ts ON entries(ts);
                CREATE INDEX IF NOT EXISTS entries_provider_ts ON entries(provider, ts);

                CREATE TABLE IF NOT EXISTS daily (
                    day         TEXT NOT NULL,
                    provider    TEXT NOT NULL,
                    model       TEXT NOT NULL,
                    input       INTEGER NOT NULL DEFAULT 0,
                    output      INTEGER NOT NULL DEFAULT 0,
                    cache_read  INTEGER NOT NULL DEFAULT 0,
                    cache_write INTEGER NOT NULL DEFAULT 0,
                    cost        REAL NOT NULL DEFAULT 0,
                    priced      INTEGER NOT NULL DEFAULT 1,
                    cost_basis  TEXT NOT NULL DEFAULT 'local_estimate',
                    PRIMARY KEY (day, provider, model)
                );

                CREATE TABLE IF NOT EXISTS limit_samples (
                    at          REAL NOT NULL,
                    provider    TEXT NOT NULL,
                    key         TEXT NOT NULL,
                    utilization REAL NOT NULL,
                    resets_at   REAL,
                    source      TEXT NOT NULL DEFAULT 'unknown',
                    PRIMARY KEY (provider, key, at)
                );
                CREATE INDEX IF NOT EXISTS limit_samples_at ON limit_samples(at);

                CREATE TABLE IF NOT EXISTS ingest_state (
                    path        TEXT PRIMARY KEY,
                    provider    TEXT NOT NULL,
                    size        INTEGER NOT NULL,
                    byte_offset INTEGER NOT NULL,
                    mtime       REAL NOT NULL,
                    seen_at     REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                """)
            try database.setUserVersion(1)
            importLegacyFiles(into: database)
        }
    }

    /// Folds the pre-0.7 JSONL into the tables, once. A failure here must not stop the app:
    /// the worst case is history that starts today, and the files stay on disk either way.
    private func importLegacyFiles(into database: Database) {
        if let text = try? String(contentsOf: legacyDailyURL, encoding: .utf8) {
            let decoder = JSONDecoder()
            var records: [DailyRecord] = []
            text.enumerateLines { line, _ in
                guard let data = line.data(using: .utf8),
                      let rec = try? decoder.decode(DailyRecord.self, from: data) else { return }
                records.append(rec)
            }
            if !records.isEmpty {
                try? database.transaction {
                    for rec in records { try upsertDaily(rec, into: database) }
                }
                try? fm.moveItem(at: legacyDailyURL,
                                 to: legacyDailyURL.appendingPathExtension("migrated"))
            }
        }
        if let text = try? String(contentsOf: legacyLimitsURL, encoding: .utf8) {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            var samples: [LimitSample] = []
            text.enumerateLines { line, _ in
                guard let data = line.data(using: .utf8),
                      let s = try? decoder.decode(LimitSample.self, from: data) else { return }
                samples.append(s)
            }
            if !samples.isEmpty {
                try? database.transaction {
                    for s in samples { try insertSample(s, into: database) }
                }
                try? fm.moveItem(at: legacyLimitsURL,
                                 to: legacyLimitsURL.appendingPathExtension("migrated"))
            }
        }
    }

    // MARK: - Entries

    /// Stores usage records, ignoring ones already held. Returns how many were new.
    ///
    /// Dedup is the provider's own message identity where there is one, and the transcript
    /// path plus byte offset where there is not: Codex and Ollama records carry no id, and
    /// the file position is the only thing about them that is stable across re-reads.
    @discardableResult
    public func ingest(_ entries: [Entry]) -> Int {
        guard let db = connection(), !entries.isEmpty else { return 0 }
        var added = 0
        try? db.transaction {
            for e in entries {
                var changed = false
                try db.query("""
                    INSERT INTO entries
                        (dedup, provider, ts, model, input, output, cache_read, cache_5m, cache_1h)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(dedup) DO NOTHING
                    RETURNING 1
                    """, [.text(e.dedupKey), .text(e.provider), .date(e.ts), .text(e.model),
                          .int(e.input), .int(e.output), .int(e.cacheRead),
                          .int(e.cache5m), .int(e.cache1h)]) { _ in changed = true }
                if changed {
                    added += 1
                    pendingDays.insert(Warehouse.day(for: e.ts))
                }
            }
        }
        return added
    }

    /// Recomputes the rollups for every day that has learned something since the last call.
    /// Returns how many stored rows changed, which is zero on a quiet poll.
    @discardableResult
    public func rollupPending(config: Config) -> Int {
        guard let db = connection(), !pendingDays.isEmpty else { return 0 }
        let days = pendingDays
        pendingDays.removeAll()
        var changed = 0
        try? db.transaction {
            for day in days.sorted() {
                for rec in rollup(day: day, config: config, database: db) {
                    if try upsertDaily(rec, into: db) { changed += 1 }
                }
            }
        }
        return changed
    }

    public func entries(since: Date? = nil, until: Date? = nil,
                        provider: String? = nil) -> [Entry] {
        guard let db = connection() else { return [] }
        var sql = """
            SELECT dedup, provider, ts, model, input, output, cache_read, cache_5m, cache_1h
            FROM entries WHERE 1 = 1
            """
        var bindings: [Database.Value] = []
        if let since { sql += " AND ts >= ?"; bindings.append(.date(since)) }
        if let until { sql += " AND ts <= ?"; bindings.append(.date(until)) }
        if let provider { sql += " AND provider = ?"; bindings.append(.text(provider)) }
        sql += " ORDER BY ts"
        var out: [Entry] = []
        try? db.query(sql, bindings) { row in
            out.append(Entry(provider: row.string(1), key: row.string(0),
                             ts: Date(timeIntervalSince1970: row.double(2)),
                             model: row.string(3), input: row.int(4), output: row.int(5),
                             cacheRead: row.int(6), cache5m: row.int(7), cache1h: row.int(8)))
        }
        return out
    }

    public var entryCount: Int {
        guard let db = connection() else { return 0 }
        var count = 0
        try? db.query("SELECT count(*) FROM entries") { count = $0.int(0) }
        return count
    }

    /// The oldest usage record held, which is what the dashboard means by "history since".
    public var earliestEntry: Date? {
        guard let db = connection() else { return nil }
        var out: Date?
        try? db.query("SELECT min(ts) FROM entries") { out = $0.date(0) }
        return out
    }

    // MARK: - Ingest marks

    public func ingestMark(path: String) -> IngestMark? {
        guard let db = connection() else { return nil }
        var mark: IngestMark?
        try? db.query("""
            SELECT path, provider, size, byte_offset, mtime FROM ingest_state WHERE path = ?
            """, [.text(path)]) { row in
            mark = IngestMark(path: row.string(0), provider: row.string(1),
                              size: row.int(2), byteOffset: row.int(3),
                              mtime: Date(timeIntervalSince1970: row.double(4)))
        }
        return mark
    }

    public func setIngestMark(_ mark: IngestMark, at: Date = Date()) {
        guard let db = connection() else { return }
        try? db.query("""
            INSERT INTO ingest_state (path, provider, size, byte_offset, mtime, seen_at)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(path) DO UPDATE SET
                size = excluded.size, byte_offset = excluded.byte_offset,
                mtime = excluded.mtime, seen_at = excluded.seen_at
            """, [.text(mark.path), .text(mark.provider), .int(mark.size),
                  .int(mark.byteOffset), .date(mark.mtime), .date(at)])
    }

    /// Drops marks for transcripts that are gone, so the table does not accumulate a row
    /// per file Claude Code has since pruned.
    public func forgetIngestMarks(notIn live: Set<String>, provider: String) {
        guard let db = connection() else { return }
        var stale: [String] = []
        try? db.query("SELECT path FROM ingest_state WHERE provider = ?",
                      [.text(provider)]) { row in
            let path = row.string(0)
            if !live.contains(path) { stale.append(path) }
        }
        guard !stale.isEmpty else { return }
        try? db.transaction {
            for path in stale {
                try db.query("DELETE FROM ingest_state WHERE path = ?", [.text(path)])
            }
        }
    }

    // MARK: - Daily rollups

    /// Recomputes the rollups for every day the given entries touch, from everything the
    /// entries table holds for those days, and stores the result.
    ///
    /// The merge rule is "the fullest reading of a day wins": a stored row is replaced only
    /// when the recomputed one carries at least as many tokens. Entries are deduped and
    /// never deleted before their retention, so a day normally only grows. The rule still
    /// matters at the seam: entries eventually age out while their daily row does not, and
    /// a rollup computed after that would otherwise write the day back down to nothing.
    @discardableResult
    public func merge(entries: [Entry], config: Config, now: Date = Date()) -> Int {
        guard let db = connection(), !entries.isEmpty else { return 0 }
        _ = db
        ingest(entries)
        // Every day these entries touch, not only the ones that changed: a caller handing
        // over a batch is asking for those days to be correct afterwards.
        pendingDays.formUnion(entries.map { Warehouse.day(for: $0.ts) })
        return rollupPending(config: config)
    }

    /// Every (provider, model) row for one UTC day, computed from the entries table.
    private func rollup(day: String, config: Config, database: Database) -> [DailyRecord] {
        var out: [DailyRecord] = []
        try? database.query("""
            SELECT provider, model,
                   sum(input), sum(output), sum(cache_read), sum(cache_5m), sum(cache_1h)
            FROM entries
            WHERE strftime('%Y-%m-%d', ts, 'unixepoch') = ?
            GROUP BY provider, model
            """, [.text(day)]) { row in
            let provider = row.string(0)
            let model = row.string(1)
            let input = row.int(2), output = row.int(3)
            let cacheRead = row.int(4), c5m = row.int(5), c1h = row.int(6)
            var rec = DailyRecord(day: day, provider: provider, model: model,
                                  input: input, output: output, cacheRead: cacheRead,
                                  cacheWrite: c5m + c1h, cost: 0, priced: true)
            if let p = config.price(for: model) {
                var total = Double(input) * p.input
                total += Double(output) * p.output
                total += Double(cacheRead) * p.cacheRead
                total += Double(c5m) * p.input * 1.25
                total += Double(c1h) * p.input * 2.0
                rec.cost = total / 1_000_000
            } else {
                rec.priced = false
                // An unpriced model has no cost to state, so the basis says so rather than
                // letting a zero read as free.
                rec.costBasis = .unknown
            }
            out.append(rec)
        }
        return out.sorted { $0.key < $1.key }
    }

    /// True when the row was written. A smaller reading of a day is dropped on the floor.
    @discardableResult
    private func upsertDaily(_ rec: DailyRecord, into database: Database) throws -> Bool {
        var wrote = false
        try database.query("""
            INSERT INTO daily (day, provider, model, input, output, cache_read, cache_write,
                               cost, priced, cost_basis)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(day, provider, model) DO UPDATE SET
                input = excluded.input, output = excluded.output,
                cache_read = excluded.cache_read, cache_write = excluded.cache_write,
                cost = excluded.cost, priced = excluded.priced,
                cost_basis = excluded.cost_basis
            WHERE excluded.input + excluded.output + excluded.cache_read
                  >= daily.input + daily.output + daily.cache_read
            RETURNING 1
            """, [.text(rec.day), .text(rec.provider), .text(rec.model), .int(rec.input),
                  .int(rec.output), .int(rec.cacheRead), .int(rec.cacheWrite),
                  .double(rec.cost), .int(rec.priced ? 1 : 0),
                  .text(rec.costBasis.rawValue)]) { _ in wrote = true }
        return wrote
    }

    /// Rolls entries up in memory, without touching the store. Kept because the shape is
    /// useful on its own and because it is the cheapest thing to test the pricing against.
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
                rec.costBasis = .unknown
            }
            acc[key] = rec
        }
        return acc.values.sorted { $0.key < $1.key }
    }

    public func load() -> [DailyRecord] { records() }

    public func records(since: Date? = nil, until: Date? = nil,
                        provider: String? = nil) -> [DailyRecord] {
        guard let db = connection() else { return [] }
        var sql = """
            SELECT day, provider, model, input, output, cache_read, cache_write,
                   cost, priced, cost_basis
            FROM daily WHERE 1 = 1
            """
        var bindings: [Database.Value] = []
        if let since { sql += " AND day >= ?"; bindings.append(.text(Warehouse.day(for: since))) }
        if let until { sql += " AND day <= ?"; bindings.append(.text(Warehouse.day(for: until))) }
        if let provider { sql += " AND provider = ? COLLATE NOCASE"; bindings.append(.text(provider)) }
        sql += " ORDER BY day, provider, model"
        var out: [DailyRecord] = []
        try? db.query(sql, bindings) { row in
            out.append(DailyRecord(day: row.string(0), provider: row.string(1),
                                   model: row.string(2), input: row.int(3),
                                   output: row.int(4), cacheRead: row.int(5),
                                   cacheWrite: row.int(6), cost: row.double(7),
                                   priced: row.bool(8),
                                   costBasis: Provenance(rawValueOrUnknown: row.string(9))))
        }
        return out
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

    // MARK: - Limit samples

    /// Records a reading, skipping the ones that say nothing new. A sample is kept when the
    /// percentage moved, the window rolled over, or `minInterval` has passed since the last
    /// one, so a five minute poll does not write a table full of identical rows.
    @discardableResult
    public func recordLimits(_ windows: [LimitWindow], at: Date = Date(),
                             minInterval: TimeInterval = 900) -> Int {
        guard let db = connection(), !windows.isEmpty else { return 0 }
        var appended = 0
        try? db.transaction {
            for w in windows where !w.isUninformative {
                let sample = LimitSample(at: at, provider: w.provider, key: w.key,
                                         utilization: w.utilization, resetsAt: w.resetsAt,
                                         source: w.source)
                let last = latestSample(provider: w.provider, key: w.key, database: db)
                if let last, last.sameWindowInstance(as: sample),
                   last.utilization == sample.utilization,
                   at.timeIntervalSince(last.at) < minInterval { continue }
                // A reading older than the newest stored one would break the ordering every
                // reader assumes, and a clock that jumped is not evidence of usage.
                if let last, sample.at <= last.at { continue }
                try insertSample(sample, into: db)
                appended += 1
            }
            if appended > 0 {
                let cutoff = at.addingTimeInterval(
                    -Double(Warehouse.sampleRetentionDays) * 86400)
                try db.query("DELETE FROM limit_samples WHERE at < ?", [.date(cutoff)])
            }
        }
        return appended
    }

    private func insertSample(_ s: LimitSample, into database: Database) throws {
        try database.query("""
            INSERT INTO limit_samples (at, provider, key, utilization, resets_at, source)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(provider, key, at) DO NOTHING
            """, [.date(s.at), .text(s.provider), .text(s.key), .double(s.utilization),
                  .date(s.resetsAt), .text(s.source.rawValue)])
    }

    private func latestSample(provider: String, key: String,
                              database: Database) -> LimitSample? {
        var out: LimitSample?
        try? database.query("""
            SELECT at, provider, key, utilization, resets_at, source FROM limit_samples
            WHERE provider = ? AND key = ? ORDER BY at DESC LIMIT 1
            """, [.text(provider), .text(key)]) { row in out = Warehouse.sample(from: row) }
        return out
    }

    public func limitSamples(provider: String? = nil, key: String? = nil,
                             since: Date? = nil) -> [LimitSample] {
        guard let db = connection() else { return [] }
        var sql = """
            SELECT at, provider, key, utilization, resets_at, source
            FROM limit_samples WHERE 1 = 1
            """
        var bindings: [Database.Value] = []
        if let provider { sql += " AND provider = ? COLLATE NOCASE"; bindings.append(.text(provider)) }
        if let key { sql += " AND key = ?"; bindings.append(.text(key)) }
        if let since { sql += " AND at >= ?"; bindings.append(.date(since)) }
        sql += " ORDER BY at"
        var out: [LimitSample] = []
        try? db.query(sql, bindings) { out.append(Warehouse.sample(from: $0)) }
        return out
    }

    /// The last reading of each of a provider's windows, rebuilt as windows.
    ///
    /// Codex publishes its percentages inside session transcripts, so a poll that reads no
    /// new lines used to have no percentages at all, and a pruned session file took the
    /// numbers with it. The samples table already holds every reading, so the last one is
    /// the answer. Callers still have to decide whether it is fresh enough to show: this
    /// hands back when it was read and says nothing about whether that is recent.
    public func latestLimits(provider: String) -> (windows: [LimitWindow], at: Date)? {
        guard let db = connection() else { return nil }
        var windows: [LimitWindow] = []
        var newest: Date?
        try? db.query("""
            SELECT s.at, s.provider, s.key, s.utilization, s.resets_at, s.source
            FROM limit_samples s
            JOIN (SELECT key, max(at) AS at FROM limit_samples
                  WHERE provider = ? COLLATE NOCASE GROUP BY key) latest
              ON latest.key = s.key AND latest.at = s.at
            WHERE s.provider = ? COLLATE NOCASE
            """, [.text(provider), .text(provider)]) { row in
            let sample = Warehouse.sample(from: row)
            windows.append(LimitWindow(provider: sample.provider, key: sample.key,
                                       utilization: sample.utilization,
                                       resetsAt: sample.resetsAt, source: sample.source))
            if sample.at > (newest ?? .distantPast) { newest = sample.at }
        }
        guard let newest, !windows.isEmpty else { return nil }
        return (windows, newest)
    }

    private static func sample(from row: Database.Row) -> LimitSample {
        LimitSample(at: Date(timeIntervalSince1970: row.double(0)),
                    provider: row.string(1), key: row.string(2),
                    utilization: row.double(3), resetsAt: row.date(4),
                    source: Provenance(rawValueOrUnknown: row.string(5)))
    }

    // MARK: - Retention and files

    /// Ages out entries past their retention. The daily rows they were rolled into stay,
    /// which is the whole reason both tables exist.
    @discardableResult
    public func pruneEntries(now: Date = Date()) -> Int {
        guard let db = connection() else { return 0 }
        let cutoff = now.addingTimeInterval(-Double(Warehouse.entryRetentionDays) * 86400)
        var removed = 0
        try? db.query("SELECT count(*) FROM entries WHERE ts < ?", [.date(cutoff)]) {
            removed = $0.int(0)
        }
        guard removed > 0 else { return 0 }
        try? db.query("DELETE FROM entries WHERE ts < ?", [.date(cutoff)])
        return removed
    }

    /// Bytes on disk, for the settings row that says what this is costing you. WAL and
    /// shared memory files count: they are real bytes in the same directory.
    public var sizeBytes: Int64 {
        let names = ["redline.db", "redline.db-wal", "redline.db-shm"]
        return names.reduce(Int64(0)) { total, name in
            let path = root.appendingPathComponent(name).path
            let size = (try? fm.attributesOfItem(atPath: path)[.size]) as? Int64 ?? 0
            return total + size
        }
    }

    public func removeAll() {
        guard let db = connection() else { return }
        try? db.transaction {
            try db.execute("""
                DELETE FROM entries;
                DELETE FROM daily;
                DELETE FROM limit_samples;
                DELETE FROM ingest_state;
                """)
        }
        db.compact()
    }
}

extension Entry {
    /// What makes this record unique in the store. The provider's own message id where
    /// there is one; otherwise the transcript position it was parsed from, which is stable
    /// for an append only file and is why `origin` is carried on the entry at all.
    var dedupKey: String {
        if let key, !key.isEmpty { return "\(provider):\(key)" }
        if let origin { return "\(provider):\(origin)" }
        // Nothing identifies this record but its own contents, so use them. Two identical
        // records at the same instant are indistinguishable and collapsing them is right.
        return "\(provider):\(ts.timeIntervalSince1970):\(model):\(input):\(output):\(cacheRead)"
    }
}
