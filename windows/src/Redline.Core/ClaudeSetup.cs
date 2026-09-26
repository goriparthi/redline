// What Claude Code has been configured with, read from the files it reads; nothing is written, and
// nothing outside ~/.claude and a project's own CLAUDE.md is opened. Unparseable yields nothing.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Redline.Core;

public static class ClaudeSetup
{
    /// Servers named in ~/.claude.json, globally and per project, plus settings.json. Names only.
    public static List<string> McpServers(string? home = null)
    {
        var root = home ?? RedlineHome.Url;
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? any)
        {
            if (any is not JsonObject dict) return;
            foreach (var (key, _) in dict) names.Add(key);
        }
        if (ReadJson(RedlineHome.Join(root, ".claude.json")) is { } json)
        {
            Collect(json["mcpServers"]);
            if (json["projects"] is JsonObject projects)
                foreach (var (_, value) in projects) Collect((value as JsonObject)?["mcpServers"]);
        }
        if (ReadJson(RedlineHome.Join(root, ".claude/settings.json")) is { } settings)
            Collect(settings["mcpServers"]);
        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// A skill is a directory under ~/.claude/skills holding SKILL.md; a stray file is not one.
    public static List<string> Skills(string? home = null)
    {
        var root = RedlineHome.Join(home ?? RedlineHome.Url, ".claude/skills");
        try
        {
            return Directory.GetFileSystemEntries(root)
                .Where(p => File.Exists(Path.Combine(p, "SKILL.md")))
                .Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch { return new(); }
    }

    public static List<string> Agents(string? home = null) =>
        MarkdownNames(RedlineHome.Join(home ?? RedlineHome.Url, ".claude/agents"));

    /// Commands, including one level of namespace directories. The namespace is dropped:
    /// the transcript records the invoked name.
    public static List<string> Commands(string? home = null)
    {
        var root = RedlineHome.Join(home ?? RedlineHome.Url, ".claude/commands");
        var output = MarkdownNames(root);
        try
        {
            foreach (var dir in Directory.GetDirectories(root)) output.AddRange(MarkdownNames(dir));
        }
        catch { }
        return output.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    internal static List<string> MarkdownNames(string dir)
    {
        try
        {
            return Directory.GetFileSystemEntries(dir)
                .Where(p => Path.GetExtension(p) == ".md")
                .Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch { return new(); }
    }

    /// Memory files and their size in characters with @imports expanded, each counted once.
    /// The global file is charged to every session, a project's only to its own.
    public static List<MemoryFile> MemoryFiles(IReadOnlyDictionary<string, int> sessionsByDir, string? home = null)
    {
        var root = home ?? RedlineHome.Url;
        var total = sessionsByDir.Values.Sum();
        var targets = new List<(string Path, int Sessions, bool Global)>
        {
            (RedlineHome.Join(root, ".claude/CLAUDE.md"), total, true),
        };
        targets.AddRange(sessionsByDir.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => (Path.Combine(k, "CLAUDE.md"), sessionsByDir[k], false)));
        var output = new List<MemoryFile>();
        // Windows paths compare without case, so one file reached two ways counts once
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var chars = 0;
            Expand(target.Path, root, 0, seen, n => chars += n);
            if (chars <= 0) continue;
            output.Add(new MemoryFile(target.Path, chars, target.Sessions, target.Global));
        }
        return output;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// Depth is capped as well as `seen` preventing a loop: a deeper chain is not silently followed.
    internal static void Expand(string path, string home, int depth, HashSet<string> seen, Action<int> add)
    {
        if (depth >= 3) return;
        string key;
        try { key = Path.GetFullPath(path); } catch { return; }
        if (seen.Contains(key)) return;
        string text;
        try { text = File.ReadAllText(path, StrictUtf8); } catch { return; }
        seen.Add(key);
        // Characters as Swift counts them: grapheme clusters, not UTF-16 units
        add(new StringInfo(text).LengthInTextElements);
        var imports = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim(' ', '\t');
            if (!trimmed.StartsWith('@') || trimmed.Length <= 1) continue;
            var raw = trimmed[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(raw)) continue;
            if (raw.StartsWith("~/")) imports.Add(RedlineHome.Join(home, raw[2..]));
            else if (raw.StartsWith('/') || Path.IsPathFullyQualified(raw)) imports.Add(raw);
            else imports.Add(Path.Combine(Path.GetDirectoryName(path) ?? "", raw));
        }
        foreach (var next in imports) Expand(next, home, depth + 1, seen, add);
    }

    internal static JsonObject? ReadJson(string path) => Json.ReadObject(path);

    /// Gathers everything the checks need, so the app, the CLI and the tests assemble the same input.
    public static global::Redline.Core.FindingsInput FindingsInput(IReadOnlyList<SessionScan> sessions, int windowDays,
                                                                     DateTimeOffset? now = null, string? home = null)
    {
        var byDir = new Dictionary<string, int>();
        foreach (var cwd in sessions.Select(s => s.Cwd).OfType<string>())
            byDir[cwd] = byDir.GetValueOrDefault(cwd) + 1;
        return new global::Redline.Core.FindingsInput(sessions.ToList(), McpServers(home), Skills(home), Agents(home),
                                 Commands(home), MemoryFiles(byDir, home), windowDays,
                                 now ?? DateTimeOffset.UtcNow);
    }
}
