// Parses Claude Code transcripts (~/.claude/projects/**/*.jsonl) into usage entries. Ingest tails
// into the warehouse (Keep Local History); Scan parses the whole window into a per-file cache.
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed class UsageStore
{
    public const string Provider = "Claude";
    private readonly string _root;
    private Dictionary<string, (DateTimeOffset Mtime, long Size, DateTimeOffset Cutoff, List<Entry> Entries)> _fileCache = new();

    public UsageStore(string? root = null)
    {
        _root = root ?? RedlineHome.PathFor(".claude/projects");
    }

    /// Every transcript under a root, recursively and including hidden ones, as Swift walks it.
    internal static IEnumerable<FileInfo> Transcripts(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0,
        };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opts).ToList(); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (!string.Equals(Path.GetExtension(f), ".jsonl", StringComparison.Ordinal)) continue;
            FileInfo info;
            try { info = new FileInfo(f); if (!info.Exists) continue; _ = info.Length; }
            catch { continue; }
            yield return info;
        }
    }

    /// Reads what is new in every transcript and stores it; returns records added. No lookback
    /// applies: a transcript is read once and kept until retention removes it.
    public int Ingest(Warehouse warehouse, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (!Directory.Exists(_root)) return 0;
        var live = new HashSet<string>();
        var added = 0;
        foreach (var info in Transcripts(_root))
        {
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var size = info.Length;
            var path = info.FullName;
            live.Add(path);
            var mark = warehouse.IngestMark(path);
            var start = TranscriptTail.StartOffset(mark, size);
            // Nothing appended since the last pass: this turns a huge corpus into a directory walk
            if (mark is not null && start == mark.ByteOffset && size == mark.Size) continue;

            var batch = new List<Entry>();
            var next = TranscriptTail.Read(path, start, (line, offset) =>
            {
                if (EntryFrom(line, path, offset) is { } e) batch.Add(e);
            });
            added += warehouse.Ingest(batch);
            warehouse.SetIngestMark(new IngestMark(path, Provider, size, next, mtime), at);
        }
        warehouse.ForgetIngestMarks(live, Provider);
        return added;
    }

    public List<Entry> Scan(int lookbackDays, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var cutoff = t.AddSeconds(-(double)(lookbackDays + 1) * 86400);
        var livePaths = new HashSet<string>();
        if (!Directory.Exists(_root)) return new List<Entry>();

        foreach (var info in Transcripts(_root))
        {
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var size = info.Length;
            if (!(mtime > cutoff)) continue;
            var path = info.FullName;
            livePaths.Add(path);
            // The cutoff is part of the key: a cache built for 14 days must not answer a 30 day scan
            if (_fileCache.TryGetValue(path, out var c) && c.Mtime == mtime && c.Size == size &&
                c.Cutoff <= cutoff) continue;
            _fileCache[path] = (mtime, size, cutoff, Parse(path, cutoff));
        }
        _fileCache = _fileCache.Where(kv => livePaths.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        // Dedup across files: resumed sessions copy identical message ids between transcripts
        var seen = new HashSet<string>();
        var output = new List<Entry>();
        foreach (var (_, c) in _fileCache)
        {
            // A cache parsed for a wider window reaches past this cutoff, so it is enforced again
            foreach (var e in c.Entries)
            {
                if (!(e.Ts > cutoff)) continue;
                if (e.Key is { } k && !seen.Add(k)) continue;
                output.Add(e);
            }
        }
        return output;
    }

    internal List<Entry> Parse(string path, DateTimeOffset cutoff)
    {
        string text;
        try { text = File.ReadAllText(path); } catch { return new List<Entry>(); }
        var entries = new List<Entry>();
        // Counted per file, not logged per line, so a format change is one event, not thousands
        var malformed = 0;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (EntryFrom(line, path, null) is { } e)
            {
                if (e.Ts > cutoff) entries.Add(e);
                continue;
            }
            // Only invalid JSON counts; a line with no usage or a synthetic model is a normal skip
            if (line.Contains("\"usage\"") && Json.Parse(line) is null) malformed++;
        }
        if (malformed > 0)
        {
            Diag.Log.Warn("transcript.lines_unparsed", "lines carried usage but were not valid JSON",
                new() { ["path"] = path, ["count"] = malformed.ToString() });
        }
        return entries;
    }

    /// One transcript line to one usage record, or null for the many lines with no usage.
    /// `offset` is where the line started and becomes the record's origin.
    internal Entry? EntryFrom(string line, string path, long? offset)
    {
        if (!line.Contains("\"usage\"")) return null;
        if (Json.ParseObject(line) is not { } obj ||
            obj["message"] is not JsonObject msg ||
            msg["usage"] is not JsonObject usage ||
            TranscriptTail.ParseTimestamp(Json.Str(obj["timestamp"])) is not { } ts) return null;
        var model = Json.Str(msg["model"]) ?? "unknown";
        if (model == "<synthetic>") return null;

        var input = Json.Int(usage["input_tokens"]) ?? 0;
        var output = Json.Int(usage["output_tokens"]) ?? 0;
        var cacheRead = Json.Int(usage["cache_read_input_tokens"]) ?? 0;
        var c5m = Json.Int(usage["cache_creation_input_tokens"]) ?? 0;
        var c1h = 0;
        if (usage["cache_creation"] is JsonObject cc)
        {
            c5m = Json.Int(cc["ephemeral_5m_input_tokens"]) ?? 0;
            c1h = Json.Int(cc["ephemeral_1h_input_tokens"]) ?? 0;
        }
        if (input + output + cacheRead + c5m + c1h <= 0) return null;

        string? key = null;
        if (Json.Str(msg["id"]) is { } mid) key = mid + ":" + (Json.Str(obj["requestId"]) ?? "");
        return new Entry(Provider, key, ts, model, input, output, cacheRead, c5m, c1h,
                         offset is { } o ? $"{path}#{o}" : null);
    }
}
