// Decisions about borrowed Claude credentials that need neither the credential store nor the
// network, so they can be tested directly. The app layer performs the reads; this says what they mean.
using System.Globalization;
using System.Text;

namespace Redline.Core;

/// Why a credential read produced nothing. A locked store must retry, not latch like a signed-out CLI.
public abstract record CredentialOutcome
{
    public sealed record Found(BorrowedCredential Credential) : CredentialOutcome;
    /// The item is genuinely absent. Claude Code is signed out; only the user can fix it.
    public sealed record NotFound : CredentialOutcome;
    /// The item exists but the system would not hand it over. Transient, so retried, not latched.
    public sealed record AccessDenied : CredentialOutcome;
    /// Read succeeded but the payload was not a credential we understand.
    public sealed record Unreadable : CredentialOutcome;

    public BorrowedCredential? CredentialOrNull => this is Found f ? f.Credential : null;

    /// Only a signed-out CLI is durable news; the rest resolve on the next pass.
    public bool IsTerminal => this is NotFound;
}

/// What a failed refresh of RedLine's own grant means: terminal clears it, retryable keeps it.
public abstract record GrantRefreshOutcome(string Message)
{
    /// The grant is dead. Clear it, and say so once.
    public sealed record Terminal(string Message) : GrantRefreshOutcome(Message);
    /// Try again on the next poll.
    public sealed record Retryable(string Message) : GrantRefreshOutcome(Message);

    public bool IsTerminal => this is Terminal;
}

public static class ClaudeAuthPolicy
{
    /// Exponential backoff in seconds, capped at 30 minutes like the usage endpoint's own.
    public static double Backoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return 0;
        return Math.Min(300 * Math.Pow(2, consecutiveFailures - 1), 1800);
    }

    /// Only a lifted rate limit, a recovered server or a working network can make an identical
    /// request succeed later; any other 4xx rejects the request or the grant for good.
    public static GrantRefreshOutcome ClassifyRefresh(int status, string body)
    {
        var lower = body.ToLowerInvariant();
        // Transport failure: no status, so nothing was rejected
        if (status == 0) return new GrantRefreshOutcome.Retryable("Could not reach the sign-in service; retrying");
        if (status == 429) return new GrantRefreshOutcome.Retryable("Rate limited while renewing the sign-in; retrying");
        if (status >= 500) return new GrantRefreshOutcome.Retryable("The sign-in service is failing; retrying");
        // A 2xx with no token is a shape we do not understand, but the grant may well be fine
        if (status is >= 200 and < 300)
            return new GrantRefreshOutcome.Retryable("The sign-in service returned no token; retrying");
        // Named OAuth causes first, so the message can say which one it was
        if (lower.Contains("invalid_grant")) return new GrantRefreshOutcome.Terminal("Sign-in expired; sign in again");
        if (lower.Contains("invalid_client") || lower.Contains("unauthorized_client"))
            return new GrantRefreshOutcome.Terminal("This build's OAuth client id was rejected; check oauth.clientId");
        return new GrantRefreshOutcome.Terminal(
            $"Sign-in could not be renewed (HTTP {status.ToString(CultureInfo.InvariantCulture)}); sign in again");
    }
}

/// RFC 6749 form bodies. Only the RFC 3986 unreserved set passes, so `+` and `&` in a token survive.
public static class FormBody
{
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    // Sorted so the same body always produces the same bytes, which makes a failure reproducible
    public static string Encoded(IReadOnlyDictionary<string, string> body) =>
        string.Join("&", body.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{Escape(k)}={Escape(body[k] ?? "")}"));

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b < 128 && Unreserved.Contains((char)b)) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
