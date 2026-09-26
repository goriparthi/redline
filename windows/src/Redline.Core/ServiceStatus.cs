// Public status feeds for the hosted providers, in Statuspage's standard JSON shape. The fetch
// lives in the app (Core has no network); Ollama local is probed directly, Ollama Cloud has no feed.
using System.Text.Json.Nodes;

namespace Redline.Core;

/// One vocabulary for provider health wherever it is drawn; surfaces pick a colour per tone.
public static class ServiceGlyph
{
    public enum Tone { Healthy, Warning, Critical, Unknown }

    /// Interim state while a fetch is in flight. Not an indicator any status page reports.
    public const string Checking = "checking";

    /// SF Symbol names, kept as the shared vocabulary; the Windows app maps them to its glyphs.
    public static string Symbol(string indicator) => indicator switch
    {
        "none" or "local" => "checkmark.circle.fill",
        "minor" => "exclamationmark.triangle.fill",
        "major" or "critical" => "exclamationmark.octagon.fill",
        "local-down" => "bolt.slash.circle.fill",
        Checking => "ellipsis.circle.fill",
        _ => "questionmark.circle.fill",
    };

    public static Tone ToneFor(string indicator) => indicator switch
    {
        "none" or "local" => Tone.Healthy,
        // A local server that is not answering is a real degradation, not an unknown
        "minor" or "local-down" => Tone.Warning,
        "major" or "critical" => Tone.Critical,
        _ => Tone.Unknown,
    };
}

public static class ServiceStatus
{
    public const string ClaudeUrl = "https://status.claude.com/api/v2/status.json";
    public const string CodexUrl = "https://status.openai.com/api/v2/status.json";

    /// Indicator is none | minor | major | critical.
    public sealed record Report(string Indicator, string Description, DateTimeOffset At)
    {
        public Report(string indicator, string description) : this(indicator, description, DateTimeOffset.UtcNow) { }

        public bool IsOperational => Indicator == "none";

        /// Calm and factual, per the brand: report what the operator reports, never alarm
        public string Phrase => Indicator switch
        {
            "none" => "service ok",
            "minor" => "minor incident reported",
            "major" or "critical" => "outage reported",
            _ => "status unknown",
        };
    }

    /// Parses Statuspage's status.json. Separated from the fetch so it is testable.
    public static Report? Parse(string text)
    {
        if (Json.ParseObject(text) is not { } json || json["status"] is not JsonObject status ||
            Json.Str(status["indicator"]) is not { } indicator) return null;
        return new Report(indicator, Json.Str(status["description"]) ?? "");
    }

    public static Report? Parse(byte[] data) => Parse(System.Text.Encoding.UTF8.GetString(data));
}
