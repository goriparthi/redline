using System.Text.Json;

namespace RedLine.Core;

/// <summary>
/// The answers a command that is on or off can give. Same vocabulary as a settings change,
/// because it is the same question asked of a different kind of setting.
/// </summary>
public enum ToggleOutcomeKind
{
    Status,
    Changed,
    Unchanged,
    Failed,
    /// <summary>The engine could not be asked, so <c>On</c> means nothing.</summary>
    Unavailable,
}

public sealed record ToggleOutcome
{
    public ToggleOutcomeKind Kind { get; init; }
    public string Key { get; init; } = "";
    public bool On { get; init; }

    /// <summary>What the platform calls the thing, such as "Run key". For saying where it went.</summary>
    public string Name { get; init; } = "";

    /// <summary>Something worth showing that is not a failure, like needing a new session.</summary>
    public string Detail { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>False when <c>On</c> is not an answer, only the absence of one.</summary>
    public bool Known => Kind is not (ToggleOutcomeKind.Unavailable or ToggleOutcomeKind.Failed);

    public bool Accepted =>
        Kind is ToggleOutcomeKind.Status or ToggleOutcomeKind.Changed
             or ToggleOutcomeKind.Unchanged;
}

public static class ToggleJson
{
    public static ToggleOutcome? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var kind = Text(root, "outcome") switch
            {
                "status" => ToggleOutcomeKind.Status,
                "changed" => ToggleOutcomeKind.Changed,
                "unchanged" => ToggleOutcomeKind.Unchanged,
                "failed" => ToggleOutcomeKind.Failed,
                _ => (ToggleOutcomeKind?)null,
            };
            if (kind is null) return null;

            return new ToggleOutcome
            {
                Kind = kind.Value,
                Key = Text(root, "key"),
                On = root.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                Name = Text(root, "name"),
                Detail = Text(root, "detail"),
                Message = Text(root, "message"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}

/// <summary>
/// The two settings that are commands rather than config: starting at login, and wiring
/// Claude Code's statusline. Both are the engine's to do on every platform, so the shell asks
/// rather than touching the registry or anyone's settings file itself.
/// </summary>
public sealed class ToggleStore(Engine? engine = null)
{
    private readonly Engine engine = engine ?? new Engine();

    /// <summary>The names the engine reports these by, so a caller can match one up.</summary>
    public const string AutostartKey = "autostart";
    public const string UsageFeedKey = "usageFeed";

    public ToggleOutcome Autostart() => Ask(AutostartKey, "autostart", "--json");

    /// <summary>
    /// Turning this on starts the app, not the watcher: the app runs a watcher of its own, and
    /// two would fight over the lock. An empty --args is how the engine is told "no arguments".
    /// </summary>
    public ToggleOutcome SetAutostart(bool on, string? program = null)
    {
        if (!on) return Ask(AutostartKey, "autostart", "off", "--json");
        var target = program ?? Environment.ProcessPath;
        return target is null
            ? Ask(AutostartKey, "autostart", "on", "--json")
            : Ask(AutostartKey, "autostart", "on", "--program", target, "--args", "", "--json");
    }

    public ToggleOutcome UsageFeed() => Ask(UsageFeedKey, "setup", "--json");

    public ToggleOutcome SetUsageFeed(bool on) =>
        on ? Ask(UsageFeedKey, "setup", "claude", "--json")
           : Ask(UsageFeedKey, "setup", "off", "--json");

    /// <summary>
    /// Status exits 20 when the feed is off, which is an answer and not a failure, so the
    /// exit code is not consulted at all: the reported outcome is.
    /// </summary>
    private ToggleOutcome Ask(string key, params string[] arguments)
    {
        var result = engine.Run(arguments);
        if (!result.Ran) return Unavailable(key, Problem(result));
        return ToggleJson.Parse(result.Output) ?? Unavailable(key, Problem(result));
    }

    private static ToggleOutcome Unavailable(string key, string message) =>
        new() { Kind = ToggleOutcomeKind.Unavailable, Key = key, Message = message };

    private static string Problem(EngineResult result)
    {
        var said = result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim();
        return said.Length > 0 ? said : "redline could not be run";
    }
}
