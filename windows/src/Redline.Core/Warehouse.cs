// Local history that outlives the transcripts it came from: one SQLite database holding
// entries, daily rollups, limit samples and ingest marks. The daily rows are the long memory.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// One provider's usage of one model on one UTC day. Days are UTC because a record that
/// silently means a different span depending on where it was written cannot be summed.
public sealed record DailyRecord
{
    public string Day { get; set; }            // yyyy-MM-dd, UTC
    public string Provider { get; set; }
    public string Model { get; set; }
    public int Input { get; set; }
    public int Output { get; set; }
    public int CacheRead { get; set; }
    public int CacheWrite { get; set; }
    public double Cost { get; set; }
    public bool Priced { get; set; }
    /// Token counts come from the provider; the dollar figure is ours, so this describes the cost.
    public Provenance CostBasis { get; set; }

    public int Io => Input + Output;

    public DailyRecord(string day, string provider, string model, int input, int output,
                       int cacheRead, int cacheWrite, double cost, bool priced,
                       Provenance costBasis = Provenance.LocalEstimate)
    {
        Day = day;
        Provider = provider;
        Model = model;
        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
        Cost = cost;
        Priced = priced;
        CostBasis = costBasis;
    }

    public string Key => $"{Day}|{Provider}|{Model}";
}

/// One reading of one limit window, kept so pace and burn rate have something to measure.
public sealed record LimitSample(DateTimeOffset At, string Provider, string Key, double Utilization,
                                 DateTimeOffset? ResetsAt, Provenance Source)
{
    /// Same window instance only when they share a reset time. A new reset means the window
    /// rolled over and the two readings must never be differenced.
    public bool SameWindowInstance(LimitSample other)
    {
        if (Provider != other.Provider || Key != other.Key) return false;
        return (ResetsAt, other.ResetsAt) switch
        {
            (null, null) => true,
            ({ } a, { } b) => Math.Abs((a - b).TotalSeconds) < 60,
            _ => false,
        };
    }
}

/// How far into one transcript the ingest has read. Transcripts are append only, so the
/// next scan starts at the offset rather than re-reading megabytes to find the last line.
public sealed record IngestMark(string Path, string Provider, long Size, long ByteOffset,
                                DateTimeOffset Mtime);

public sealed class Warehouse : IDisposable
{
    public const string DayFormat = "yyyy-MM-dd";

    /// How long limit samples are kept. Long enough to see a month of weekly windows.
    public const int SampleRetentionDays = 60;
    /// How long individual entries are kept. The daily rollups are forever.
    public const int EntryRetentionDays = 365;

    private readonly string _root;
    private Database? _db;
    private bool _opened;
    // Days whose rollup is stale because entries landed in them since the last pass
    private readonly HashSet<string> _pendingDays = new(StringComparer.Ordinal);

    public Warehouse(string? root = null)
    {
        _root = root ?? RedlineHome.PathFor(".local/share/redline/history");
    }

    public string DatabaseUrl => Path.Combine(_root, "redline.db");
    /// The pre-0.7 files. Read once at migration, then renamed rather than deleted.
    public string LegacyDailyUrl => Path.Combine(_root, "daily.jsonl");
    public string LegacyLimitsUrl => Path.Combine(_root, "limits.jsonl");

    public static string Day(DateTimeOffset date) =>
        date.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }

    // Connection

    /// Opened lazily so constructing a Warehouse never touches the disk, and failures are
    /// survivable: a tray app with a broken history file still shows live percentages.
    private Database? Connection()
    {
        if (_opened) return _db;
        _opened = true;
        Database? database = null;
        try
        {
            database = new Database(DatabaseUrl);
            Migrate(database);
            _db = database;
        }
        catch
        {
            database?.Dispose();
            _db = null;
        }
        return _db;
    }

    private void Migrate(Database database)
    {
        if (database.UserVersion < 1)
        {
            database.Execute("""
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
                """);
            database.SetUserVersion(1);
            ImportLegacyFiles(database);
        }
    }

    /// Folds the pre-0.7 JSONL into the tables, once. A failure here must not stop the app:
    /// the worst case is history that starts today, and the files stay on disk either way.
    private void ImportLegacyFiles(Database database)
    {
        if (ReadLines(LegacyDailyUrl) is { } dailyLines)
        {
            var records = dailyLines.Select(DecodeDaily).OfType<DailyRecord>().ToList();
            if (records.Count > 0)
            {
                try { database.Transaction(() => { foreach (var rec in records) UpsertDaily(rec, database); }); }
                catch { }
                try { File.Move(LegacyDailyUrl, LegacyDailyUrl + ".migrated"); } catch { }
            }
        }
        if (ReadLines(LegacyLimitsUrl) is { } limitLines)
        {
            var samples = limitLines.Select(DecodeSample).OfType<LimitSample>().ToList();
            if (samples.Count > 0)
            {
                try { database.Transaction(() => { foreach (var s in samples) InsertSample(s, database); }); }
                catch { }
                try { File.Move(LegacyLimitsUrl, LegacyLimitsUrl + ".migrated"); } catch { }
            }
        }
    }

    private static string[]? ReadLines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : null; }
        catch { return null; }
    }

    // Mirrors Swift's synthesized Codable: every key present, costBasis a known raw value
    internal static DailyRecord? DecodeDaily(string line)
    {
        if (Json.ParseObject(line) is not { } o) return null;
        if (Json.Str(o["day"]) is not { } day || Json.Str(o["provider"]) is not { } provider ||
            Json.Str(o["model"]) is not { } model || Json.Int(o["input"]) is not { } input ||
            Json.Int(o["output"]) is not { } output || Json.Int(o["cacheRead"]) is not { } cacheRead ||
            Json.Int(o["cacheWrite"]) is not { } cacheWrite || Json.Num(o["cost"]) is not { } cost ||
            Json.Bool(o["priced"]) is not { } priced || StrictProvenance(o["costBasis"]) is not { } basis)
            return null;
        return new DailyRecord(day, provider, model, input, output, cacheRead, cacheWrite,
                               cost, priced, basis);
    }

    internal static LimitSample? DecodeSample(string line)
    {
        if (Json.ParseObject(line) is not { } o) return null;
        if (ParseIso8601(Json.Str(o["at"])) is not { } at || Json.Str(o["provider"]) is not { } provider ||
            Json.Str(o["key"]) is not { } key || Json.Num(o["utilization"]) is not { } util ||
            StrictProvenance(o["source"]) is not { } source)
            return null;
        DateTimeOffset? resets = null;
        if (o["resetsAt"] is { } r)
        {
            resets = ParseIso8601(Json.Str(r));
            if (resets is null) return null;
        }
        return new LimitSample(at, provider, key, util, resets, source);
    }

    private static Provenance? StrictProvenance(JsonNode? n) => Json.Str(n) switch
    {
        "official" => Provenance.Official,
        "local_estimate" => Provenance.LocalEstimate,
        "experimental" => Provenance.Experimental,
        "unknown" => Provenance.Unknown,
        _ => null,
    };

    // JSONEncoder's .iso8601 strategy: internet date time, no fractional seconds
    private static DateTimeOffset? ParseIso8601(string? s)
    {
        if (s is null) return null;
        string[] formats = { "yyyy-MM-dd'T'HH:mm:ssZ", "yyyy-MM-dd'T'HH:mm:sszzz" };
        return DateTimeOffset.TryParseExact(s, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    // Entries

    /// Stores usage records, ignoring ones already held. Returns how many were new. Dedup is
    /// the provider's message id where there is one, else transcript path plus byte offset.
    public int Ingest(IReadOnlyCollection<Entry> entries)
    {
        if (Connection() is not { } db || entries.Count == 0) return 0;
        int added = 0;
        try
        {
            db.Transaction(() =>
            {
                foreach (var e in entries)
                {
                    bool changed = false;
                    db.Query("""
                        INSERT INTO entries
                            (dedup, provider, ts, model, input, output, cache_read, cache_5m, cache_1h)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                        ON CONFLICT(dedup) DO NOTHING
                        RETURNING 1
                        """, new Database.Value[] {
                            new Database.Value.Text(e.DedupKey()), new Database.Value.Text(e.Provider),
                            Database.Value.Date(e.Ts), new Database.Value.Text(e.Model),
                            new Database.Value.Int(e.Input), new Database.Value.Int(e.Output),
                            new Database.Value.Int(e.CacheRead), new Database.Value.Int(e.Cache5m),
                            new Database.Value.Int(e.Cache1h) }, _ => changed = true);
                    if (changed)
                    {
                        added += 1;
                        _pendingDays.Add(Day(e.Ts));
                    }
                }
            });
        }
        catch { }
        return added;
    }

    /// Recomputes the rollups for every day that has learned something since the last call.
    /// Returns how many stored rows changed, which is zero on a quiet poll.
    public int RollupPending(Config config)
    {
        if (Connection() is not { } db || _pendingDays.Count == 0) return 0;
        var days = _pendingDays.OrderBy(d => d, StringComparer.Ordinal).ToList();
        _pendingDays.Clear();
        int changed = 0;
        try
        {
            db.Transaction(() =>
            {
                foreach (var day in days)
                    foreach (var rec in Rollup(day, config, db))
                        if (UpsertDaily(rec, db)) changed += 1;
            });
        }
        catch { }
        return changed;
    }

    public List<Entry> Entries(DateTimeOffset? since = null, DateTimeOffset? until = null,
                               string? provider = null)
    {
        if (Connection() is not { } db) return new();
        var sql = """
            SELECT dedup, provider, ts, model, input, output, cache_read, cache_5m, cache_1h
            FROM entries WHERE 1 = 1
            """;
        var bindings = new List<Database.Value>();
        if (since is { } s) { sql += " AND ts >= ?"; bindings.Add(Database.Value.Date(s)); }
        if (until is { } u) { sql += " AND ts <= ?"; bindings.Add(Database.Value.Date(u)); }
        if (provider is not null) { sql += " AND provider = ?"; bindings.Add(new Database.Value.Text(provider)); }
        sql += " ORDER BY ts";
        var output = new List<Entry>();
        try
        {
            db.Query(sql, bindings, row => output.Add(new Entry(
                row.String(1), row.String(0), Database.FromSeconds(row.Double(2)), row.String(3),
                row.Int(4), row.Int(5), row.Int(6), row.Int(7), row.Int(8))));
        }
        catch { }
        return output;
    }

    public int EntryCount
    {
        get
        {
            if (Connection() is not { } db) return 0;
            int count = 0;
            try { db.Query("SELECT count(*) FROM entries", null, r => count = r.Int(0)); } catch { }
            return count;
        }
    }

    /// The oldest usage record held, which is what the dashboard means by "history since".
    public DateTimeOffset? EarliestEntry
    {
        get
        {
            if (Connection() is not { } db) return null;
            DateTimeOffset? output = null;
            try { db.Query("SELECT min(ts) FROM entries", null, r => output = r.Date(0)); } catch { }
            return output;
        }
    }

    // Ingest marks

    public IngestMark? IngestMark(string path)
    {
        if (Connection() is not { } db) return null;
        IngestMark? mark = null;
        try
        {
            db.Query("SELECT path, provider, size, byte_offset, mtime FROM ingest_state WHERE path = ?",
                new Database.Value[] { new Database.Value.Text(path) },
                row => mark = new IngestMark(row.String(0), row.String(1), row.Long(2), row.Long(3),
                                             Database.FromSeconds(row.Double(4))));
        }
        catch { }
        return mark;
    }

    public void SetIngestMark(IngestMark mark, DateTimeOffset? at = null)
    {
        if (Connection() is not { } db) return;
        try
        {
            db.Query("""
                INSERT INTO ingest_state (path, provider, size, byte_offset, mtime, seen_at)
                VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(path) DO UPDATE SET
                    size = excluded.size, byte_offset = excluded.byte_offset,
                    mtime = excluded.mtime, seen_at = excluded.seen_at
                """, new Database.Value[] {
                    new Database.Value.Text(mark.Path), new Database.Value.Text(mark.Provider),
                    new Database.Value.Int(mark.Size), new Database.Value.Int(mark.ByteOffset),
                    Database.Value.Date(mark.Mtime), Database.Value.Date(at ?? DateTimeOffset.UtcNow) });
        }
        catch { }
    }

    /// Drops marks for transcripts that are gone, so the table does not accumulate a row
    /// per file Claude Code has since pruned.
    public void ForgetIngestMarks(IReadOnlySet<string> notIn, string provider)
    {
        if (Connection() is not { } db) return;
        var stale = new List<string>();
        try
        {
            db.Query("SELECT path FROM ingest_state WHERE provider = ?",
                new Database.Value[] { new Database.Value.Text(provider) }, row =>
                {
                    var path = row.String(0);
                    if (!notIn.Contains(path)) stale.Add(path);
                });
        }
        catch { }
        if (stale.Count == 0) return;
        try
        {
            db.Transaction(() =>
            {
                foreach (var path in stale)
                    db.Query("DELETE FROM ingest_state WHERE path = ?",
                             new Database.Value[] { new Database.Value.Text(path) });
            });
        }
        catch { }
    }

    // Daily rollups

    /// Recomputes the rollups for every day the given entries touch and stores the result.
    /// "The fullest reading of a day wins": a stored row is replaced only by one at least as large.
    public int Merge(IReadOnlyCollection<Entry> entries, Config config, DateTimeOffset? now = null)
    {
        if (Connection() is null || entries.Count == 0) return 0;
        Ingest(entries);
        // Every day these entries touch, not only the ones that changed
        foreach (var e in entries) _pendingDays.Add(Day(e.Ts));
        return RollupPending(config);
    }

    /// Every (provider, model) row for one UTC day, computed from the entries table.
    private static List<DailyRecord> Rollup(string day, Config config, Database database)
    {
        var output = new List<DailyRecord>();
        try
        {
            database.Query("""
                SELECT provider, model,
                       sum(input), sum(output), sum(cache_read), sum(cache_5m), sum(cache_1h)
                FROM entries
                WHERE strftime('%Y-%m-%d', ts, 'unixepoch') = ?
                GROUP BY provider, model
                """, new Database.Value[] { new Database.Value.Text(day) }, row =>
                {
                    var provider = row.String(0);
                    var model = row.String(1);
                    int input = row.Int(2), outp = row.Int(3);
                    int cacheRead = row.Int(4), c5m = row.Int(5), c1h = row.Int(6);
                    var rec = new DailyRecord(day, provider, model, input, outp, cacheRead,
                                              c5m + c1h, 0, true);
                    if (config.Price(model) is { } p)
                    {
                        double total = (double)input * p.Input;
                        total += (double)outp * p.Output;
                        total += (double)cacheRead * p.CacheRead;
                        total += (double)c5m * p.Input * 1.25;
                        total += (double)c1h * p.Input * 2.0;
                        rec.Cost = total / 1_000_000;
                    }
                    else
                    {
                        rec.Priced = false;
                        // An unpriced model has no cost to state, so a zero must not read as free
                        rec.CostBasis = Provenance.Unknown;
                    }
                    output.Add(rec);
                });
        }
        catch { }
        return output.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// True when the row was written. A smaller reading of a day is dropped on the floor.
    private static bool UpsertDaily(DailyRecord rec, Database database)
    {
        bool wrote = false;
        database.Query("""
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
            """, new Database.Value[] {
                new Database.Value.Text(rec.Day), new Database.Value.Text(rec.Provider),
                new Database.Value.Text(rec.Model), new Database.Value.Int(rec.Input),
                new Database.Value.Int(rec.Output), new Database.Value.Int(rec.CacheRead),
                new Database.Value.Int(rec.CacheWrite), new Database.Value.Double(rec.Cost),
                new Database.Value.Int(rec.Priced ? 1 : 0),
                new Database.Value.Text(rec.CostBasis.RawValue()) }, _ => wrote = true);
        return wrote;
    }

    /// Rolls entries up in memory, without touching the store. The cheapest thing to test
    /// the pricing against.
    public static List<DailyRecord> Rollup(IEnumerable<Entry> entries, Config config)
    {
        var acc = new Dictionary<string, DailyRecord>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var day = Day(e.Ts);
            var key = $"{day}|{e.Provider}|{e.Model}";
            var price = config.Price(e.Model);
            if (!acc.TryGetValue(key, out var rec))
                acc[key] = rec = new DailyRecord(day, e.Provider, e.Model, 0, 0, 0, 0, 0, price is not null);
            rec.Input += e.Input;
            rec.Output += e.Output;
            rec.CacheRead += e.CacheRead;
            rec.CacheWrite += e.Cache5m + e.Cache1h;
            if (price is { } p)
            {
                double total = (double)e.Input * p.Input;
                total += (double)e.Output * p.Output;
                total += (double)e.CacheRead * p.CacheRead;
                total += (double)e.Cache5m * p.Input * 1.25;
                total += (double)e.Cache1h * p.Input * 2.0;
                rec.Cost += total / 1_000_000;
            }
            else
            {
                rec.Priced = false;
                rec.CostBasis = Provenance.Unknown;
            }
        }
        return acc.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    public List<DailyRecord> Load() => Records();

    public List<DailyRecord> Records(DateTimeOffset? since = null, DateTimeOffset? until = null,
                                     string? provider = null)
    {
        if (Connection() is not { } db) return new();
        var sql = """
            SELECT day, provider, model, input, output, cache_read, cache_write,
                   cost, priced, cost_basis
            FROM daily WHERE 1 = 1
            """;
        var bindings = new List<Database.Value>();
        if (since is { } s) { sql += " AND day >= ?"; bindings.Add(new Database.Value.Text(Day(s))); }
        if (until is { } u) { sql += " AND day <= ?"; bindings.Add(new Database.Value.Text(Day(u))); }
        if (provider is not null) { sql += " AND provider = ? COLLATE NOCASE"; bindings.Add(new Database.Value.Text(provider)); }
        sql += " ORDER BY day, provider, model";
        var output = new List<DailyRecord>();
        try
        {
            db.Query(sql, bindings, row => output.Add(new DailyRecord(
                row.String(0), row.String(1), row.String(2), row.Int(3), row.Int(4), row.Int(5),
                row.Int(6), row.Double(7), row.Bool(8), ProvenanceExt.FromRaw(row.String(9)))));
        }
        catch { }
        return output;
    }

    /// One row per day, provider folded together. What a history chart or an export wants.
    public static List<(string Day, int Io, double Cost, bool Priced)> ByDay(IEnumerable<DailyRecord> records)
    {
        var acc = new Dictionary<string, (int Io, double Cost, bool Priced)>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            (int Io, double Cost, bool Priced) slot = acc.TryGetValue(r.Day, out var x) ? x : (0, 0.0, true);
            slot.Io += r.Io;
            slot.Cost += r.Cost;
            slot.Priced = slot.Priced && r.Priced;
            acc[r.Day] = slot;
        }
        return acc.Select(kv => (kv.Key, kv.Value.Io, kv.Value.Cost, kv.Value.Priced))
                  .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    // Limit samples

    /// Records a reading, skipping ones that say nothing new: kept when the percentage moved,
    /// the window rolled over, or `minInterval` seconds passed since the last one.
    public int RecordLimits(IReadOnlyCollection<LimitWindow> windows, DateTimeOffset? at = null,
                            double minInterval = 900)
    {
        if (Connection() is not { } db || windows.Count == 0) return 0;
        var t = at ?? DateTimeOffset.UtcNow;
        int appended = 0;
        try
        {
            db.Transaction(() =>
            {
                foreach (var w in windows)
                {
                    if (w.IsUninformative) continue;
                    var sample = new LimitSample(t, w.Provider, w.Key, w.Utilization, w.ResetsAt, w.Source);
                    var last = LatestSample(w.Provider, w.Key, db);
                    if (last is not null && last.SameWindowInstance(sample) &&
                        last.Utilization == sample.Utilization &&
                        (t - last.At).TotalSeconds < minInterval) continue;
                    // A reading older than the newest stored one would break every reader's ordering
                    if (last is not null && sample.At <= last.At) continue;
                    InsertSample(sample, db);
                    appended += 1;
                }
                if (appended > 0)
                {
                    var cutoff = t.AddSeconds(-(double)SampleRetentionDays * 86400);
                    db.Query("DELETE FROM limit_samples WHERE at < ?",
                             new[] { Database.Value.Date(cutoff) });
                }
            });
        }
        catch { }
        return appended;
    }

    private static void InsertSample(LimitSample s, Database database)
    {
        database.Query("""
            INSERT INTO limit_samples (at, provider, key, utilization, resets_at, source)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(provider, key, at) DO NOTHING
            """, new Database.Value[] {
                Database.Value.Date(s.At), new Database.Value.Text(s.Provider),
                new Database.Value.Text(s.Key), new Database.Value.Double(s.Utilization),
                Database.Value.Date(s.ResetsAt), new Database.Value.Text(s.Source.RawValue()) });
    }

    private static LimitSample? LatestSample(string provider, string key, Database database)
    {
        LimitSample? output = null;
        try
        {
            database.Query("""
                SELECT at, provider, key, utilization, resets_at, source FROM limit_samples
                WHERE provider = ? AND key = ? ORDER BY at DESC LIMIT 1
                """, new Database.Value[] { new Database.Value.Text(provider), new Database.Value.Text(key) },
                row => output = Sample(row));
        }
        catch { }
        return output;
    }

    public List<LimitSample> LimitSamples(string? provider = null, string? key = null,
                                          DateTimeOffset? since = null)
    {
        if (Connection() is not { } db) return new();
        var sql = """
            SELECT at, provider, key, utilization, resets_at, source
            FROM limit_samples WHERE 1 = 1
            """;
        var bindings = new List<Database.Value>();
        if (provider is not null) { sql += " AND provider = ? COLLATE NOCASE"; bindings.Add(new Database.Value.Text(provider)); }
        if (key is not null) { sql += " AND key = ?"; bindings.Add(new Database.Value.Text(key)); }
        if (since is { } s) { sql += " AND at >= ?"; bindings.Add(Database.Value.Date(s)); }
        sql += " ORDER BY at";
        var output = new List<LimitSample>();
        try { db.Query(sql, bindings, row => output.Add(Sample(row))); } catch { }
        return output;
    }

    /// The last reading of each of a provider's windows, rebuilt as windows. Says when it
    /// was read and nothing about whether that is recent; callers decide freshness.
    public (List<LimitWindow> Windows, DateTimeOffset At)? LatestLimits(string provider)
    {
        if (Connection() is not { } db) return null;
        var windows = new List<LimitWindow>();
        DateTimeOffset? newest = null;
        try
        {
            db.Query("""
                SELECT s.at, s.provider, s.key, s.utilization, s.resets_at, s.source
                FROM limit_samples s
                JOIN (SELECT key, max(at) AS at FROM limit_samples
                      WHERE provider = ? COLLATE NOCASE GROUP BY key) latest
                  ON latest.key = s.key AND latest.at = s.at
                WHERE s.provider = ? COLLATE NOCASE
                """, new Database.Value[] { new Database.Value.Text(provider), new Database.Value.Text(provider) },
                row =>
                {
                    var sample = Sample(row);
                    windows.Add(new LimitWindow(sample.Provider, sample.Key, sample.Utilization,
                                                sample.ResetsAt, sample.Source));
                    if (newest is null || sample.At > newest) newest = sample.At;
                });
        }
        catch { }
        if (newest is not { } n || windows.Count == 0) return null;
        return (windows, n);
    }

    private static LimitSample Sample(Database.Row row) =>
        new(Database.FromSeconds(row.Double(0)), row.String(1), row.String(2), row.Double(3),
            row.Date(4), ProvenanceExt.FromRaw(row.String(5)));

    // Retention and files

    /// Ages out entries past their retention. The daily rows they were rolled into stay,
    /// which is the whole reason both tables exist.
    public int PruneEntries(DateTimeOffset? now = null)
    {
        if (Connection() is not { } db) return 0;
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddSeconds(-(double)EntryRetentionDays * 86400);
        int removed = 0;
        try
        {
            db.Query("SELECT count(*) FROM entries WHERE ts < ?",
                     new[] { Database.Value.Date(cutoff) }, r => removed = r.Int(0));
        }
        catch { }
        if (removed <= 0) return 0;
        try { db.Query("DELETE FROM entries WHERE ts < ?", new[] { Database.Value.Date(cutoff) }); }
        catch { }
        return removed;
    }

    /// Bytes on disk, for the settings row that says what this is costing you. WAL and
    /// shared memory files count: they are real bytes in the same directory.
    public long SizeBytes =>
        new[] { "redline.db", "redline.db-wal", "redline.db-shm" }.Sum(name =>
        {
            try
            {
                var fi = new FileInfo(Path.Combine(_root, name));
                return fi.Exists ? fi.Length : 0L;
            }
            catch { return 0L; }
        });

    public void RemoveAll()
    {
        if (Connection() is not { } db) return;
        try
        {
            db.Transaction(() => db.Execute("""
                DELETE FROM entries;
                DELETE FROM daily;
                DELETE FROM limit_samples;
                DELETE FROM ingest_state;
                """));
        }
        catch { }
        db.Compact();
    }
}

public static class EntryDedup
{
    /// What makes this record unique in the store: the provider's message id where there is
    /// one, otherwise the transcript position it was parsed from.
    public static string DedupKey(this Entry e)
    {
        if (!string.IsNullOrEmpty(e.Key)) return $"{e.Provider}:{e.Key}";
        if (e.Origin is not null) return $"{e.Provider}:{e.Origin}";
        // Nothing identifies this record but its contents; identical twins collapsing is right
        return string.Create(CultureInfo.InvariantCulture,
            $"{e.Provider}:{SwiftDouble(Database.Seconds(e.Ts))}:{e.Model}:{e.Input}:{e.Output}:{e.CacheRead}");
    }

    // Swift prints an integral Double with a trailing ".0"
    private static string SwiftDouble(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }
}
