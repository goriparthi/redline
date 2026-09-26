// Loose accessors over System.Text.Json nodes. Every format read here is undocumented, so a
// wrong type reads as absent rather than throwing.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Redline.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    public static JsonObject? ParseObject(string text) => Parse(text) as JsonObject;

    public static JsonObject? ReadObject(string path)
    {
        try { return File.Exists(path) ? ParseObject(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        // A node built in memory (not parsed) only converts to its own CLR type
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<ulong>(out var u)) return u;
        return null;
    }

    /// Integral numbers only, mirroring Swift's `as? Int` on a JSON number.
    public static int? Int(JsonNode? n)
    {
        var d = Num(n);
        if (d is null || d != Math.Floor(d.Value) || double.IsInfinity(d.Value)) return null;
        return d is >= int.MinValue and <= int.MaxValue ? (int)d.Value : null;
    }

    public static long? Long(JsonNode? n)
    {
        var d = Num(n);
        if (d is null || d != Math.Floor(d.Value)) return null;
        return (long)d.Value;
    }

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool? Bool(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static JsonObject? Obj(JsonNode? n) => n as JsonObject;
    public static JsonArray? Arr(JsonNode? n) => n as JsonArray;

    public static List<string>? Strings(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var list = new List<string>();
        foreach (var x in a)
        {
            var s = Str(x);
            if (s is null) return null;
            list.Add(s);
        }
        return list;
    }

    /// Writes through a temp file and an atomic replace, so a crash never leaves half a file.
    public static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
