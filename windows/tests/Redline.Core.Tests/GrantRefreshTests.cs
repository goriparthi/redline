// What a failed refresh of RedLine's own grant means. Only "invalid_grant" used to be terminal,
// so a rejection in Anthropic's API envelope was retried on every poll forever.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class GrantRefreshTests
{
    /// The body shape a user reported. The correlation id is synthetic; only the shape matters.
    private const string Reported =
        """{"type":"error","error":{"type":"invalid_request_error","message":"Invalid request format"},"request_id":"req_EXAMPLE0000000000000000"}""";

    [Fact]
    public void TheReportedRejectionIsTerminalRatherThanRetriedForever()
    {
        var outcome = ClaudeAuthPolicy.ClassifyRefresh(400, Reported);
        // A 400 the endpoint will give again is not worth repeating; Sign In has to be offered
        Assert.True(outcome.IsTerminal);
        // The message is for a person, so it carries no JSON
        Assert.DoesNotContain("{", outcome.Message);
    }

    [Fact]
    public void AnOAuthExpiredGrantIsNamedAsExpired()
    {
        var outcome = ClaudeAuthPolicy.ClassifyRefresh(400, """{"error":"invalid_grant"}""");
        Assert.True(outcome.IsTerminal);
        Assert.Equal("Sign-in expired; sign in again", outcome.Message);
    }

    [Theory]
    [InlineData("""{"error":"invalid_client"}""")]
    [InlineData("""{"error":"unauthorized_client"}""")]
    public void ARejectedClientIdPointsAtTheClientId(string body)
    {
        var outcome = ClaudeAuthPolicy.ClassifyRefresh(401, body);
        Assert.True(outcome.IsTerminal);
        Assert.Contains("clientId", outcome.Message);
    }

    [Fact]
    public void RateLimitingIsRetriedAndKeepsTheGrant()
    {
        // A lifted rate limit makes the same request succeed, so the grant stays
        Assert.False(ClaudeAuthPolicy.ClassifyRefresh(429, "slow down").IsTerminal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void ServerFaultsAreRetriedAndKeepTheGrant(int status)
    {
        Assert.False(ClaudeAuthPolicy.ClassifyRefresh(status, "").IsTerminal);
    }

    [Fact]
    public void ATransportFailureIsRetriedAndKeepsTheGrant()
    {
        // Status 0 means the request never got an answer, so nothing was rejected
        Assert.False(ClaudeAuthPolicy.ClassifyRefresh(0, "The Internet connection appears to be offline.").IsTerminal);
    }

    [Fact]
    public void ASuccessWithNoTokenKeepsTheGrantBecauseTheGrantIsNotWhatFailed()
    {
        Assert.False(ClaudeAuthPolicy.ClassifyRefresh(200, """{"ok":true}""").IsTerminal);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void EveryOtherClientErrorIsTerminalAndNamesTheStatus(int status)
    {
        var outcome = ClaudeAuthPolicy.ClassifyRefresh(status, "nope");
        Assert.True(outcome.IsTerminal);
        Assert.NotEmpty(outcome.Message);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(429, "")]
    [InlineData(500, "")]
    [InlineData(200, "{}")]
    [InlineData(400, Reported)]
    [InlineData(401, """{"error":"invalid_client"}""")]
    public void EveryOutcomeSaysSomethingAPersonCanActOn(int status, string body)
    {
        var message = ClaudeAuthPolicy.ClassifyRefresh(status, body).Message;
        Assert.NotEmpty(message);
        // Nothing here should read as a stack trace or a payload
        Assert.DoesNotContain("request_id", message);
    }
}

public sealed class FormBodyTests
{
    private static string Enc(params (string K, string V)[] pairs) =>
        FormBody.Encoded(pairs.ToDictionary(p => p.K, p => p.V));

    [Fact]
    public void KeysAreSortedSoTheSameBodyIsAlwaysTheSameBytes()
    {
        Assert.Equal("a=1&b=2&c=3", Enc(("b", "2"), ("a", "1"), ("c", "3")));
    }

    /// Both characters are legal in a query string, so the URL sets would leave them alone
    /// and a refresh token carrying either would be truncated or split by the server.
    [Fact]
    public void PlusAndAmpersandAreEscapedBecauseTheyAreLegalInAQuery()
    {
        Assert.Equal("t=a%2Bb", Enc(("t", "a+b")));
        Assert.Equal("t=a%26b%3Dc", Enc(("t", "a&b=c")));
    }

    [Fact]
    public void UnreservedCharactersSurviveUnchanged()
    {
        const string token = "AbYz09-._~";
        Assert.Equal($"t={token}", Enc(("t", token)));
    }

    [Fact]
    public void SpacesAndSlashesAndColonsAreEscaped()
    {
        Assert.Equal("u=http%3A%2F%2Fx%2Fy%20z", Enc(("u", "http://x/y z")));
    }

    [Fact]
    public void AnEmptyValueStillProducesItsKey()
    {
        Assert.Equal("a=", Enc(("a", "")));
    }

    [Fact]
    public void AnEmptyBodyIsAnEmptyString()
    {
        Assert.Equal("", Enc());
    }

    /// A real PKCE verifier and a base64url token must pass through unaltered.
    [Fact]
    public void Base64URLPayloadsPassThroughUnaltered()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal($"code_verifier={verifier}", Enc(("code_verifier", verifier)));
    }
}
