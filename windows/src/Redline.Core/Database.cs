// A thin wrapper over SQLite: a statement runner and a migration ledger, not an ORM. Every
// query is written as SQL where it is used, so the shape of the question stays visible.
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Redline.Core;

public sealed class DatabaseError : Exception
{
    public int Code { get; }
    public DatabaseError(int code, string message) : base(message) { Code = code; }
    public override string ToString() => $"sqlite error {Code}: {Message}";
}

public sealed class Database : IDisposable
{
    private SqliteConnection? _handle;
    private readonly object _lock = new();

    /// Opens, creating the file and its directory. WAL because the CLI is a second process
    /// that must be able to read during a write. Unpooled so disposing releases the file.
    public Database(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { }
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 3,
        }.ToString();
        try
        {
            _handle = new SqliteConnection(cs);
            _handle.Open();
        }
        catch (SqliteException e)
        {
            _handle?.Dispose();
            _handle = null;
            throw new DatabaseError(e.SqliteErrorCode, e.Message);
        }
        // chmod 0600 has no equivalent here: the file lives under the private user profile
        Execute("PRAGMA busy_timeout = 3000");
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA synchronous = NORMAL");
        Execute("PRAGMA foreign_keys = ON");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private SqliteConnection Handle =>
        _handle ?? throw new DatabaseError(21, "database is closed");

    public void Execute(string sql)
    {
        lock (_lock)
        {
            try
            {
                using var cmd = Handle.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException e)
            {
                var failure = new DatabaseError(e.SqliteErrorCode, e.Message);
                Record("db.execute_failed", failure, sql);
                throw failure;
            }
        }
    }

    /// Runs one statement, binding positionally, and hands each result row to `row`.
    public void Query(string sql, IReadOnlyList<Value>? bindings = null, Action<Row>? row = null)
    {
        lock (_lock)
        {
            using var cmd = Handle.CreateCommand();
            cmd.CommandText = Positional(sql, out var count);
            var values = bindings ?? Array.Empty<Value>();
            for (int i = 0; i < count; i++)
                cmd.Parameters.AddWithValue("$p" + (i + 1), i < values.Count ? values[i].Raw : DBNull.Value);
            SqliteDataReader reader;
            try { reader = cmd.ExecuteReader(); }
            catch (SqliteException e) { throw Fail("db.prepare_failed", sql, e); }
            using (reader)
            {
                while (true)
                {
                    bool more;
                    try { more = reader.Read(); }
                    catch (SqliteException e) { throw Fail("db.step_failed", sql, e); }
                    if (!more) return;
                    row?.Invoke(new Row(reader));
                }
            }
        }
    }

    /// Batches writes into one transaction. A poll can ingest thousands of rows, and a
    /// commit per row turns a fast write into a disk-bound one.
    public void Transaction(Action body)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            body();
            Execute("COMMIT");
        }
        catch
        {
            // A failed rollback leaves the connection inside a transaction, so record it
            try { Execute("ROLLBACK"); }
            catch (Exception e)
            {
                Diag.Log.Error("db.rollback_failed", "could not roll back",
                               new() { ["error"] = e.ToString() });
            }
            throw;
        }
    }

    public int UserVersion
    {
        get
        {
            int version = 0;
            try { Query("PRAGMA user_version", null, r => version = r.Int(0)); } catch { }
            return version;
        }
    }

    public void SetUserVersion(int version) =>
        Execute($"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)}");

    /// Reclaims space after a retention pass. Cheap to call and a no-op when nothing freed.
    public void Compact()
    {
        try { Execute("PRAGMA incremental_vacuum; VACUUM"); } catch { }
    }

    // Rewrites each bare `?` to a numbered name; no SQL in this app puts `?` inside a literal
    private static string Positional(string sql, out int count)
    {
        var sb = new StringBuilder(sql.Length + 16);
        count = 0;
        foreach (var ch in sql)
        {
            if (ch == '?') sb.Append("$p").Append(++count);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private DatabaseError Fail(string code, string sql, SqliteException e)
    {
        var failure = new DatabaseError(e.SqliteErrorCode, $"{e.Message} [{sql}]");
        Record(code, failure, sql);
        return failure;
    }

    private static void Record(string code, DatabaseError failure, string sql)
    {
        // SQL text is schema, never row data, so it is safe to keep. Truncated for length.
        var statement = string.Join(" ", sql.Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Diag.Log.Error(code, failure.Message, new()
        {
            ["sql"] = statement.Length > 120 ? statement[..120] : statement,
            ["sqlite"] = failure.Code.ToString(CultureInfo.InvariantCulture),
        });
    }

    // Values and rows

    public abstract record Value
    {
        internal abstract object Raw { get; }

        public sealed record Int(long V) : Value { internal override object Raw => V; }
        public sealed record Double(double V) : Value { internal override object Raw => V; }
        public sealed record Text(string V) : Value { internal override object Raw => V; }
        public sealed record Null : Value { internal override object Raw => DBNull.Value; }

        /// Dates are stored as unix seconds throughout: a REAL sorts, ranges and buckets in
        /// SQL, which an ISO string only does by accident of its format.
        public static Value Date(DateTimeOffset? date) =>
            date is { } d ? new Double(Seconds(d)) : new Null();
    }

    public static double Seconds(DateTimeOffset d) =>
        (d.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    public static DateTimeOffset FromSeconds(double s) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond));

    public readonly struct Row
    {
        private readonly SqliteDataReader _r;
        internal Row(SqliteDataReader r) { _r = r; }

        public int Int(int column) => _r.IsDBNull(column) ? 0 : (int)_r.GetInt64(column);
        public long Long(int column) => _r.IsDBNull(column) ? 0 : _r.GetInt64(column);
        public double Double(int column) => _r.IsDBNull(column) ? 0 : _r.GetDouble(column);
        public bool Bool(int column) => Long(column) != 0;
        public string String(int column) => _r.IsDBNull(column) ? "" : _r.GetString(column);
        public bool IsNull(int column) => _r.IsDBNull(column);
        public DateTimeOffset? Date(int column) => IsNull(column) ? null : FromSeconds(Double(column));
    }
}
