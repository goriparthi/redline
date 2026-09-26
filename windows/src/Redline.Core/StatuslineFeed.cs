// Claude's rate-limit windows as Claude Code reports them on its statusline payload.
// StatuslineFeeder writes them to a sidecar; StatuslineFeed parses it. No token, no network.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed record StatuslineSnapshot(IReadOnlyList<LimitWindow> Windows, DateTimeOffset? UpdatedAt)
{
    public bool IsEmpty => Windows.Count == 0;

    /// Fresh enough to stand alone; a quiet quarter hour means Claude Code is idle.
    public bool IsFresh(DateTimeOffset? now = null)
    {
        if (UpdatedAt is not { } at) return false;
        return ((now ?? DateTimeOffset.UtcNow) - at).TotalSeconds <= StatuslineFeed.FreshFor;
    }

    public bool Equals(StatuslineSnapshot? o) =>
        o is not null && UpdatedAt == o.UpdatedAt && Windows.SequenceEqual(o.Windows);
    public override int GetHashCode() => HashCode.Combine(Windows.Count, UpdatedAt);
}

public static class StatuslineFeed
{
    public const string Provider = "Claude";

    /// How long a sidecar reading may stand in for a live one, in seconds.
    public const double FreshFor = 900;

    /// Under RedLine's own directory so it never writes into a tree another tool owns.
    public static string DefaultPath(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".local/share/redline/claude-usage.json");

    /// A missing file is the feeder not being installed, which the caller reports differently.
    public static StatuslineSnapshot? Read(string path, DateTimeOffset? now = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch
        {
            Diag.Log.Debug("feed.unreadable", "sidecar not readable", new() { ["path"] = path });
            return null;
        }
        return Parse(data, now);
    }

    public static StatuslineSnapshot? Parse(string text, DateTimeOffset? now = null) =>
        Parse(Encoding.UTF8.GetBytes(text), now);

    public static StatuslineSnapshot? Parse(byte[] data, DateTimeOffset? now = null)
    {
        JsonObject? json = null;
        try { json = JsonNode.Parse(data) as JsonObject; } catch { }
        if (json is null)
        {
            // A file that is there but not JSON is a broken feeder rather than a missing one
            Diag.Log.Error("feed.parse_failed", "sidecar is not valid JSON",
                new() { ["bytes"] = data.Length.ToString(CultureInfo.InvariantCulture) });
            return null;
        }
        return Parse(json, now);
    }

    /// Accepts ClaudeHUD's externalUsageWritePath shape and the raw rate_limits block alike.
    public static StatuslineSnapshot Parse(JsonObject json, DateTimeOffset? now = null)
    {
        var root = json["rate_limits"] as JsonObject ?? json;
        var output = new List<LimitWindow>();

        foreach (var key in new[] { "five_hour", "seven_day" })
        {
            if (root[key] is not JsonObject d) continue;
            // used_percentage is the statusline spelling; utilization is the usage endpoint's
            if ((Json.Num(d["used_percentage"]) ?? Json.Num(d["utilization"])) is not { } pct) continue;
            output.Add(new LimitWindow(Provider, key, pct, Date(d["resets_at"]), Provenance.Official));
        }

        // Model-scoped weeks are named at runtime, so the key comes from the display name
        if (root["model_scoped"] is JsonArray scoped && scoped.All(e => e is JsonObject))
        {
            foreach (var entry in scoped.Cast<JsonObject>())
            {
                if ((Json.Num(entry["utilization"]) ?? Json.Num(entry["used_percentage"])) is not { } pct) continue;
                var name = Json.Str(entry["display_name"])?.Trim() ?? "";
                if (name.Length == 0) continue;
                output.Add(new LimitWindow(Provider, ScopedKey(name), pct, Date(entry["resets_at"]),
                                           Provenance.Official));
            }
        }

        var stamp = Date(json["updated_at"]);
        // A passed reset reports a percentage that no longer exists; the sidecar is often old
        return new StatuslineSnapshot(LimitParser.Sorted(LimitParser.Unexpired(output, now)), stamp);
    }

    /// "Fable" -> "seven_day_fable", so it sorts beside the other weekly windows.
    internal static string ScopedKey(string displayName)
    {
        var slug = new string(displayName.ToLowerInvariant().Replace(" ", "_")
            .Where(c => char.IsLetter(c) || char.IsNumber(c) || c == '_').ToArray());
        return slug.Length == 0 ? "seven_day_scoped" : "seven_day_" + slug;
    }

    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    /// ISO8601 with or without fractional seconds, or epoch seconds or milliseconds.
    internal static DateTimeOffset? Date(JsonNode? value)
    {
        if (Json.Str(value) is { Length: > 0 } s)
        {
            return DateTimeOffset.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }
        if (Json.Num(value) is not { } n || n <= 0) return null;
        // Anything past the year 2286 in seconds is milliseconds instead
        var seconds = n > 1e11 ? n / 1000 : n;
        return DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }
}

/// Runs a chained statusline command with the payload on stdin and returns its stdout.
public delegate string ChainRunner(string command, string stdin);

/// The Windows `redlinectl statusline` feeder: the C# form of scripts/claude-statusline.sh.
/// Only the rate-limit block is written; cwd, session id, paths and costs are discarded.
public sealed class StatuslineFeeder
{
    public const string ChainVariable = "REDLINE_STATUSLINE_CHAIN";
    public const string OutputVariable = "REDLINE_CLAUDE_USAGE";

    public string OutputPath { get; }
    public string? Chain { get; }
    private readonly ChainRunner _runner;
    private readonly Func<DateTimeOffset> _clock;

    /// Null arguments read REDLINE_CLAUDE_USAGE, REDLINE_STATUSLINE_CHAIN and the real shell.
    public StatuslineFeeder(string? outputPath = null, string? chain = null,
                            ChainRunner? runner = null, Func<DateTimeOffset>? clock = null)
    {
        var envOut = Environment.GetEnvironmentVariable(OutputVariable);
        OutputPath = outputPath ?? (string.IsNullOrEmpty(envOut) ? StatuslineFeed.DefaultPath() : envOut);
        Chain = chain ?? Environment.GetEnvironmentVariable(ChainVariable);
        _runner = runner ?? RunShell;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// One statusline draw: write the sidecar, then return what the chained command printed.
    public string Draw(string payload)
    {
        // Like bash's $(cat): trailing newlines are dropped before anything sees the payload
        payload = payload.TrimEnd('\n');
        WriteSidecar(payload);
        if (string.IsNullOrEmpty(Chain)) return "";
        // The chained command owns the visible line and gets the payload exactly as sent
        try { return _runner(Chain, payload) ?? ""; }
        catch { return ""; }
    }

    /// Reads the whole payload, draws, and passes the chain's output through. Always exits 0.
    public int Run(TextReader stdin, TextWriter stdout)
    {
        string payload;
        try { payload = stdin.ReadToEnd(); } catch { payload = ""; }
        var line = Draw(payload);
        try { stdout.Write(line); stdout.Flush(); } catch { }
        return 0;
    }

    /// The statusline must draw even when the sidecar cannot be written, so failures are swallowed.
    public bool WriteSidecar(string payload)
    {
        var text = Extract(payload, _clock());
        if (text is null) return false;
        try
        {
            Json.WriteAtomic(OutputPath, text + "\n");
            return true;
        }
        catch { return false; }
    }

    /// The sidecar body for a payload, or null when it carries no window. Mirrors the jq filter,
    /// since draws with no API call send every window null and nulls would erase a real reading.
    public static string? Extract(string payload, DateTimeOffset now)
    {
        if (Json.Parse(payload) is not JsonObject p) return null;
        var raw = p["rate_limits"];
        if (raw is null || Json.Bool(raw) == false) return null;
        if (raw is not JsonObject r) return null;
        var fiveHour = Truthy(r["five_hour"]);
        var sevenDay = Truthy(r["seven_day"]);
        var scoped = Truthy(r["model_scoped"]);
        if (fiveHour is null && sevenDay is null && !HasLength(scoped)) return null;
        var output = new JsonObject
        {
            ["updated_at"] = DiagnosticsLog.Stamp(now),
            ["five_hour"] = fiveHour?.DeepClone(),
            ["seven_day"] = sevenDay?.DeepClone(),
            ["model_scoped"] = scoped?.DeepClone(),
        };
        return output.ToJsonString();
    }

    // jq's `//` treats false like null
    private static JsonNode? Truthy(JsonNode? n) => n is null || Json.Bool(n) == false ? null : n;

    private static bool HasLength(JsonNode? n) => n switch
    {
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when Json.Str(v) is { } s => s.Length > 0,
        JsonValue v when Json.Num(v) is { } d => d != 0,
        _ => false,
    };

    /// Default runner. Bash-looking chains go to Git Bash; the rest to cmd.exe, Windows' own shell.
    public static string RunShell(string command, string stdin)
    {
        var bash = LooksLikeBash(command) ? FindBash() : null;
        var psi = bash is not null
            ? new ProcessStartInfo(bash) { ArgumentList = { "-c", command } }
            : new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" };
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.CreateNoWindow = true;
        using var process = Process.Start(psi);
        if (process is null) return "";
        var reading = process.StandardOutput.ReadToEndAsync();
        try
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch { }
        var output = reading.GetAwaiter().GetResult();
        process.WaitForExit();
        return output;
    }

    internal static bool LooksLikeBash(string command)
    {
        var t = command.TrimStart();
        string first;
        if (t.Length > 0 && (t[0] == '"' || t[0] == '\''))
        {
            var close = t.IndexOf(t[0], 1);
            first = close > 0 ? t[1..close] : t[1..];
        }
        else first = t.Split(' ', 2)[0];
        var name = first.Replace('\\', '/').Split('/').Last().ToLowerInvariant();
        return name is "bash" or "bash.exe" or "sh" or "sh.exe" || name.EndsWith(".sh");
    }

    private static string? FindBash()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "bash.exe");
                // System32's bash.exe is WSL, which would not see this user's Windows paths
                if (File.Exists(candidate) && !candidate.Contains("System32", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch { }
        }
        var git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(git) ? git : null;
    }
}
