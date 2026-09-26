// Pure JSON scanning for a borrowed CLI credential, kept apart from credential-store access so
// the undocumented shape can be unit tested without touching real secrets.
using System.Text;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// Expiry is carried rather than applied: a stale token means "needs refresh", not "signed out".
public sealed record BorrowedCredential(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    /// A missing expiry is non-expiring; the margin in seconds covers clock skew and the round trip.
    public bool IsFresh(DateTimeOffset? now = null, double margin = 60)
    {
        if (ExpiresAt is not { } e) return true;
        return e > (now ?? DateTimeOffset.UtcNow).AddSeconds(margin);
    }

    public bool CanRefresh => (RefreshToken?.Trim(' ', '\t', ' ') ?? "").Length > 0;
}

public static class CredentialScan
{
    // Scans for an accessToken/expiresAt pair rather than a key path a CLI update could move.
    // Null when the token is absent, empty, or already expired.
    public static string? AccessToken(JsonObject json, DateTimeOffset? now = null, double margin = 60) =>
        Credential(json) is { } c && c.IsFresh(now, margin) ? c.AccessToken : null;

    /// The whole credential, expired or not. The refresh path needs the stale one.
    public static BorrowedCredential? Credential(JsonObject json) => Find(json, 0);

    private static BorrowedCredential? Find(JsonObject dict, int depth)
    {
        if (Json.Str(Value(dict, "accesstoken")) is { Length: > 0 } token)
            return new BorrowedCredential(token, Json.Str(Value(dict, "refreshtoken")), Expiry(dict));
        if (depth >= 3) return null;
        // Sorted keys so a nested match is deterministic rather than dictionary-order dependent
        foreach (var (_, v) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (v is JsonObject d && Find(d, depth + 1) is { } c) return c;
        return null;
    }

    private static JsonNode? Value(JsonObject dict, string name) =>
        dict.FirstOrDefault(kv => kv.Key.ToLowerInvariant() == name).Value;

    // expiresAt is epoch milliseconds
    private static DateTimeOffset? Expiry(JsonObject dict) =>
        Json.Num(Value(dict, "expiresat")) is { } ms && ms > 0
            ? DateTimeOffset.UnixEpoch.AddTicks((long)(ms * TimeSpan.TicksPerMillisecond)) : null;
}

/// macOS `security find-generic-password -w` hex-dumps a payload it cannot return as a C-string.
/// Real credential JSON always holds '{' and '"', so an all-hex even-length payload is a dump.
public static class SecurityCLIOutput
{
    public static string Decode(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 2 || trimmed.Length % 2 != 0 || !trimmed.All(char.IsAsciiHexDigit)) return raw;
        try { return Encoding.UTF8.GetString(Convert.FromHexString(trimmed)); }
        catch { return raw; }
    }
}
