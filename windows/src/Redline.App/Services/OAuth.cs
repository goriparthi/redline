// Claude rate-limit access. Prefers borrowing the CLI's token, which the CLI keeps refreshed,
// and falls back to this app's own optional OAuth + PKCE sign-in. Borrowed tokens are never spent.
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Net.Http;
using Redline.Core;

namespace Redline.App.Services;

/// This app's own grant, kept in Credential Manager under the generic target "redline".
public sealed record TokenStore(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public const string Target = "redline";
    public const string Account = "oauth";

    public bool Save(ICredentialStore? store = null)
    {
        var json = new JsonObject
        {
            ["accessToken"] = AccessToken,
            ["refreshToken"] = RefreshToken,
            ["expiresAt"] = ExpiresAt.ToUnixTimeMilliseconds() / 1000.0,
        };
        // Reports failure rather than claiming a sign-in that will read as signed out next poll
        return (store ?? WindowsCredentialStore.Shared).Write(Target, Account, Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    public static TokenStore? Load(ICredentialStore? store = null)
    {
        CredentialRead read;
        try { read = (store ?? WindowsCredentialStore.Shared).Read(Target); }
        catch { return null; }
        if (read.Credential is not { } c) return null;
        // An item that will not decode reads as "signed out" everywhere unless it says so here
        var json = Json.ParseObject(WindowsCredentialStore.DecodeBlob(c.Blob));
        if (json is null || Json.Str(json["accessToken"]) is not { Length: > 0 } access ||
            Json.Num(json["expiresAt"]) is not { } seconds)
        {
            Diag.Log.Error("oauth.token_decode_failed", "Credential Manager item did not decode");
            return null;
        }
        return new TokenStore(access, Json.Str(json["refreshToken"]),
            DateTimeOffset.UnixEpoch.AddMilliseconds(seconds * 1000));
    }

    public static void Clear(ICredentialStore? store = null)
    {
        try { (store ?? WindowsCredentialStore.Shared).Delete(Target); } catch { }
    }
}

/// PKCE and callback helpers, pure so they are testable without a browser or a socket.
public static class Pkce
{
    public static string RandomUrlSafe(int count) => Base64Url(RandomNumberGenerator.GetBytes(count));

    /// S256: base64url(SHA-256(ASCII verifier)), no padding.
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// Constant time, so the callback cannot be probed for the state one byte at a time.
    public static bool StateMatches(string expected, string? returned) =>
        returned is not null && expected.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(returned));

    public static string RedirectUri(int port) => $"http://localhost:{port}/callback";

    /// Null for anything but an absolute https authorize URL.
    public static string? AuthorizeUrl(OAuthSettings settings, string challenge, string state)
    {
        if (!Uri.TryCreate(settings.AuthorizeUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            return null;
        var items = new (string, string)[]
        {
            ("client_id", settings.ClientId),
            ("response_type", "code"),
            ("redirect_uri", RedirectUri(settings.RedirectPort)),
            ("scope", settings.Scopes),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
        };
        var query = string.Join("&", items.Select(i => Uri.EscapeDataString(i.Item1) + "=" + Uri.EscapeDataString(i.Item2)));
        return new UriBuilder(baseUri) { Query = query }.Uri.AbsoluteUri;
    }

    /// Code and state from the first line of a raw HTTP request, e.g. "GET /callback?code=x HTTP/1.1".
    public static (string? Code, string? State) ParseCallback(string request)
    {
        var firstLine = request.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (null, null);
        var q = parts[1].IndexOf('?');
        if (q < 0) return (null, null);
        string? code = null, state = null;
        foreach (var pair in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Unescape(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Unescape(pair[(eq + 1)..]);
            if (name == "code" && code is null) code = value;
            if (name == "state" && state is null) state = value;
        }
        return (code, state);
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }
}

/// One-shot listener for the OAuth redirect. A raw socket on 127.0.0.1, because HttpListener
/// goes through http.sys, which listens on every interface and only filters by Host header.
public sealed class CallbackServer
{
    private TcpListener? _listener;

    /// Binds now, so a busy port fails here; the task completes when a request carries a code.
    public Task<(string? Code, string? State)> Start(int port, CancellationToken ct)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        return Task.Run(() => Serve(listener, ct), CancellationToken.None);
    }

    private async Task<(string? Code, string? State)> Serve(TcpListener listener, CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                using (client)
                {
                    var (code, state) = await Handle(client, ct);
                    if (code is not null) return (code, state);
                }
            }
        }
        finally { Stop(); }
    }

    private static async Task<(string? Code, string? State)> Handle(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[65536];
        int read;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { read = await stream.ReadAsync(buffer, timeout.Token); }
            catch { return (null, null); }
        }
        if (read <= 0) return (null, null);
        var (code, state) = Pkce.ParseCallback(Encoding.UTF8.GetString(buffer, 0, read));
        var body = code is not null ? "Signed in. You can close this tab." : "No authorization code in request.";
        var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n" +
                   $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct); } catch { }
        return (code, state);
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }
}

public sealed class OAuthManager
{
    private OAuthSettings _settings;
    private bool _useCLIToken;
    private readonly CallbackServer _server = new();
    private CancellationTokenSource? _signIn;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _lock = new();

    /// The borrowed credential with when it was read, so it is re-read when it dies.
    private (BorrowedCredential? Credential, DateTimeOffset At, CredentialOutcome Outcome)? _cliProbe;
    private DateTimeOffset? _cliSeenModifiedAt;
    /// Only a genuinely signed-out CLI latches. Everything else retries on a timer.
    private bool _cliSignedOut;
    private bool _delegationIneffective;
    // The usage endpoint rate-limits hard and stays limited, so back off rather than hammer it
    private DateTimeOffset? _backoffUntil;
    private TokenEncoding? _tokenEncoding;
    private int _consecutive429;

    private readonly ICredentialStore _store;
    private readonly HttpClient _http;
    private readonly Func<CredentialOutcome> _loadCli;
    private readonly Func<DateTimeOffset?> _cliModifiedAt;
    private readonly DelegatedRefresh _delegated;
    private readonly Action<string> _openBrowser;
    private readonly Func<DateTimeOffset> _clock;

    /// A denied read must not be re-asked every poll, but must be re-asked eventually.
    public const double DeniedRetry = 600;
    public const double UnreadableRetry = 120;

    public OAuthManager(OAuthSettings settings, bool useCLIToken,
                        ICredentialStore? store = null, HttpClient? http = null,
                        Func<CredentialOutcome>? loadCli = null, Func<DateTimeOffset?>? cliModifiedAt = null,
                        DelegatedRefresh? delegated = null, Action<string>? openBrowser = null,
                        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _useCLIToken = useCLIToken;
        _store = store ?? WindowsCredentialStore.Shared;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _loadCli = loadCli ?? (() => ClaudeCredentialSource.Load());
        _cliModifiedAt = cliModifiedAt ?? (() => ClaudeCredentialSource.ModifiedAt());
        _delegated = delegated ?? DelegatedRefresh.Shared;
        _openBrowser = openBrowser ?? OpenInBrowser;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public OAuthSettings Settings => _settings;

    public void Update(OAuthSettings settings, bool useCLIToken)
    {
        var changed = useCLIToken != _useCLIToken || settings.ClientId != _settings.ClientId;
        _settings = settings;
        _useCLIToken = useCLIToken;
        // A changed choice must probe fresh, or a single refusal latches until relaunch
        if (changed) ResetCLIProbe();
    }

    /// Forget cached or rejected CLI-token state; the user re-asserted the choice.
    public void ResetCLIProbe()
    {
        lock (_lock)
        {
            _cliProbe = null;
            _cliSeenModifiedAt = null;
            _cliSignedOut = false;
            _delegationIneffective = false;
        }
        _delegated.ResetCooldown();
    }

    /// An expired borrowed credential still counts: it means "renew me", not "gone".
    public bool IsSignedIn
    {
        get
        {
            bool have;
            lock (_lock) have = _cliProbe?.Credential is not null;
            return have || TokenStore.Load(_store) is not null;
        }
    }

    public bool CanSignIn => _settings.IsConfigured;

    /// This app's own browser grant, as distinct from a borrowed CLI token.
    public bool HasOwnGrant => TokenStore.Load(_store) is not null;

    public bool UsingCLIToken => CachedCLIToken() is not null;

    /// Names which nothing this is, because each needs a different action from the user.
    public string TokenUnavailableReason()
    {
        bool signedOut, expired;
        CredentialOutcome? outcome;
        lock (_lock)
        {
            signedOut = _cliSignedOut;
            outcome = _cliProbe?.Outcome;
            expired = _cliProbe?.Credential is not null;
        }
        if (!_useCLIToken) return "No limits source set up";
        if (signedOut) return "Claude Code is signed out; run claude to sign in";
        if (outcome is CredentialOutcome.AccessDenied) return "Credential access needed; choose Reconnect";
        if (expired) return "Claude token expired; waiting for Claude Code to renew it";
        return "No limits source set up";
    }

    private string? CachedCLIToken()
    {
        lock (_lock)
        {
            return _cliProbe?.Credential is { } c && c.IsFresh(_clock()) ? c.AccessToken : null;
        }
    }

    /// Decides whether the store is worth reading again, then reads it. Every "no" here is
    /// temporary except a signed-out CLI.
    internal void RefreshCLIProbe(bool force = false)
    {
        if (!_useCLIToken) return;
        var now = _clock();
        bool signedOut;
        (BorrowedCredential? Credential, DateTimeOffset At, CredentialOutcome Outcome)? probe;
        DateTimeOffset? seen;
        lock (_lock)
        {
            signedOut = _cliSignedOut;
            probe = _cliProbe;
            seen = _cliSeenModifiedAt;
        }
        if (signedOut && !force) return;

        // A moved stamp means the CLI rotated the token and the cached copy is worthless
        var modifiedAt = _cliModifiedAt();
        var rotated = modifiedAt is not null && modifiedAt != seen;

        if (!force && !rotated && probe is { } p)
        {
            if (p.Credential is { } credential)
            {
                // A live token needs nothing; an expired one is re-read, the CLI may have renewed it
                if (credential.IsFresh(now)) return;
            }
            else
            {
                var wait = p.Outcome is CredentialOutcome.AccessDenied ? DeniedRetry : UnreadableRetry;
                if ((now - p.At).TotalSeconds < wait) return;
            }
        }

        var outcome = _loadCli();
        lock (_lock)
        {
            _cliProbe = (outcome.CredentialOrNull, now, outcome);
            _cliSeenModifiedAt = modifiedAt;
            // Only an absent item is durable news; the user has to sign the CLI back in
            _cliSignedOut = outcome.IsTerminal;
        }
    }

    /// The stale-token recovery, capped at one rung: ask Claude Code to renew its own
    /// credential. There is no rung 2; the borrowed refresh token is never spent.
    internal string? Escalate()
    {
        var now = _clock();
        BorrowedCredential? credential;
        bool skip;
        lock (_lock)
        {
            credential = _cliProbe?.Credential;
            skip = _delegationIneffective;
        }
        if (credential is null || credential.IsFresh(now) || skip) return null;

        var before = credential.ExpiresAt;
        switch (_delegated.Attempt(now))
        {
            case DelegatedRefreshOutcome.Ran:
                RefreshCLIProbe(force: true);
                if (CachedCLIToken() is { } token) return token;
                // It ran and changed nothing, so this CLI does not renew on a status check
                lock (_lock)
                {
                    if (_cliProbe?.Credential?.ExpiresAt == before) _delegationIneffective = true;
                }
                break;
            case DelegatedRefreshOutcome.CliUnavailable:
                lock (_lock) _delegationIneffective = true;
                break;
        }
        return null;
    }

    /// One deliberate read, for the moment the user enables the CLI token.
    public async Task<bool> ProbeCLITokenAsync()
    {
        await Task.Run(() => RefreshCLIProbe());
        return CachedCLIToken() is not null;
    }

    private string RecordRateLimit()
    {
        lock (_lock)
        {
            _consecutive429++;
            _backoffUntil = _clock().AddSeconds(ClaudeAuthPolicy.Backoff(_consecutive429));
        }
        return "Usage temporarily unavailable";
    }

    private void ClearRateLimit()
    {
        lock (_lock)
        {
            _consecutive429 = 0;
            _backoffUntil = null;
        }
    }

    /// A deliberate stop, not a failure. Only Sign Out uses it; only ResetCLIProbe undoes it.
    private void RejectCLIToken()
    {
        lock (_lock)
        {
            _cliProbe = null;
            _cliSignedOut = true;
        }
    }

    /// Opens the browser for consent. Null on success, else a message for the user.
    public async Task<string?> SignInAsync(CancellationToken cancel = default)
    {
        if (!_settings.IsConfigured) return "Set oauth.clientId in the config first";
        AbandonSignIn();
        var verifier = Pkce.RandomUrlSafe(64);
        var state = Pkce.RandomUrlSafe(32);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        _signIn = cts;

        Task<(string? Code, string? State)> callback;
        try { callback = _server.Start(_settings.RedirectPort, cts.Token); }
        catch (Exception e)
        {
            AbandonSignIn();
            return $"Cannot listen on port {_settings.RedirectPort}: {e.Message}";
        }
        if (Pkce.AuthorizeUrl(_settings, Pkce.Challenge(verifier), state) is not { } url)
        {
            AbandonSignIn();
            return "Bad authorize URL";
        }
        _openBrowser(url);

        (string? Code, string? State) result;
        try { result = await callback; }
        catch (OperationCanceledException)
        {
            AbandonSignIn();
            return cancel.IsCancellationRequested ? "Sign-in cancelled" : "Sign-in timed out";
        }
        catch (Exception e)
        {
            AbandonSignIn();
            return $"Sign-in failed: {e.Message}";
        }
        AbandonSignIn();
        if (result.Code is not { } code || !Pkce.StateMatches(state, result.State))
            return "Callback missing code or state mismatch";
        return await ExchangeAsync(code, state, verifier);
    }

    public void SignOut()
    {
        AbandonSignIn();
        TokenStore.Clear(_store);
        // Also stop using the CLI's token, or Sign Out would appear to do nothing
        RejectCLIToken();
    }

    private void AbandonSignIn()
    {
        var pending = _signIn;
        _signIn = null;
        try { pending?.Cancel(); } catch { }
        _server.Stop();
    }

    private async Task<string?> ExchangeAsync(string code, string state, string verifier)
    {
        var (json, status, text) = await PostTokenAsync(_settings.TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = Pkce.RedirectUri(_settings.RedirectPort),
            ["code_verifier"] = verifier,
        });
        if (json is null || Json.Str(json["access_token"]) is not { Length: > 0 } access)
            return $"Sign-in could not be completed (HTTP {status}). " + Prefix(text, 120);
        var expiresIn = Json.Num(json["expires_in"]) ?? 3600;
        var saved = new TokenStore(access, Json.Str(json["refresh_token"]), _clock().AddSeconds(expiresIn - 60)).Save(_store);
        return saved ? null : "Signed in but could not save token to Credential Manager";
    }

    private async Task<(string? Token, string? Error)> WithValidTokenAsync()
    {
        // Prefer the CLI's token: it is refreshed for us, so it outlives our own grant
        if (CachedCLIToken() is { } cli) return (cli, null);
        // Stale rather than absent: ask the CLI to renew its own credential and re-read
        if (_useCLIToken && await Task.Run(Escalate) is { } escalated) return (escalated, null);
        if (TokenStore.Load(_store) is not { } store) return (null, TokenUnavailableReason());
        if (store.ExpiresAt > _clock()) return (store.AccessToken, null);
        if (store.RefreshToken is not { } refresh) return (null, "Token expired; sign in again");

        var (json, status, text) = await PostTokenAsync(_settings.TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = _settings.ClientId,
        });
        if (json is null || Json.Str(json["access_token"]) is not { Length: > 0 } access)
        {
            // A rejection is terminal, so drop the dead grant and let Sign In be offered again
            var outcome = ClaudeAuthPolicy.ClassifyRefresh(status, text);
            if (outcome.IsTerminal) TokenStore.Clear(_store);
            return (null, outcome.Message);
        }
        var expiresIn = Json.Num(json["expires_in"]) ?? 3600;
        new TokenStore(access, Json.Str(json["refresh_token"]) ?? refresh, _clock().AddSeconds(expiresIn - 60)).Save(_store);
        return (access, null);
    }

    /// Windows or an error message, never both. Serialized so two polls never probe at once.
    public async Task<(List<LimitWindow>? Windows, string? Error)> FetchLimitsAsync()
    {
        await _probeGate.WaitAsync();
        try
        {
            await Task.Run(() => RefreshCLIProbe());
            return await LoadLimitsAsync(allowRetry: true);
        }
        finally { _probeGate.Release(); }
    }

    private async Task<(List<LimitWindow>? Windows, string? Error)> LoadLimitsAsync(bool allowRetry)
    {
        bool waiting;
        lock (_lock) waiting = _backoffUntil is { } until && until > _clock();
        if (waiting) return (null, "Usage temporarily unavailable");

        var usingCLI = CachedCLIToken() is not null;
        var (token, err) = await WithValidTokenAsync();
        if (token is null) return (null, err ?? "Not signed in");
        if (HttpsUri(_settings.UsageUrl) is not { } url) return (null, "Bad usage URL");

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", _settings.BetaHeader);
        int status;
        string text;
        try
        {
            using var resp = await _http.SendAsync(req);
            status = (int)resp.StatusCode;
            text = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception e) { return (null, e.Message); }

        var json = Json.ParseObject(text);
        if (json is null || status is < 200 or >= 300)
        {
            // A refusal usually means the CLI rotated its token under us: re-read once, no latch
            if (usingCLI && status is 401 or 403 && allowRetry)
            {
                await Task.Run(() => RefreshCLIProbe(force: true));
                return await LoadLimitsAsync(allowRetry: false);
            }
            if (status == 429) return (null, RecordRateLimit());
            return (null, $"Limits HTTP {status}: {Prefix(text, 160)}");
        }
        ClearRateLimit();
        return (LimitParser.ClaudeUsage(json), null);
    }

    /// RFC 6749 says form-encoded; this endpoint is undocumented, so a 4xx is retried as JSON
    /// once and whichever answers is remembered for the rest of the launch.
    private enum TokenEncoding { Form, Json }

    private async Task<(JsonObject? Json, int Status, string Text)> PostTokenAsync(string url, Dictionary<string, string> body)
    {
        TokenEncoding? preferred;
        lock (_lock) preferred = _tokenEncoding;
        var first = preferred ?? TokenEncoding.Form;
        var r = await SendAsync(url, body, first);
        if (r.Json is not null)
        {
            lock (_lock) _tokenEncoding = first;
            return r;
        }
        // Only a rejection of the request itself is worth re-sending differently
        if (r.Status is < 400 or >= 500 || preferred is not null) return r;
        var other = first == TokenEncoding.Form ? TokenEncoding.Json : TokenEncoding.Form;
        var retry = await SendAsync(url, body, other);
        if (retry.Json is not null)
        {
            lock (_lock) _tokenEncoding = other;
            return retry;
        }
        // The spec-compliant request's rejection is the more meaningful one to report
        return (null, r.Status, r.Text);
    }

    private async Task<(JsonObject? Json, int Status, string Text)> SendAsync(string url, Dictionary<string, string> body, TokenEncoding encoding)
    {
        if (HttpsUri(url) is not { } uri) return (null, 0, $"Bad URL {url}");
        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = encoding == TokenEncoding.Form
                ? new StringContent(FormBody.Encoded(body), Encoding.UTF8, "application/x-www-form-urlencoded")
                : new StringContent(new JsonObject(body.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))).ToJsonString(),
                                    Encoding.UTF8, "application/json"),
        };
        try
        {
            using var resp = await _http.SendAsync(req);
            var status = (int)resp.StatusCode;
            var text = await resp.Content.ReadAsStringAsync();
            var json = Json.ParseObject(text);
            if (json is null || status is < 200 or >= 300) return (null, status, Prefix(text, 300));
            return (json, status, text);
        }
        catch (Exception e) { return (null, 0, e.Message); }
    }

    /// Credentials only ever travel over https, whatever a config override says.
    internal static Uri? HttpsUri(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;

    private static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    private static void OpenInBrowser(string url)
    {
        if (HttpsUri(url) is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Diag.Log.Error("oauth.browser_failed", "could not open the browser", new() { ["error"] = e.Message }); }
    }
}
