// Parses Codex CLI rollout transcripts (~/.codex/sessions/**/*.jsonl). Needs no auth:
// Codex writes both its rate-limit percentages and its token counts straight to disk.
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed class CodexSnapshot
{
    public List<Entry> Entries { get; set; } = new();
    public List<LimitWindow> Limits { get; set; } = new();
    public DateTimeOffset? LimitsAt { get; set; }
}

public sealed class CodexStore
{
    public const string Provider = "Codex";
    private readonly string _root;

    public CodexStore(string? root = null)
    {
        _root = root ?? RedlineHome.PathFor(".codex/sessions");
    }

    /// Stores what is new in every rollout and returns the percentages. A pass that reads no new
    /// lines falls back to the newest stored reading, and says when it was taken.
    public CodexSnapshot Ingest(Warehouse warehouse, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var snap = new CodexSnapshot();
        if (!Directory.Exists(_root)) return snap;

        var live = new HashSet<string>();
        foreach (var info in UsageStore.Transcripts(_root))
        {
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var size = info.Length;
            var path = info.FullName;
            live.Add(path);
            var mark = warehouse.IngestMark(path);
            var start = TranscriptTail.StartOffset(mark, size);
            if (mark is not null && start == mark.ByteOffset && size == mark.Size) continue;

            var batch = new List<Entry>();
            var next = TranscriptTail.Read(path, start, (line, offset) =>
            {
                var parsed = Event(line, path, offset);
                if (parsed.Entry is { } entry) batch.Add(entry);
                if (parsed.Limits is { Count: > 0 } windows && parsed.Ts is { } ts &&
                    ts > (snap.LimitsAt ?? DateTimeOffset.MinValue))
                {
                    snap.Limits = windows;
                    snap.LimitsAt = ts;
                }
            });
            warehouse.Ingest(batch);
            snap.Entries.AddRange(batch);
            warehouse.SetIngestMark(new IngestMark(path, Provider, size, next, mtime), at);
        }
        warehouse.ForgetIngestMarks(live, Provider);

        if (snap.Limits.Count == 0 && warehouse.LatestLimits(Provider) is { } stored)
        {
            snap.Limits = stored.Windows.ToList();
            snap.LimitsAt = stored.At;
        }
        // Sessions can be days old, so discard windows that have already rolled over
        snap.Limits = LimitParser.Sorted(LimitParser.Unexpired(snap.Limits, at));
        return snap;
    }

    public CodexSnapshot Scan(int lookbackDays, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var snap = new CodexSnapshot();
        if (!Directory.Exists(_root)) return snap;

        var cutoff = t.AddSeconds(-(double)(lookbackDays + 1) * 86400);
        foreach (var info in UsageStore.Transcripts(_root))
        {
            // Limits come from the newest event overall, so a stale file cannot supply them
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (!(mtime > cutoff)) continue;
            var part = Parse(info.FullName, cutoff);
            snap.Entries.AddRange(part.Entries);
            if (part.LimitsAt is { } at && at > (snap.LimitsAt ?? DateTimeOffset.MinValue))
            {
                snap.Limits = part.Limits;
                snap.LimitsAt = at;
            }
        }
        // Sessions can be days old, so discard windows that have already rolled over
        snap.Limits = LimitParser.Sorted(LimitParser.Unexpired(snap.Limits, t));
        return snap;
    }

    internal CodexSnapshot Parse(string path, DateTimeOffset cutoff)
    {
        var snap = new CodexSnapshot();
        string text;
        try { text = File.ReadAllText(path); } catch { return snap; }
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parsed = Event(line, path, null);
            if (parsed.Ts is not { } ts || !(ts > cutoff)) continue;
            if (parsed.Limits is { Count: > 0 } windows && ts > (snap.LimitsAt ?? DateTimeOffset.MinValue))
            {
                snap.Limits = windows;
                snap.LimitsAt = ts;
            }
            if (parsed.Entry is { } entry) snap.Entries.Add(entry);
        }
        return snap;
    }

    /// One rollout line, split into the three things it can carry. Both readers go through here
    /// so a tailed file and a whole file cannot disagree about what a line means.
    internal (Entry? Entry, List<LimitWindow>? Limits, DateTimeOffset? Ts) Event(string line, string path, long? offset)
    {
        if (!line.Contains("token_count")) return (null, null, null);
        if (Json.ParseObject(line) is not { } obj ||
            obj["payload"] is not JsonObject payload ||
            Json.Str(payload["type"]) != "token_count" ||
            TranscriptTail.ParseTimestamp(Json.Str(obj["timestamp"])) is not { } ts)
            return (null, null, null);

        List<LimitWindow>? limits = null;
        if (payload["rate_limits"] is JsonObject rl) limits = LimitParser.CodexRateLimits(rl);

        // last_token_usage is the per-turn delta; total_token_usage is cumulative and would double count
        if (payload["info"] is not JsonObject info || info["last_token_usage"] is not JsonObject last)
            return (null, limits, ts);
        var input = Int(last["input_tokens"]);
        var cached = Int(last["cached_input_tokens"]);
        var output = Int(last["output_tokens"]) + Int(last["reasoning_output_tokens"]);
        // Codex counts cached tokens inside input_tokens; split them so cache reads are not billed as fresh
        var fresh = Math.Max(0, input - cached);
        if (fresh + cached + output <= 0) return (null, limits, ts);
        var entry = new Entry(Provider, null, ts, Model(obj, payload), fresh, output, cached, 0, 0,
                              offset is { } o ? $"{path}#{o}" : null);
        return (entry, limits, ts);
    }

    private static string Model(JsonObject obj, JsonObject payload) =>
        Json.Str(payload["model"]) ?? Json.Str(obj["model"]) ?? "codex";

    private static int Int(JsonNode? v) => Json.Num(v) is { } d ? (int)d : 0;
}
