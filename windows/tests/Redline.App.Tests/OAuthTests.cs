using System.Net;
using System.Net.Sockets;
using System.Text;
using Redline.App.Services;
using Redline.Core;

namespace Redline.App.Tests;

public class PkceTests
{
    [Fact]
    public void ChallengeMatchesTheRfc7636Vector() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                     Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void RandomValuesAreUrlSafeAndUnpadded()
    {
        var v = Pkce.RandomUrlSafe(64);
        Assert.Equal(86, v.Length);
        Assert.All(v, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(v, Pkce.RandomUrlSafe(64));
    }

    [Fact]
    public void StateMustMatchExactly()
    {
        Assert.True(Pkce.StateMatches("abc", "abc"));
        Assert.False(Pkce.StateMatches("abc", "abd"));
        Assert.False(Pkce.StateMatches("abc", "ab"));
        Assert.False(Pkce.StateMatches("abc", null));
        Assert.False(Pkce.StateMatches("", ""));
    }

    [Fact]
    public void ParsesTheCallbackRequestLine()
    {
        var (code, state) = Pkce.ParseCallback("GET /callback?code=a%2Bb+c&state=xyz HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Assert.Equal("a+b+c", code);
        Assert.Equal("xyz", state);
        Assert.Equal((null, null), Pkce.ParseCallback("GET /favicon.ico HTTP/1.1\r\n"));
        Assert.Equal((null, null), Pkce.ParseCallback("garbage"));
    }

    [Fact]
    public void AuthorizeUrlCarriesPkceAndRefusesPlainHttp()
    {
        var settings = new OAuthSettings { ClientId = "client 1", RedirectPort = 5555 };
        var url = Pkce.AuthorizeUrl(settings, "chal", "st")!;
        Assert.StartsWith("https://claude.ai/oauth/authorize?", url);
        Assert.Contains("client_id=client%201", url);
        Assert.Contains("code_challenge=chal", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=st", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://localhost:5555/callback"), url);
        Assert.Contains("scope=user%3Aprofile", url);

        settings.AuthorizeUrl = "http://claude.ai/oauth/authorize";
        Assert.Null(Pkce.AuthorizeUrl(settings, "chal", "st"));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task CallbackServerAnswersOnLoopbackAndWaitsForACode()
    {
        var port = FreePort();
        var server = new CallbackServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = server.Start(port, cts.Token);

        async Task<string> Send(string path)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var s = client.GetStream();
            await s.WriteAsync(Encoding.UTF8.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
            using var reader = new StreamReader(s);
            return await reader.ReadToEndAsync();
        }

        Assert.Contains("No authorization code", await Send("/favicon.ico"));
        Assert.False(waiting.IsCompleted);
        Assert.Contains("Signed in", await Send("/callback?code=c0de&state=s7"));
        var (code, state) = await waiting;
        Assert.Equal("c0de", code);
        Assert.Equal("s7", state);
    }

    [Fact]
    public async Task CallbackServerStopsOnCancel()
    {
        var server = new CallbackServer();
        using var cts = new CancellationTokenSource();
        var waiting = server.Start(FreePort(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

public class TokenStoreTests
{
    [Fact]
    public void RoundTripsThroughTheStoreUnderTheRedlineTarget()
    {
        var store = new MemoryCredentialStore();
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.True(new TokenStore("acc", "ref", at).Save(store));
        Assert.Equal(CredentialReadStatus.Found, store.Read("redline").Status);
        Assert.Equal(new TokenStore("acc", "ref", at), TokenStore.Load(store));
        TokenStore.Clear(store);
        Assert.Null(TokenStore.Load(store));
    }

    [Fact]
    public void AnUndecodableItemReadsAsNoGrant()
    {
        var store = new MemoryCredentialStore();
        store.Write("redline", "oauth", Encoding.UTF8.GetBytes("not json"));
        Assert.Null(TokenStore.Load(store));
    }
}

public class OAuthManagerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string Usage = "{\"five_hour\":{\"utilization\":42,\"resets_at\":\"2027-01-15T10:00:00Z\"}}";

    private static BorrowedCredential Fresh => new("cli-token", "cli-refresh", Now.AddHours(2));
    private static BorrowedCredential Expired => new("old-token", "cli-refresh", Now.AddHours(-1));

    [Fact]
    public async Task UsesAFreshBorrowedTokenAsTheBearer()
    {
        var handler = new ScriptedHandler(_ => ScriptedHandler.Json(Usage));
        var oauth = new OAuthManager(new OAuthSettings(), useCLIToken: true, new MemoryCredentialStore(),
            new HttpClient(handler), loadCli: () => new CredentialOutcome.Found(Fresh), cliModifiedAt: () => null,
            clock: () => Now);

        var (windows, error) = await oauth.FetchLimitsAsync();
        Assert.Null(error);
        Assert.Equal(42, Assert.Single(windows!).Utilization);
        Assert.Equal("Bearer cli-token", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Contains("oauth-2025-04-20", handler.Requests[0].Headers.GetValues("anthropic-beta"));
        Assert.True(oauth.UsingCLIToken);
    }

    [Fact]
    public async Task AnExpiredBorrowedTokenIsNeverRefreshed()
    {
        var handler = new ScriptedHandler(_ => ScriptedHandler.Json(Usage));
        var runs = 0;
        var delegated = new DelegatedRefresh(() => "claude.exe", (_, _, _) => { runs++; return 0; });
        var oauth = new OAuthManager(new OAuthSettings { ClientId = "x" }, true, new MemoryCredentialStore(),
            new HttpClient(handler), () => new CredentialOutcome.Found(Expired), () => null, delegated, clock: () => Now);

        var (windows, error) = await oauth.FetchLimitsAsync();
        Assert.Null(windows);
        Assert.Equal("Claude token expired; waiting for Claude Code to renew it", error);
        // Delegation ran once and, having renewed nothing, is not tried again
        Assert.Equal(1, runs);
        await oauth.FetchLimitsAsync();
        Assert.Equal(1, runs);
        // Nothing was sent anywhere: no usage call with a dead token, no token endpoint at all
        Assert.Empty(handler.Requests);
        Assert.True(oauth.IsSignedIn);
    }

    [Fact]
    public async Task DelegatedRenewalIsPickedUpOnTheForcedReRead()
    {
        var handler = new ScriptedHandler(_ => ScriptedHandler.Json(Usage));
        var renewed = false;
        var delegated = new DelegatedRefresh(() => "claude.exe", (_, _, _) => { renewed = true; return 0; });
        var oauth = new OAuthManager(new OAuthSettings(), true, new MemoryCredentialStore(), new HttpClient(handler),
            () => new CredentialOutcome.Found(renewed ? Fresh : Expired), () => null, delegated, clock: () => Now);

        var (windows, error) = await oauth.FetchLimitsAsync();
        Assert.Null(error);
        Assert.NotNull(windows);
        Assert.Equal("Bearer cli-token", handler.Requests.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task OnlyASignedOutCliLatches()
    {
        var reads = 0;
        CredentialOutcome next = new CredentialOutcome.AccessDenied();
        var clock = Now;
        var oauth = new OAuthManager(new OAuthSettings(), true, new MemoryCredentialStore(),
            new HttpClient(new ScriptedHandler(_ => ScriptedHandler.Json(Usage))),
            () => { reads++; return next; }, () => null, new DelegatedRefresh(() => null), clock: () => clock);

        var (_, error) = await oauth.FetchLimitsAsync();
        Assert.Equal("Credential access needed; choose Reconnect", error);
        await oauth.FetchLimitsAsync();
        Assert.Equal(1, reads);
        // A refusal is retried once its wait has passed
        clock = Now.AddSeconds(OAuthManager.DeniedRetry + 1);
        next = new CredentialOutcome.NotFound();
        (_, error) = await oauth.FetchLimitsAsync();
        Assert.Equal(2, reads);
        Assert.Equal("Claude Code is signed out; run claude to sign in", error);
        clock = clock.AddHours(5);
        await oauth.FetchLimitsAsync();
        Assert.Equal(2, reads);
        oauth.ResetCLIProbe();
        await oauth.FetchLimitsAsync();
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ARateLimitBacksOff()
    {
        var handler = new ScriptedHandler(_ => ScriptedHandler.Json("{}", HttpStatusCode.TooManyRequests));
        var oauth = new OAuthManager(new OAuthSettings(), true, new MemoryCredentialStore(), new HttpClient(handler),
            () => new CredentialOutcome.Found(Fresh), () => null, clock: () => Now);
        Assert.Equal("Usage temporarily unavailable", (await oauth.FetchLimitsAsync()).Error);
        Assert.Equal("Usage temporarily unavailable", (await oauth.FetchLimitsAsync()).Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OwnGrantRefreshIsFormEncodedAndSaved()
    {
        var store = new MemoryCredentialStore();
        new TokenStore("mine-old", "mine-refresh", Now.AddMinutes(-5)).Save(store);
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/token")
            ? ScriptedHandler.Json("{\"access_token\":\"mine-new\",\"expires_in\":3600}")
            : ScriptedHandler.Json(Usage));
        var oauth = new OAuthManager(new OAuthSettings { ClientId = "cid" }, useCLIToken: false, store,
            new HttpClient(handler), () => new CredentialOutcome.NotFound(), () => null, clock: () => Now);

        var (windows, error) = await oauth.FetchLimitsAsync();
        Assert.Null(error);
        Assert.NotNull(windows);
        Assert.Equal("application/x-www-form-urlencoded", handler.Requests[0].Content!.Headers.ContentType!.MediaType);
        Assert.Contains("grant_type=refresh_token", handler.Bodies[0]);
        var saved = TokenStore.Load(store)!;
        Assert.Equal("mine-new", saved.AccessToken);
        Assert.Equal("mine-refresh", saved.RefreshToken);
    }

    [Fact]
    public async Task ARejectedRefreshClearsTheGrant()
    {
        var store = new MemoryCredentialStore();
        new TokenStore("mine-old", "mine-refresh", Now.AddMinutes(-5)).Save(store);
        var handler = new ScriptedHandler(_ => ScriptedHandler.Json("{\"error\":{\"type\":\"invalid_request_error\"}}", HttpStatusCode.BadRequest));
        var oauth = new OAuthManager(new OAuthSettings { ClientId = "cid" }, false, store, new HttpClient(handler),
            () => new CredentialOutcome.NotFound(), () => null, clock: () => Now);

        var (_, error) = await oauth.FetchLimitsAsync();
        Assert.StartsWith("Sign-in could not be renewed", error);
        // Form first, then the JSON retry, then nothing more
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("application/json", handler.Requests[1].Content!.Headers.ContentType!.MediaType);
        Assert.Null(TokenStore.Load(store));
        Assert.False(oauth.HasOwnGrant);
    }

    [Fact]
    public async Task SignInNeedsAClientId() =>
        Assert.Equal("Set oauth.clientId in the config first",
            await new OAuthManager(new OAuthSettings(), false, new MemoryCredentialStore()).SignInAsync());

    [Fact]
    public void RefusesNonHttpsUrls()
    {
        Assert.Null(OAuthManager.HttpsUri("http://api.anthropic.com/api/oauth/usage"));
        Assert.NotNull(OAuthManager.HttpsUri("https://api.anthropic.com/api/oauth/usage"));
    }
}

public class ClaudeCredentialSourceTests
{
    private const string Blob = "{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"refreshToken\":\"r\",\"expiresAt\":1900000000000}}";

    [Fact]
    public void TheFileComesFirst()
    {
        using var home = new TempHome();
        home.Write(".claude/.credentials.json", Blob);
        var store = new MemoryCredentialStore();
        store.Denied.Add(ClaudeCredentialSource.CredentialTarget);
        var found = Assert.IsType<CredentialOutcome.Found>(ClaudeCredentialSource.Load(home.Path, store));
        Assert.Equal("tok", found.Credential.AccessToken);
    }

    [Fact]
    public void FallsBackToCredentialManagerIncludingSuffixedTargets()
    {
        using var home = new TempHome();
        var store = new MemoryCredentialStore();
        store.Write(ClaudeCredentialSource.CredentialTarget + "/user", "u", Encoding.Unicode.GetBytes(Blob));
        var found = Assert.IsType<CredentialOutcome.Found>(ClaudeCredentialSource.Load(home.Path, store));
        Assert.Equal("tok", found.Credential.AccessToken);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void KeepsTheOutcomesApart()
    {
        using var home = new TempHome();
        var store = new MemoryCredentialStore();
        Assert.IsType<CredentialOutcome.NotFound>(ClaudeCredentialSource.Load(home.Path, store));

        store.Denied.Add(ClaudeCredentialSource.CredentialTarget);
        Assert.IsType<CredentialOutcome.AccessDenied>(ClaudeCredentialSource.Load(home.Path, store));

        home.Write(".claude/.credentials.json", "{\"nothing\":true}");
        store.Denied.Clear();
        Assert.IsType<CredentialOutcome.Unreadable>(ClaudeCredentialSource.Load(home.Path, store));
    }
}
