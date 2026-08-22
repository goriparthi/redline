using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedLine.Core;

/// <summary>
/// What control a setting needs. The engine names these and this only reads the name: a shell
/// that decided for itself what a value may be would eventually disagree with the engine, and
/// then two validators would both be sure.
/// </summary>
public enum SettingKind
{
    Bool,
    Number,
    Choice,
    List,
    /// <summary>A kind this build has not heard of. Shown as text rather than dropped, so a
    /// newer engine gains a setting here instead of quietly losing one.</summary>
    Unknown,
}

/// <summary>
/// One setting as `redline config --json` publishes it. Field names are the contract; see
/// SettingsContractTests, which exists in both languages against the same fixture.
/// </summary>
public sealed record SettingDefinition
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";

    /// <summary>The current value, as the engine renders it. Always a string, whatever
    /// the kind.</summary>
    [JsonPropertyName("value")] public string Value { get; init; } = "";

    [JsonPropertyName("kind")] public string KindName { get; init; } = "";
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("allowed")] public IReadOnlyList<string> Allowed { get; init; } = [];

    public SettingKind Kind => KindName switch
    {
        "bool" => SettingKind.Bool,
        "number" => SettingKind.Number,
        "choice" => SettingKind.Choice,
        "list" => SettingKind.List,
        _ => SettingKind.Unknown,
    };

    public bool IsOn => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value as a number, or null when it is not one. Invariant on purpose: the
    /// engine writes 0.5 with a point and a comma locale would read it as five.</summary>
    public double? Number =>
        double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>Which of <see cref="Allowed"/> are on, for a list.</summary>
    public IReadOnlyList<string> Selected =>
        Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A number in the shape the engine writes one, so a control that hands back 70 does not
    /// try to store "70.00" and read back something that never matches.
    /// </summary>
    public static string Format(double value) =>
        value == Math.Round(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// What the engine did with a change. Reported rather than inferred from an exit code, because
/// "already that" and "refused" are different answers and only one of them is worth a message.
/// </summary>
public enum SettingsOutcomeKind
{
    Read,
    Changed,
    Unchanged,
    Rejected,
    UnknownKey,
    Failed,
    /// <summary>The engine could not be asked at all. Not one of its answers.</summary>
    Unavailable,
}

public sealed record SettingsOutcome
{
    public SettingsOutcomeKind Kind { get; init; }
    public string Key { get; init; } = "";

    /// <summary>The value now in force: what was read, what it already was, or what it
    /// became.</summary>
    public string Value { get; init; } = "";

    public string Previous { get; init; } = "";

    /// <summary>The engine's own words for what the setting accepts, when it refused one.</summary>
    public string Expected { get; init; } = "";

    /// <summary>What to show a person. Empty when nothing went wrong.</summary>
    public string Message { get; init; } = "";

    /// <summary>True when the file now holds what was asked for, including when it
    /// already did.</summary>
    public bool Accepted =>
        Kind is SettingsOutcomeKind.Read or SettingsOutcomeKind.Changed
             or SettingsOutcomeKind.Unchanged;
}

public static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record Catalog
    {
        [JsonPropertyName("settings")]
        public IReadOnlyList<SettingDefinition> Settings { get; init; } = [];
    }

    /// <summary>Null for anything unreadable, so a caller can say so rather than show an
    /// empty settings page that looks like an app with no settings.</summary>
    public static IReadOnlyList<SettingDefinition>? ParseCatalog(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Catalog>(json, Options);
            return parsed?.Settings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static SettingsOutcome? ParseOutcome(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var key = Text(root, "key");
            return Text(root, "outcome") switch
            {
                "read" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.Read, Key = key, Value = Text(root, "value"),
                },
                "changed" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.Changed, Key = key,
                    Previous = Text(root, "from"), Value = Text(root, "to"),
                },
                "unchanged" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.Unchanged, Key = key, Value = Text(root, "value"),
                },
                // The engine's wording for what it accepts, never a rule restated here
                "rejected" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.Rejected, Key = key,
                    Expected = Text(root, "expected"),
                    Message = $"{key} needs {Text(root, "expected")}",
                },
                "unknownKey" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.UnknownKey, Key = key,
                    Message = $"no such setting: {key}",
                },
                "failed" => new SettingsOutcome
                {
                    Kind = SettingsOutcomeKind.Failed, Key = key,
                    Message = Text(root, "message"),
                },
                _ => null,
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
/// Everything and nothing: the settings the engine offers, or the reason there are none to show.
/// </summary>
public sealed record SettingsCatalog(IReadOnlyList<SettingDefinition> Settings, string Problem = "")
{
    public bool Available => Problem.Length == 0;

    public SettingDefinition? Find(string key) =>
        Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Settings, through the engine. Nothing here knows a key by name, what it defaults to or what
/// it will accept: the engine publishes all of that and refuses anything it would not load, so
/// there is only ever one set of rules.
/// </summary>
public sealed class SettingsStore(Engine? engine = null)
{
    private readonly Engine engine = engine ?? new Engine();

    public SettingsCatalog Read()
    {
        var result = engine.Run("config", "--json");
        if (!result.Ran) return new SettingsCatalog([], Problem(result));

        var parsed = SettingsJson.ParseCatalog(result.Output);
        return parsed is null
            ? new SettingsCatalog([], "redline did not report its settings in a readable shape")
            : new SettingsCatalog(parsed);
    }

    public SettingsOutcome Write(string key, string value)
    {
        var result = engine.Run("config", key, value, "--json");
        if (!result.Ran)
        {
            return new SettingsOutcome
            {
                Kind = SettingsOutcomeKind.Unavailable, Key = key, Message = Problem(result),
            };
        }

        // The outcome is on stdout even when it reports a refusal, so there is one stream to
        // read. An exit code alone could not tell a refusal from a write that failed.
        return SettingsJson.ParseOutcome(result.Output) ?? new SettingsOutcome
        {
            Kind = SettingsOutcomeKind.Unavailable, Key = key, Message = Problem(result),
        };
    }

    public SettingsOutcome Write(string key, bool on) => Write(key, on ? "true" : "false");

    public SettingsOutcome Write(string key, double value) =>
        Write(key, SettingDefinition.Format(value));

    public SettingsOutcome Write(string key, IEnumerable<string> values) =>
        Write(key, string.Join(",", values));

    /// <summary>Whatever the engine said about not running, rather than a sentence made
    /// up here.</summary>
    private static string Problem(EngineResult result)
    {
        var said = result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim();
        return said.Length > 0 ? said : "redline could not be run";
    }
}
