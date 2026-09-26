// Reads the JSONL written by the ollama shim (`redlinectl ollama`). Ollama keeps no usage
// history of its own, so anything that bypasses the shim is invisible here by design.
namespace Redline.Core;

/// Cloud models carry a "cloud" tag in their name; everything else runs on this machine.
public static class OllamaLocality
{
    public static bool IsCloud(string model)
    {
        var m = model.ToLowerInvariant();
        return m.EndsWith(":cloud", StringComparison.Ordinal) || m.EndsWith("-cloud", StringComparison.Ordinal) ||
               m.Contains(":cloud-", StringComparison.Ordinal);
    }

    /// A cloud model gets the cloud glyph in front of its name, visible in every list at no width.
    public static string Marked(string model) => IsCloud(model) ? "☁ " + model : model;
}

public sealed class OllamaStore
{
    public const string Provider = "Ollama";
    private readonly string _log;

    public OllamaStore(string? log = null)
    {
        _log = log ?? RedlineHome.PathFor(".local/share/redline/ollama.jsonl");
    }

    public bool IsConfigured => File.Exists(_log);

    public List<Entry> Scan(int lookbackDays, DateTimeOffset? now = null)
    {
        var output = new List<Entry>();
        string text;
        try { text = File.ReadAllText(_log); } catch { return output; }
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddSeconds(-(double)(lookbackDays + 1) * 86400);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (EntryFrom(line, null) is { } e && e.Ts > cutoff) output.Add(e);
        }
        return output;
    }

    /// Reads what the shim has appended since the last pass and stores it.
    public int Ingest(Warehouse warehouse, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        FileInfo info;
        try { info = new FileInfo(_log); if (!info.Exists) return 0; }
        catch { return 0; }
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var size = info.Length;
        var mark = warehouse.IngestMark(_log);
        var start = TranscriptTail.StartOffset(mark, size);
        if (mark is not null && start == mark.ByteOffset && size == mark.Size) return 0;

        var batch = new List<Entry>();
        var next = TranscriptTail.Read(_log, start, (line, offset) =>
        {
            if (EntryFrom(line, offset) is { } e) batch.Add(e);
        });
        var added = warehouse.Ingest(batch);
        warehouse.SetIngestMark(new IngestMark(_log, Provider, size, next, mtime), at);
        return added;
    }

    internal Entry? EntryFrom(string line, long? offset)
    {
        if (Json.ParseObject(line) is not { } o ||
            TranscriptTail.ParseTimestamp(Json.Str(o["ts"])) is not { } ts) return null;
        var input = Int(o["prompt_eval_count"]);
        var output = Int(o["eval_count"]);
        if (input + output <= 0) return null;
        // Local inference has no dollar cost, so these stay unpriced and surface as volume, not spend
        return new Entry(Provider, null, ts, Json.Str(o["model"]) ?? "ollama",
                         input, output, 0, 0, 0, offset is { } off ? $"{_log}#{off}" : null);
    }

    private static int Int(System.Text.Json.Nodes.JsonNode? v) => Json.Num(v) is { } d ? (int)d : 0;
}
