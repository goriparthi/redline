// Setup findings: what the transcripts say about how the tool is configured. Never invent a
// figure; a finding with no honest saving attached is reported without one.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// What the reader is expected to do about a finding. "Fix this now" and "this is how you
/// work" call for different reactions and should not be mixed.
public enum FindingKind { FixNow, Habit, Fyi }

/// `Measured` means counted from the transcripts; `Estimated` means derived through an
/// assumption that is stated.
public enum FindingBasis { Measured, Estimated }

public static class FindingKindExt
{
    public static string RawValue(this FindingKind k) => k switch
    {
        FindingKind.FixNow => "fix",
        FindingKind.Habit => "habit",
        _ => "fyi",
    };

    public static string Label(this FindingKind k) => k switch
    {
        FindingKind.FixNow => "Fix now",
        FindingKind.Habit => "Habit",
        _ => "FYI",
    };

    public static string RawValue(this FindingBasis b) => b == FindingBasis.Measured ? "measured" : "estimated";
}

/// One row behind a claim: a name down one edge, what was counted down the other. `Value`
/// is null when the name is the whole finding.
public sealed record FindingEvidence(string Label, string? Value = null)
{
    public string Id => Label + "|" + (Value ?? "");
}

public sealed record Finding
{
    public string Id { get; }
    public FindingKind Kind { get; }
    public FindingBasis Basis { get; }
    public string Title { get; }
    /// One or two sentences: what was observed and why it matters, nothing else.
    public string Detail { get; }
    public IReadOnlyList<FindingEvidence> Evidence { get; }
    /// Null whenever no honest figure exists. A null here is a deliberate answer.
    public int? EstimatedTokens { get; }
    public double? EstimatedUSD { get; }
    /// Something to copy or run. Null when the fix is a judgement rather than an edit.
    public string? Fix { get; }

    public Finding(string id, FindingKind kind, FindingBasis basis, string title, string detail,
                   IReadOnlyList<FindingEvidence>? evidence = null, int? estimatedTokens = null,
                   double? estimatedUSD = null, string? fix = null)
    {
        Id = id;
        Kind = kind;
        Basis = basis;
        Title = title;
        Detail = detail;
        Evidence = evidence ?? Array.Empty<FindingEvidence>();
        EstimatedTokens = estimatedTokens;
        EstimatedUSD = estimatedUSD;
        Fix = fix;
    }

    public bool Equals(Finding? o) => o is not null && Id == o.Id && Kind == o.Kind &&
        Basis == o.Basis && Title == o.Title && Detail == o.Detail &&
        Evidence.SequenceEqual(o.Evidence) && EstimatedTokens == o.EstimatedTokens &&
        EstimatedUSD == o.EstimatedUSD && Fix == o.Fix;
    public override int GetHashCode() => HashCode.Combine(Id, Kind, Title);
}

public sealed record FindingsReport
{
    public DateTimeOffset GeneratedAt { get; }
    public int WindowDays { get; }
    public int SessionsScanned { get; }
    public IReadOnlyList<Finding> Findings { get; }
    /// How many were dismissed and are held back, so the panel can say something is hidden
    /// rather than letting a dismissal look like a finding that stopped being true.
    public int Hidden { get; }

    public FindingsReport(DateTimeOffset generatedAt, int windowDays, int sessionsScanned,
                          IReadOnlyList<Finding> findings, int hidden = 0)
    {
        GeneratedAt = generatedAt;
        WindowDays = windowDays;
        SessionsScanned = sessionsScanned;
        Findings = findings;
        Hidden = hidden;
    }

    public bool IsEmpty => Findings.Count == 0;

    /// Total of the savings that could honestly be computed. Findings without a figure are
    /// not counted, and the caller says so.
    public double EstimatedUSD => Findings.Sum(f => f.EstimatedUSD ?? 0);

    public Dictionary<FindingKind, int> CountsByKind =>
        Findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());

    /// "3 findings · 1 to fix". The line the menu shows.
    public string Summary
    {
        get
        {
            if (Findings.Count == 0) return "no findings";
            var parts = new List<string> { $"{Findings.Count} finding{(Findings.Count == 1 ? "" : "s")}" };
            if (CountsByKind.TryGetValue(FindingKind.FixNow, out var fix) && fix > 0) parts.Add($"{fix} to fix");
            return string.Join(" · ", parts);
        }
    }

    public bool Equals(FindingsReport? o) => o is not null && GeneratedAt == o.GeneratedAt &&
        WindowDays == o.WindowDays && SessionsScanned == o.SessionsScanned &&
        Hidden == o.Hidden && Findings.SequenceEqual(o.Findings);
    public override int GetHashCode() => HashCode.Combine(GeneratedAt, WindowDays, Findings.Count);
}

// MARK: - Transcript scanning

/// One tool call, reduced to the fields the checks ask about. `Subject` is the skill name,
/// subagent type, or the head of a Bash command, depending on the tool.
public sealed record ToolUse(string? Id, string Name, string? FilePath, string? Subject, DateTimeOffset? Ts);

/// `ResultChars` is characters returned per tool_use id: the size of a re-read is the only
/// measurable part of what a re-read costs.
public sealed record SessionScan(string Path, string? Cwd, string? LastModel, DateTimeOffset? Start,
                                 DateTimeOffset? End, IReadOnlyList<ToolUse> Tools,
                                 IReadOnlyDictionary<string, int> ResultChars,
                                 IReadOnlyList<string> CommandNames);

public sealed class TranscriptScanner
{
    private readonly string _root;
    private Dictionary<string, (DateTime Mtime, long Size, SessionScan Scan)> _cache = new();

    public TranscriptScanner(string? root = null)
    {
        _root = root ?? RedlineHome.PathFor(".claude/projects");
    }

    /// Sessions touched inside the window, cached on mtime and size like the usage scan.
    public List<SessionScan> Scan(int lookbackDays, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddSeconds(-(double)lookbackDays * 86400);
        var output = new List<SessionScan>();
        if (!Directory.Exists(_root)) return output;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_root, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0,
            }).ToList();
        }
        catch { return output; }
        var live = new HashSet<string>();
        foreach (var path in files)
        {
            if (!string.Equals(System.IO.Path.GetExtension(path), ".jsonl", StringComparison.Ordinal)) continue;
            DateTime mtime;
            long size;
            try
            {
                var info = new FileInfo(path);
                mtime = info.LastWriteTimeUtc;
                size = info.Length;
            }
            catch { continue; }
            if (new DateTimeOffset(mtime, TimeSpan.Zero) <= cutoff) continue;
            live.Add(path);
            if (_cache.TryGetValue(path, out var c) && c.Mtime == mtime && c.Size == size)
            {
                output.Add(c.Scan);
                continue;
            }
            var scan = Parse(path);
            _cache[path] = (mtime, size, scan);
            output.Add(scan);
        }
        _cache = _cache.Where(kv => live.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return output;
    }

    internal SessionScan Parse(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch
        {
            return new SessionScan(path, null, null, null, null, Array.Empty<ToolUse>(),
                new Dictionary<string, int>(), Array.Empty<string>());
        }
        string? cwd = null;
        string? lastModel = null;
        DateTimeOffset? start = null, end = null;
        var tools = new List<ToolUse>();
        var results = new Dictionary<string, int>();
        var commands = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // Cheap gate first: most lines are plain text exchanges no check looks at
            var interesting = line.Contains("\"tool_use\"") || line.Contains("\"tool_result\"")
                || line.Contains("<command-name>") || cwd is null;
            if (!interesting || Json.ParseObject(line) is not { } obj) continue;
            if (cwd is null && Json.Str(obj["cwd"]) is { Length: > 0 } c) cwd = c;
            var ts = ParseTimestamp(Json.Str(obj["timestamp"]));
            if (ts is { } t)
            {
                if (start is null || t < start) start = t;
                if (end is null || t > end) end = t;
            }
            if (obj["message"] is not JsonObject msg) continue;
            if (Json.Str(msg["model"]) is { } m && m != "<synthetic>") lastModel = m;
            if (!(msg["content"] is JsonArray arr && arr.All(b => b is JsonObject)))
            {
                // A slash command arrives as a plain string body carrying the tag
                if (Json.Str(msg["content"]) is { } body) commands.AddRange(CommandNames(body));
                continue;
            }
            foreach (var block in arr.Cast<JsonObject>())
            {
                switch (Json.Str(block["type"]))
                {
                    case "tool_use":
                        if (Json.Str(block["name"]) is not { } name) continue;
                        var input = block["input"] as JsonObject ?? new JsonObject();
                        tools.Add(new ToolUse(Json.Str(block["id"]), name, Json.Str(input["file_path"]),
                            Subject(name, input), ts));
                        break;
                    case "tool_result":
                        if (Json.Str(block["tool_use_id"]) is not { } id) continue;
                        results[id] = ResultLength(block["content"]);
                        break;
                    case "text":
                        if (Json.Str(block["text"]) is { } body && body.Contains("<command-name>"))
                            commands.AddRange(CommandNames(body));
                        break;
                }
            }
        }
        return new SessionScan(path, cwd, lastModel, start, end, tools, results, commands);
    }

    private static readonly string[] IsoFormats =
        { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK" };

    internal static DateTimeOffset? ParseTimestamp(string? s) =>
        s is not null && DateTimeOffset.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// The one input field each tool is identified by. Skills, agents and MCP servers are all
    /// "was this ever actually used", and each spells that differently.
    internal static string? Subject(string tool, JsonObject input) => tool switch
    {
        "Skill" => Json.Str(input["skill"]) ?? Json.Str(input["command"]),
        "Task" or "Agent" => Json.Str(input["subagent_type"]),
        "Bash" => Json.Str(input["command"])?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
        _ => null,
    };

    /// Characters as a person counts them (grapheme clusters), matching Swift's `count`.
    internal static int ResultLength(JsonNode? content)
    {
        if (Json.Str(content) is { } s) return new StringInfo(s).LengthInTextElements;
        if (content is JsonArray arr && arr.All(b => b is JsonObject))
            return arr.Cast<JsonObject>().Sum(b => Json.Str(b["text"]) is { } t ? new StringInfo(t).LengthInTextElements : 0);
        return 0;
    }

    public static List<string> CommandNames(string body)
    {
        const string open = "<command-name>", close = "</command-name>";
        var output = new List<string>();
        var pos = 0;
        while (true)
        {
            var o = body.IndexOf(open, pos, StringComparison.Ordinal);
            if (o < 0) break;
            var from = o + open.Length;
            var c = body.IndexOf(close, from, StringComparison.Ordinal);
            if (c < 0) break;
            var name = body[from..c].Trim().Trim('/');
            if (name.Length > 0) output.Add(name);
            pos = c + close.Length;
        }
        return output;
    }
}

// MARK: - The checks

/// One memory file: what it costs to carry and how often it is actually carried. `Sessions`
/// is counted, not assumed; `Global` is true for ~/.claude/CLAUDE.md.
public sealed record MemoryFile(string Path, int Chars, int Sessions, bool Global);

public sealed class FindingsInput
{
    public List<SessionScan> Sessions { get; set; }
    public List<string> ConfiguredMCPServers { get; set; }
    public List<string> Skills { get; set; }
    public List<string> Agents { get; set; }
    public List<string> Commands { get; set; }
    /// Already expanded through @imports, each with how many scanned sessions load it.
    public List<MemoryFile> MemoryFiles { get; set; }
    public int WindowDays { get; set; }
    public DateTimeOffset Now { get; set; }

    public FindingsInput(IEnumerable<SessionScan> sessions, IEnumerable<string> configuredMCPServers,
                         IEnumerable<string> skills, IEnumerable<string> agents, IEnumerable<string> commands,
                         IEnumerable<MemoryFile> memoryFiles, int windowDays, DateTimeOffset? now = null)
    {
        Sessions = sessions.ToList();
        ConfiguredMCPServers = configuredMCPServers.ToList();
        Skills = skills.ToList();
        Agents = agents.ToList();
        Commands = commands.ToList();
        MemoryFiles = memoryFiles.ToList();
        WindowDays = windowDays;
        Now = now ?? DateTimeOffset.UtcNow;
    }
}

public static class Findings
{
    /// Characters per token. Rough, used only where the result is labelled an estimate.
    public const double CharsPerToken = 4.0;

    /// A file read this many times in one session is being re-read rather than read.
    public const int RereadThreshold = 3;

    /// Estimated tokens above which a memory file is worth mentioning.
    public const int MemoryTokenThreshold = 5_000;

    public static FindingsReport Report(FindingsInput input, Config config)
    {
        var findings = new List<Finding>();
        findings.AddRange(UnusedMCPServers(input));
        findings.AddRange(RereadFiles(input, config));
        findings.AddRange(HeavyMemoryFiles(input, config));
        findings.AddRange(Ghosts(input));
        // Costed findings first, then by kind: a number attached is what to meet first
        var sorted = findings.OrderByDescending(f => f.EstimatedUSD ?? -1)
                             .ThenBy(f => (int)f.Kind).ToList();
        return new FindingsReport(input.Now, input.WindowDays, input.Sessions.Count, sorted);
    }

    internal static List<Finding> UnusedMCPServers(FindingsInput input)
    {
        if (input.ConfiguredMCPServers.Count == 0 || input.Sessions.Count == 0) return new();
        var used = new HashSet<string>();
        foreach (var session in input.Sessions)
            foreach (var tool in session.Tools)
            {
                if (!tool.Name.StartsWith("mcp__", StringComparison.Ordinal)) continue;
                var server = tool.Name[5..].Split("__")[0];
                used.Add(Normalize(server));
            }
        var unused = input.ConfiguredMCPServers.Where(s => !used.Contains(Normalize(s)))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (unused.Count == 0) return new();
        return new()
        {
            new Finding(
                "mcp-unused", FindingKind.Habit, FindingBasis.Measured,
                $"{unused.Count} MCP server{(unused.Count == 1 ? "" : "s")} configured but never called",
                "Every session that loads a server pays for its tool schemas in the prompt, whether " +
                "or not a tool is called. These were not called once in " +
                $"{input.WindowDays} days. The schema cost is real but is not visible " +
                "from here, so no figure is claimed for it.",
                unused.Select(u => new FindingEvidence(u, "never called")).ToList(),
                fix: "Remove or disable the unused servers in ~/.claude.json, " +
                     "or scope them to the projects that need them."),
        };
    }

    internal static List<Finding> RereadFiles(FindingsInput input, Config config)
    {
        var wastedChars = 0;
        var perFile = new Dictionary<string, (int Reads, int Chars)>();
        var costUSD = 0.0;
        var priced = false;

        foreach (var session in input.Sessions)
        {
            var byFile = new Dictionary<string, List<ToolUse>>();
            foreach (var tool in session.Tools)
            {
                if (tool.Name != "Read" || tool.FilePath is not { } path) continue;
                if (!byFile.TryGetValue(path, out var list)) byFile[path] = list = new List<ToolUse>();
                list.Add(tool);
            }
            var price = session.LastModel is { } model ? config.Price(model) : null;
            foreach (var (path, reads) in byFile)
            {
                if (reads.Count < RereadThreshold) continue;
                // The first read is the work; every later one is the finding.
                var extra = reads.Skip(1).ToList();
                var chars = extra.Sum(r => session.ResultChars.TryGetValue(r.Id ?? "", out var n) ? n : 0);
                if (chars <= 0) continue;
                wastedChars += chars;
                var slot = perFile.TryGetValue(path, out var s) ? s : (Reads: 0, Chars: 0);
                perFile[path] = (slot.Reads + extra.Count, slot.Chars + chars);
                if (price is not null)
                {
                    priced = true;
                    costUSD += chars / CharsPerToken / 1_000_000 * price.Input;
                }
            }
        }
        if (wastedChars <= 0) return new();
        var tokens = (int)(wastedChars / CharsPerToken);
        var top = perFile.OrderByDescending(kv => kv.Value.Chars).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(kv => new FindingEvidence(LastPathComponent(kv.Key),
                $"{kv.Value.Reads} extra reads · ~{Usage.FmtTokens((int)(kv.Value.Chars / CharsPerToken))} tokens"))
            .ToList();
        return new()
        {
            new Finding(
                "reread-files", FindingKind.Habit, FindingBasis.Estimated,
                $"Files read {RereadThreshold}+ times in a single session",
                "The same file came back into context repeatedly inside one session. " +
                "Character counts are measured; the token and dollar figures divide them " +
                $"by {(int)CharsPerToken} characters per token and price them at the " +
                "session's own model, so treat them as an order of magnitude.",
                top, tokens, priced ? costUSD : null,
                "Ask for the region rather than the file, or keep the result in the " +
                "conversation instead of re-reading after each shell step."),
        };
    }

    internal static List<Finding> HeavyMemoryFiles(FindingsInput input, Config config)
    {
        var heavy = input.MemoryFiles
            .Select(f => (File: f, Tokens: (int)(f.Chars / CharsPerToken)))
            .Where(r => r.Tokens >= MemoryTokenThreshold && r.File.Sessions > 0)
            .OrderByDescending(r => r.Tokens).ToList();
        if (heavy.Count == 0) return new();
        var tokens = heavy.Sum(r => r.Tokens);
        // Priced at a cache write, what a system prompt costs on a session's first request,
        // times the sessions that actually load each file rather than every session
        var price = config.Price("sonnet");
        double? usd = price is null ? null
            : heavy.Sum(r => r.Tokens / 1_000_000.0 * price.Input * 1.25 * r.File.Sessions);
        var loads = heavy.Sum(r => r.File.Sessions);
        return new()
        {
            new Finding(
                "memory-heavy", FindingKind.Fyi, FindingBasis.Estimated,
                $"~{Usage.FmtTokens(tokens)} tokens of memory files carried into sessions",
                "CLAUDE.md and everything it imports are part of the prompt. Sizes and " +
                "session counts are measured; the token figure assumes " +
                $"{(int)CharsPerToken} characters per token and the cost prices " +
                $"{loads} session loads at Sonnet's cache-write rate.",
                heavy.Select(r => new FindingEvidence(ShortPath(r.File.Path),
                    $"~{Usage.FmtTokens(r.Tokens)} tokens · {r.File.Sessions} session" +
                    (r.File.Sessions == 1 ? "" : "s") + (r.File.Global ? " · every project" : ""))).ToList(),
                tokens, usd,
                "Move the parts that only matter to one project into that project's own " +
                "CLAUDE.md, or into a skill that loads on demand."),
        };
    }

    internal static List<Finding> Ghosts(FindingsInput input)
    {
        if (input.Sessions.Count == 0) return new();
        var usedSkills = new HashSet<string>();
        var usedAgents = new HashSet<string>();
        var usedCommands = new HashSet<string>();
        foreach (var session in input.Sessions)
        {
            foreach (var tool in session.Tools)
            {
                if (tool.Subject is not { } raw) continue;
                var subject = Normalize(raw);
                switch (tool.Name)
                {
                    case "Skill": usedSkills.Add(subject); break;
                    case "Task":
                    case "Agent": usedAgents.Add(subject); break;
                }
            }
            foreach (var name in session.CommandNames) usedCommands.Add(Normalize(name));
        }
        // A slash command and a skill share a namespace in practice, so a name used either
        // way counts as used and no ghost is reported for it twice.
        var allUsed = new HashSet<string>(usedSkills);
        allUsed.UnionWith(usedCommands);
        var rows = new List<FindingEvidence>();
        rows.AddRange(input.Skills.Where(s => !allUsed.Contains(Normalize(s)))
            .OrderBy(s => s, StringComparer.Ordinal).Select(s => new FindingEvidence(s, "skill")));
        rows.AddRange(input.Agents.Where(s => !usedAgents.Contains(Normalize(s)))
            .OrderBy(s => s, StringComparer.Ordinal).Select(s => new FindingEvidence(s, "agent")));
        rows.AddRange(input.Commands.Where(s => !allUsed.Contains(Normalize(s)))
            .OrderBy(s => s, StringComparer.Ordinal).Select(s => new FindingEvidence(s, "command")));
        if (rows.Count == 0) return new();
        var plural = rows.Count == 1 ? "" : "s";
        return new()
        {
            new Finding(
                "ghost-definitions", FindingKind.Fyi, FindingBasis.Measured,
                $"{rows.Count} skill{plural}, agent{plural} or command{plural} never invoked",
                $"Defined under ~/.claude and not used once in {input.WindowDays} days. " +
                "Their names and descriptions are cheap to carry, so this is " +
                "housekeeping rather than spend.",
                rows,
                fix: "Archive the ones you have stopped using; keep the rest and ignore this."),
        };
    }

    internal static string Normalize(string s) =>
        s.ToLowerInvariant().Replace("-", "_").Replace(".", "_").Replace(" ", "_");

    internal static string ShortPath(string path)
    {
        var home = RedlineHome.Url;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    private static string LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }
}
