using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedLine.Core;

/// <summary>
/// The wire format the Swift engine publishes. Field names match what it writes, because the
/// two are one contract and a rename on either side is a break.
/// </summary>
public sealed record Snapshot
{
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("limits")] public IReadOnlyList<LimitWindow> Limits { get; init; } = [];
    [JsonPropertyName("today")] public Totals? Today { get; init; }
    [JsonPropertyName("week")] public Totals? Week { get; init; }

    [JsonPropertyName("todayByProvider")]
    public IReadOnlyDictionary<string, Totals> TodayByProvider { get; init; }
        = new Dictionary<string, Totals>();

    [JsonPropertyName("weekByProvider")]
    public IReadOnlyDictionary<string, Totals> WeekByProvider { get; init; }
        = new Dictionary<string, Totals>();

    /// <summary>When the Claude figures were last true, which is not when this was written.</summary>
    [JsonPropertyName("claudeLimitsAsOf")] public DateTimeOffset? ClaudeLimitsAsOf { get; init; }

    [JsonPropertyName("ollama")] public OllamaSection? Ollama { get; init; }

    /// <summary>
    /// The window nearest its limit, which is the one a tray icon should be showing.
    /// </summary>
    public LimitWindow? Worst =>
        Limits.Count == 0 ? null : Limits.MaxBy(w => w.Utilization);

    /// <summary>
    /// How old this reading is. A stale snapshot has to be drawn as stale rather than as
    /// fact, so the caller is given the age rather than a boolean someone else chose.
    /// </summary>
    public TimeSpan AgeAt(DateTimeOffset now) => now - UpdatedAt;
}

public sealed record LimitWindow
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("utilization")] public double Utilization { get; init; }
    [JsonPropertyName("resetsAt")] public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>The same wording the macOS app uses, so the two do not name one window twice.</summary>
    public string DisplayName => Key switch
    {
        "five_hour" => "Session · 5h",
        "seven_day" => Provider == "Claude" ? "Week · all models" : "Week",
        "seven_day_opus" => "Week · Opus",
        "seven_day_sonnet" => "Week · Sonnet",
        _ => Key.Replace('_', ' '),
    };
}

public sealed record Totals
{
    /// <summary>Input plus output tokens.</summary>
    [JsonPropertyName("io")] public long Io { get; init; }
    [JsonPropertyName("cost")] public double Cost { get; init; }

    /// <summary>
    /// True when something in the total had no price. The figure is then a floor rather than
    /// an answer, and must never be shown as though it were exact.
    /// </summary>
    [JsonPropertyName("hasUnpriced")] public bool HasUnpriced { get; init; }
}

public sealed record OllamaSection
{
    [JsonPropertyName("reachable")] public bool Reachable { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("running")] public IReadOnlyList<OllamaModel> Running { get; init; } = [];
    [JsonPropertyName("downloadedCount")] public int DownloadedCount { get; init; }
}

public sealed record OllamaModel
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("vramShare")] public double VramShare { get; init; }
}

public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Returns null for anything unreadable. A missing or half-written snapshot is
    /// an ordinary state, not an exception worth throwing at a UI thread.</summary>
    public static Snapshot? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Snapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
