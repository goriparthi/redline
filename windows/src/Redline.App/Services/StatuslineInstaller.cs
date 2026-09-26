// Installs the Claude usage feed: the ~/.claude/settings.json statusLine entry that runs
// `redlinectl statusline`. An existing statusline command is carried forward, never discarded.
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.App.Services;

/// The wrapper .cmd is the marker of consent, as the .sh is on macOS. The chained command sits
/// in a sidecar file the wrapper loads into REDLINE_STATUSLINE_CHAIN, so no cmd quoting touches it.
public static class StatuslineInstaller
{
    public const string Marker = "RedLine Claude usage feed";
    public const string ScriptName = "claude-statusline.cmd";
    public const string ChainName = "claude-statusline.chain";
    public const string CtlName = "redlinectl.exe";

    public abstract record Result
    {
        public sealed record Installed(string Script, string? Chained) : Result;
        public sealed record AlreadyInstalled(string Script) : Result;
        public sealed record Failed(string Message) : Result;
    }

    public static string SettingsPath(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".claude/settings.json");

    public static string ScriptPath(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".local/share/redline/" + ScriptName);

    public static string ChainPath(string? home = null) =>
        RedlineHome.Join(home ?? RedlineHome.Url, ".local/share/redline/" + ChainName);

    /// redlinectl.exe ships beside RedLine.exe.
    public static string DefaultCtlPath => Path.Combine(AppContext.BaseDirectory, CtlName);

    /// The feed as a standing choice: the wrapper exists only while it is wanted.
    public static bool IsWanted(string? home = null) => File.Exists(ScriptPath(home));

    /// True when settings.json already runs our feed, whatever else has changed around it.
    public static bool IsInstalled(string? home = null) =>
        ReadSettings(home).Settings is { } s && CommandOf(s) is { } command && IsOurs(command);

    public static Result Install(string? home = null, string? ctlPath = null)
    {
        var ctl = ctlPath ?? DefaultCtlPath;
        if (!File.Exists(ctl))
            return new Result.Failed($"{CtlName} is not in this build. Reinstall RedLine, or build Redline.Cli beside it.");

        var (settings, broken) = ReadSettings(home);
        // A settings.json that will not parse is still the user's; overwriting it would lose it all
        if (broken)
            return new Result.Failed($"{SettingsPath(home)} is not valid JSON, so it was left untouched. Fix it and try again.");
        settings ??= new JsonObject();

        var existing = CommandOf(settings);
        var ours = existing is not null && IsOurs(existing);
        // Re-running must not chain the feed to itself, which would recurse on every draw
        string? chained = existing is null ? null
            : ours ? (IsWrapperForm(existing) ? ChainedCommand(home) : null)
            : existing;
        if (chained is not null && chained.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return new Result.Failed("The statusline command already configured spans several lines, which the "
                                     + "Windows feed cannot carry. Put it in a script and point statusLine at that.");

        var script = ScriptPath(home);
        try
        {
            // Replace our own files freely; this doubles as the update path for a moved install
            if (chained is not null) Json.WriteAtomic(ChainPath(home), chained);
            else if (File.Exists(ChainPath(home))) File.Delete(ChainPath(home));
            Json.WriteAtomic(script, WrapperScript(ctl));
        }
        catch (Exception e)
        {
            return new Result.Failed($"Could not write {script}\n\n{e.Message}");
        }

        var command = chained is null ? Quote(ctl) + " statusline" : Quote(script);
        if (ours && existing == command) return new Result.AlreadyInstalled(script);

        var line = settings["statusLine"] as JsonObject ?? new JsonObject();
        line["type"] = "command";
        line["command"] = command;
        settings["statusLine"] = line;
        if (!WriteSettings(settings, home)) return new Result.Failed($"Could not write {SettingsPath(home)}");
        return new Result.Installed(script, chained);
    }

    /// Re-wires the feed when something else unwired it. Sessions that predate the install
    /// write their in-memory settings back; the wrapper on disk says the feed is still wanted.
    public static bool RepairIfNeeded(string? home = null, string? ctlPath = null)
    {
        if (!File.Exists(ScriptPath(home)) || IsInstalled(home)) return false;
        return Install(home, ctlPath) is Result.Installed;
    }

    /// Puts settings.json back the way the feed found it. Only touches an entry that runs our
    /// feed; must run before the wrapper or redlinectl.exe are removed.
    public static bool Uninstall(string? home = null)
    {
        if (ReadSettings(home).Settings is not { } settings || settings["statusLine"] is not JsonObject line ||
            Json.Str(line["command"]) is not { } command || !IsOurs(command)) return false;

        if (IsWrapperForm(command) && ChainedCommand(home) is { } chained)
        {
            line["command"] = chained;
        }
        else
        {
            // Nothing was there before the feed, so leave nothing behind
            settings.Remove("statusLine");
        }
        return WriteSettings(settings, home);
    }

    /// Removes the wrapper and its chain file: the end of consent, and with it the repair.
    public static void RemoveScript(string? home = null)
    {
        foreach (var path in new[] { ScriptPath(home), ChainPath(home) })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// The command preserved behind the feed, or null when none was.
    public static string? ChainedCommand(string? home = null)
    {
        try
        {
            var path = ChainPath(home);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).TrimEnd('\r', '\n');
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    internal static bool IsOurs(string command) =>
        IsWrapperForm(command) ||
        (command.Contains("redlinectl", StringComparison.OrdinalIgnoreCase) &&
         command.TrimEnd().EndsWith(" statusline", StringComparison.OrdinalIgnoreCase));

    internal static bool IsWrapperForm(string command) =>
        command.Contains(ScriptName, StringComparison.OrdinalIgnoreCase);

    /// `set /p` reads the chain literally, so quotes, pipes and percent signs in it survive.
    internal static string WrapperScript(string ctlPath) =>
        "@echo off\r\n" +
        $"rem {Marker}. Written by Set Up Claude Tracking; see SECURITY.md.\r\n" +
        "set \"REDLINE_STATUSLINE_CHAIN=\"\r\n" +
        $"if exist \"%~dp0{ChainName}\" set /p REDLINE_STATUSLINE_CHAIN=<\"%~dp0{ChainName}\"\r\n" +
        $"\"{ctlPath.Replace("%", "%%")}\" statusline\r\n";

    private static string Quote(string path) => "\"" + path + "\"";

    private static string? CommandOf(JsonObject settings) =>
        settings["statusLine"] is JsonObject line ? Json.Str(line["command"]) : null;

    /// Settings null and broken false means the file is simply absent.
    private static (JsonObject? Settings, bool Broken) ReadSettings(string? home)
    {
        var path = SettingsPath(home);
        if (!File.Exists(path)) return (null, false);
        try
        {
            var text = File.ReadAllText(path);
            if (text.Trim().Length == 0) return (null, false);
            return JsonNode.Parse(text) is JsonObject o ? (o, false) : (null, true);
        }
        catch { return (null, true); }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Through a temporary file and an atomic replace: a half-written settings.json would break
    /// every Claude Code session on this machine.
    private static bool WriteSettings(JsonObject settings, string? home)
    {
        var path = SettingsPath(home);
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".redline.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, Sorted(settings)!.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            Diag.Log.Error("feed.settings_write_failed", "could not write settings.json",
                new() { ["path"] = path, ["error"] = e.Message });
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    // Sorted keys like the macOS writer, so both platforms produce the same file
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node!.DeepClone(),
    };
}
