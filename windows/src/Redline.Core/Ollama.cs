// Parsing for the Ollama HTTP API. Network calls live in the app; only the shapes are here so
// they can be tested without a running server.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed record OllamaModel(string Name, long SizeBytes, string? ParameterSize,
                                 string? Quantization, DateTimeOffset? ModifiedAt)
{
    public string Id => Name;

    /// Family without the tag, e.g. "qwen3-coder" from "qwen3-coder:30b"
    public string Family => Name.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Name;

    public string? Tag
    {
        get
        {
            var parts = Name.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1] : null;
        }
    }
}

public sealed record OllamaRunningModel(string Name, long SizeBytes, long SizeVRAM, DateTimeOffset? ExpiresAt)
{
    public string Id => Name;

    /// Share of the loaded weights held in VRAM rather than system memory
    public double VramShare => SizeBytes > 0 ? Math.Min(1, (double)SizeVRAM / SizeBytes) : 0;
}

public static class OllamaParse
{
    internal static DateTimeOffset? Date(JsonNode? v) => TranscriptTail.ParseTimestamp(Json.Str(v));

    internal static long Int64(JsonNode? v) => Json.Num(v) is { } d ? (long)d : 0;

    /// Every element must be an object, mirroring Swift's `as? [[String: Any]]`.
    private static List<JsonObject>? Objects(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var list = new List<JsonObject>();
        foreach (var x in a)
        {
            if (x is not JsonObject o) return null;
            list.Add(o);
        }
        return list;
    }

    private static string? NameOf(JsonObject m) =>
        (Json.Str(m["name"]) ?? Json.Str(m["model"])) is { Length: > 0 } s ? s : null;

    /// GET /api/tags
    public static List<OllamaModel> Models(JsonObject json)
    {
        if (Objects(json["models"]) is not { } list) return new();
        return list.Select(m =>
            {
                if (NameOf(m) is not { } name) return null;
                var details = m["details"] as JsonObject;
                return new OllamaModel(name, Int64(m["size"]),
                                       Json.Str(details?["parameter_size"]),
                                       Json.Str(details?["quantization_level"]),
                                       Date(m["modified_at"]));
            })
            .OfType<OllamaModel>()
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// GET /api/ps
    public static List<OllamaRunningModel> Running(JsonObject json)
    {
        if (Objects(json["models"]) is not { } list) return new();
        return list.Select(m => NameOf(m) is { } name
                ? new OllamaRunningModel(name, Int64(m["size"]), Int64(m["size_vram"]), Date(m["expires_at"]))
                : null)
            .OfType<OllamaRunningModel>()
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }
}

public static class Ollama
{
    public static string FmtBytes(long n)
    {
        double d = n;
        var c = CultureInfo.InvariantCulture;
        if (d >= 1_000_000_000) return (d / 1_000_000_000).ToString("0.0", c) + " GB";
        if (d >= 1_000_000) return (d / 1_000_000).ToString("0", c) + " MB";
        if (d >= 1_000) return (d / 1_000).ToString("0", c) + " KB";
        return n.ToString(c) + " B";
    }
}
