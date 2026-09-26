// Structured diagnostics as newline-delimited JSON, so a failure that used to vanish into a
// catch leaves a record a human or a tool can read back. Append only, rotated by size.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redline.Core;

[JsonConverter(typeof(JsonStringEnumConverter<DiagLevel>))]
public enum DiagLevel { debug = 0, info = 1, warn = 2, error = 3 }

/// One thing that happened. `Code` is a stable dotted slug; `Message` is for a human.
public sealed record DiagEvent(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("level")] DiagLevel Level,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("msg")] string Message,
    [property: JsonPropertyName("ctx")] Dictionary<string, string> Context,
    [property: JsonPropertyName("v")] string Version)
{
    public bool Equals(DiagEvent? o) => o is not null && At == o.At && Level == o.Level &&
        Code == o.Code && Message == o.Message && Version == o.Version &&
        Context.Count == o.Context.Count && !Context.Except(o.Context).Any();
    public override int GetHashCode() => HashCode.Combine(At, Level, Code, Message, Version);
}

public sealed class DiagnosticsLog
{
    public const int MaxBytes = 1_048_576;
    public const int KeptGenerations = 2;

    private readonly string _path;
    private readonly string _version;
    private readonly object _lock = new();
    public DiagLevel MinimumLevel { get; set; }

    public DiagnosticsLog(string path, string version, DiagLevel minimumLevel = DiagLevel.info)
    {
        _path = path;
        _version = version;
        MinimumLevel = minimumLevel;
    }

    public static string DefaultPath(string? home = null) =>
        Path.Combine(RedlineHome.DataDir(home), "diagnostics.ndjson");

    public void Log(DiagLevel level, string code, string message,
                    Dictionary<string, string>? context = null, DateTimeOffset? now = null)
    {
        if (level < MinimumLevel) return;
        var ctx = (context ?? new()).ToDictionary(kv => kv.Key, kv => Redaction.Scrub(kv.Value));
        Append(new DiagEvent(Stamp(now ?? DateTimeOffset.UtcNow), level, code,
                             Redaction.Scrub(message), ctx, _version));
    }

    public void Debug(string code, string m, Dictionary<string, string>? c = null) => Log(DiagLevel.debug, code, m, c);
    public void Info(string code, string m, Dictionary<string, string>? c = null) => Log(DiagLevel.info, code, m, c);
    public void Warn(string code, string m, Dictionary<string, string>? c = null) => Log(DiagLevel.warn, code, m, c);
    public void Error(string code, string m, Dictionary<string, string>? c = null) => Log(DiagLevel.error, code, m, c);

    /// Runs `body`, recording a thrown exception against `code` and returning default.
    public T? Attempt<T>(string code, Dictionary<string, string>? context, Func<T> body)
    {
        try { return body(); }
        catch (Exception e)
        {
            var c = new Dictionary<string, string>(context ?? new()) { ["error"] = e.Message };
            Error(code, "operation failed", c);
            return default;
        }
    }

    public bool Attempt(string code, Dictionary<string, string>? context, Action body) =>
        Attempt<bool>(code, context, () => { body(); return true; });

    /// Events on disk, oldest first, across every kept generation. Bad lines are skipped.
    public List<DiagEvent> Read(DiagLevel minimumLevel = DiagLevel.debug,
                                DateTimeOffset? since = null, int? limit = null)
    {
        lock (_lock)
        {
            var output = new List<DiagEvent>();
            for (var gen = KeptGenerations - 1; gen >= 0; gen--)
            {
                var file = gen == 0 ? _path : $"{_path}.{gen}";
                string text;
                try { if (!File.Exists(file)) continue; text = File.ReadAllText(file); }
                catch { continue; }
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    DiagEvent? e;
                    try { e = JsonSerializer.Deserialize<DiagEvent>(line); } catch { continue; }
                    if (e is null || e.Level < minimumLevel) continue;
                    if (since is not null && Parse(e.At) is { } at && at < since) continue;
                    output.Add(e);
                }
            }
            if (limit is { } l && output.Count > l) output = output.Skip(output.Count - l).ToList();
            return output;
        }
    }

    /// How often each code fired, most frequent first.
    public List<(string Code, int Count, string Latest)> Tally(DiagLevel minimumLevel = DiagLevel.warn,
                                                               DateTimeOffset? since = null)
    {
        var counts = new Dictionary<string, (int, string)>();
        foreach (var e in Read(minimumLevel, since))
        {
            counts.TryGetValue(e.Code, out var prior);
            counts[e.Code] = (prior.Item1 + 1, e.At);
        }
        return counts.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2))
            .OrderByDescending(t => t.Item2).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    private void Append(DiagEvent e)
    {
        lock (_lock)
        {
            // A diagnostics write must never take the app down, so every failure is silent.
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\n");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded(bytes.Length);
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes);
            }
            catch { }
        }
    }

    private void RotateIfNeeded(int adding)
    {
        var size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (size + adding <= MaxBytes) return;
        var older = $"{_path}.{KeptGenerations - 1}";
        try { File.Delete(older); } catch { }
        try { File.Move(_path, older); } catch { }
    }

    public static string Stamp(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string s) =>
        DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// Keeps secrets and personal paths out of a file whose whole purpose is to be shared.
public static class Redaction
{
    private static readonly string[] Secretish =
        { "sk-", "sk_", "oauth", "bearer", "token", "secret", "password", "authorization" };

    public static string Scrub(string s)
    {
        var output = s;
        var home = RedlineHome.AccountHome;
        if (!string.IsNullOrEmpty(home))
            output = output.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        var lowered = output.ToLowerInvariant();
        if (!Secretish.Any(lowered.Contains)) return output;
        return string.Join(" ", output.Split(' ').Select(w =>
        {
            var bare = w.Trim(w.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray());
            return bare.Length >= 20 && bare.Any(char.IsDigit) ? "<redacted>" : w;
        }));
    }
}

/// The process-wide log, configured once at startup.
public static class Diag
{
    private static readonly object Lock = new();
    private static DiagnosticsLog? _instance;

    public static void Configure(string version, string? path = null, DiagLevel minimumLevel = DiagLevel.info)
    {
        lock (Lock) _instance = new DiagnosticsLog(path ?? DiagnosticsLog.DefaultPath(), version, minimumLevel);
    }

    public static DiagnosticsLog Log
    {
        get { lock (Lock) return _instance ??= new DiagnosticsLog(DiagnosticsLog.DefaultPath(), "unknown"); }
    }
}
