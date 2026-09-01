// Claude rate-limit access. Prefers borrowing the CLI's Keychain token, which the CLI
// keeps refreshed, and falls back to this app's own optional OAuth + PKCE sign-in.
import RedlineCore
import AppKit
import CryptoKit
import Foundation
import Network
import Security

struct TokenStore: Codable {
    let accessToken: String
    let refreshToken: String?
    let expiresAt: Date

    static let service = "redline"

    @discardableResult
    func save() -> Bool {
        guard let data = try? JSONEncoder().encode(self) else {
            Diag.log.error("oauth.token_encode_failed", "could not encode the token store")
            return false
        }
        TokenStore.clear()
        let attrs: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: TokenStore.service,
            kSecAttrAccount: "oauth",
            kSecAttrAccessible: kSecAttrAccessibleWhenUnlocked,
            kSecValueData: data,
        ]
        let status = SecItemAdd(attrs as CFDictionary, nil)
        if status == errSecSuccess { return true }
        // A rebuilt binary loses access to the old item's ACL, so clear() can fail and
        // leave a duplicate behind; update it in place rather than dropping the token.
        guard status == errSecDuplicateItem else { return false }
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: TokenStore.service,
            kSecAttrAccount: "oauth",
        ]
        return SecItemUpdate(query as CFDictionary,
                             [kSecValueData: data] as CFDictionary) == errSecSuccess
    }

    static func load() -> TokenStore? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: "oauth",
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        // A Keychain item that will not decode reads as "signed out" everywhere, which is
        // indistinguishable from an expired grant unless it says so here.
        do {
            return try JSONDecoder().decode(TokenStore.self, from: data)
        } catch {
            Diag.log.error("oauth.token_decode_failed", "Keychain item did not decode",
                           ["error": String(describing: error)])
            return nil
        }
    }

    static func clear() {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: "oauth",
        ]
        SecItemDelete(query as CFDictionary)
    }
}

// Minimal one-shot HTTP listener that catches the OAuth redirect on localhost
final class CallbackServer {
    private var listener: NWListener?

    func start(port: UInt16, onCode: @escaping (String?, String?) -> Void) throws {
        stop()
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            throw NSError(domain: "callback", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Bad port \(port)"])
        }
        // Bind loopback only; the OAuth redirect must not be reachable from the network
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: nwPort)
        let l = try NWListener(using: params)
        l.newConnectionHandler = { [weak self] conn in
            conn.start(queue: .main)
            conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, _ in
                guard let data, let req = String(data: data, encoding: .utf8) else {
                    conn.cancel()
                    return
                }
                var code: String?
                var state: String?
                let firstLine = req.split(separator: "\r\n").first.map(String.init) ?? ""
                let parts = firstLine.split(separator: " ")
                if parts.count >= 2, let comps = URLComponents(string: String(parts[1])) {
                    code = comps.queryItems?.first(where: { $0.name == "code" })?.value
                    state = comps.queryItems?.first(where: { $0.name == "state" })?.value
                }
                let body = code != nil
                    ? "Signed in. You can close this tab."
                    : "No authorization code in request."
                let resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                    + "Content-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
                conn.send(content: resp.data(using: .utf8),
                          completion: .contentProcessed { _ in conn.cancel() })
                if code != nil {
                    self?.stop()
                    onCode(code, state)
                }
            }
        }
        l.start(queue: .main)
        listener = l
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }
}

final class OAuthManager {
    private(set) var settings: OAuthSettings
    private var useCLIToken: Bool
    private let server = CallbackServer()
    private var pendingVerifier: String?
    private var pendingState: String?
    private var signInTimeout: DispatchWorkItem?
    private let probeQueue = DispatchQueue(label: "oauth-cli-probe", qos: .utility)
    private let lock = NSLock()
    /// The borrowed credential and when it was read. Held with its expiry rather than as a
    /// bare string, so the token is re-read when it dies instead of after it fails.
    private var cliProbe: (credential: BorrowedCredential?, at: Date, outcome: CredentialOutcome)?
    /// Modification date of the CLI's Keychain item at the last read. Attribute-only reads do
    /// not decrypt the secret, so watching this costs nothing and no consent.
    private var cliSeenModifiedAt: Date?
    /// Only a genuinely signed-out CLI latches. Everything else retries, because the old
    /// behaviour cached a single failed read forever and needed a manual Reconnect.
    private var cliSignedOut = false
    /// Set once delegated refresh proves it does not renew the token on this machine, so the
    /// process spawn is not repeated on every expiry.
    private var delegationIneffective = false
    // The usage endpoint rate-limits hard and stays limited, so back off rather than
    // hammering it every poll. See anthropics/claude-code issues 31021 and 31637.
    private var backoffUntil: Date?
    /// Which body encoding this endpoint answered, once one has
    private var tokenEncoding: TokenEncoding?
    private var consecutive429 = 0

    init(settings: OAuthSettings, useCLIToken: Bool) {
        self.settings = settings
        self.useCLIToken = useCLIToken
    }

    func update(settings: OAuthSettings, useCLIToken: Bool) {
        let changed = useCLIToken != self.useCLIToken
            || settings.clientId != self.settings.clientId
        self.settings = settings
        self.useCLIToken = useCLIToken
        // A changed choice must probe fresh. Without this, a denied Keychain prompt or a
        // single 401 latched cliRejected until relaunch, so re-enabling the toggle in the
        // setup window silently did nothing.
        if changed { resetCLIProbe() }
    }

    /// Forget any cached or rejected CLI-token state so the next fetch reads the Keychain
    /// again. Called when the user re-asserts the CLI-token choice in the setup window.
    func resetCLIProbe() {
        lock.lock()
        cliProbe = nil
        cliSeenModifiedAt = nil
        cliSignedOut = false
        delegationIneffective = false
        lock.unlock()
        DelegatedRefresh.resetCooldown()
    }

    // Reads only the cached probe; the Keychain itself is touched on probeQueue.
    // A merely expired credential still counts as signed in: it means "renew me", not "gone",
    // and treating it as gone is what discarded the last good percentages mid-day.
    var isSignedIn: Bool {
        lock.lock()
        let haveCredential = cliProbe?.credential != nil
        lock.unlock()
        return haveCredential || TokenStore.load() != nil
    }

    /// Says which of the several different nothings this is. "No Claude token available" was
    /// shown for a signed-out CLI, a locked Keychain and an expired token alike, and those
    /// need three different actions from the user.
    private func tokenUnavailableReason() -> String {
        lock.lock()
        let signedOut = cliSignedOut
        let outcome = cliProbe?.outcome
        let expired = cliProbe?.credential != nil
        lock.unlock()
        // Named for what the user can do about it: "no source set up" points at Settings,
        // where "not signed in" implied sign-in was the only fix.
        if !useCLIToken {
            return "No limits source set up"
        }
        if signedOut { return "Claude Code is signed out; run claude to sign in" }
        if outcome == .accessDenied { return "Keychain access needed; choose Reconnect" }
        if expired { return "Claude token expired; waiting for Claude Code to renew it" }
        return "No limits source set up"
    }

    // Sign In is only offered when a client id has been configured
    var canSignIn: Bool { settings.isConfigured }

    /// Specifically this app's own browser grant, as distinct from a borrowed CLI token.
    /// The menu needs the distinction to label the source and to offer Sign In vs Sign Out.
    var hasOwnGrant: Bool { TokenStore.load() != nil }

    var usingCLIToken: Bool { cachedCLIToken() != nil }

    private var redirectUri: String { "http://localhost:\(settings.redirectPort)/callback" }

    private func cachedCLIToken() -> String? {
        lock.lock()
        defer { lock.unlock() }
        guard let c = cliProbe?.credential, c.isFresh() else { return nil }
        return c.accessToken
    }

    /// How long to wait before re-reading after a read that did not produce a usable token.
    /// A denied prompt must not be re-asked every poll, but it must be re-asked eventually:
    /// caching that miss forever is what forced a manual Reconnect once or twice a day.
    private static let deniedRetry: TimeInterval = 600
    private static let unreadableRetry: TimeInterval = 120

    /// Decides whether the Keychain is worth touching again, then touches it. The whole point
    /// is that every "no" here is temporary except a signed-out CLI.
    private func refreshCLIProbe(force: Bool = false, now: Date = Date()) {
        guard useCLIToken else { return }
        lock.lock()
        let signedOut = cliSignedOut
        let probe = cliProbe
        let seenModified = cliSeenModifiedAt
        lock.unlock()
        guard !signedOut || force else { return }

        // Free change detection: an attribute-only query never decrypts the secret, so it
        // neither consults the ACL nor prompts. A moved timestamp means the CLI rotated the
        // token underneath us and the cached copy is already worthless.
        let modifiedAt = ClaudeCredentialSource.keychainModifiedAt()
        let rotated = modifiedAt != nil && modifiedAt != seenModified

        if !force, !rotated, let probe {
            if let credential = probe.credential {
                // A live token needs nothing. An expired one is re-read, because the CLI may
                // have renewed it since, and that is the ordinary daily case.
                if credential.isFresh(now: now) { return }
            } else {
                let wait = probe.outcome == .accessDenied ? Self.deniedRetry
                                                          : Self.unreadableRetry
                if now.timeIntervalSince(probe.at) < wait { return }
            }
        }

        let outcome = ClaudeCredentialSource.load()
        lock.lock()
        cliProbe = (outcome.credential, now, outcome)
        cliSeenModifiedAt = modifiedAt
        // Only an absent item is durable news; the user has to sign the CLI back in.
        cliSignedOut = outcome.isTerminal
        lock.unlock()
    }

    /// The stale-token recovery, deliberately capped at one rung: ask Claude Code to renew
    /// its own credential, which keeps a single refresh chain and cannot invalidate the CLI's
    /// login. There is no rung 2. An earlier build spent the CLI's refresh token directly
    /// ("minting"); Anthropic rotates refresh tokens on use, so every mint left the CLI
    /// holding a consumed token and forced a fresh `/login`. A borrowed token is read, never
    /// spent: when the CLI will not renew, the answer is stale data honestly labelled, not a
    /// forked chain.
    private func escalate(now: Date = Date()) -> String? {
        lock.lock()
        let credential = cliProbe?.credential
        let skipDelegation = delegationIneffective
        lock.unlock()
        guard let credential, !credential.isFresh(now: now), !skipDelegation else { return nil }

        let before = credential.expiresAt
        switch DelegatedRefresh.attempt(now: now) {
        case .ran:
            refreshCLIProbe(force: true, now: now)
            if let token = cachedCLIToken() { return token }
            // It ran and changed nothing, so this build of the CLI does not renew on a
            // status check. Stop paying for the process spawn on every expiry.
            lock.lock()
            if cliProbe?.credential?.expiresAt == before { delegationIneffective = true }
            lock.unlock()
        case .cliUnavailable:
            lock.lock()
            delegationIneffective = true
            lock.unlock()
        case .skippedByCooldown, .failed:
            break
        }
        return nil
    }

    /// One deliberate Keychain read, for the moment the user enables the CLI token: warms
    /// the same probe the fetch path uses, so enabling costs exactly one consent prompt.
    /// Blocks on that prompt, so never call from the main thread.
    func probeCLIToken() -> Bool {
        refreshCLIProbe()
        return cachedCLIToken() != nil
    }

    // Exponential backoff capped at 30 minutes, reported in the brand's wording
    private func recordRateLimit() -> String {
        lock.lock()
        consecutive429 += 1
        backoffUntil = Date().addingTimeInterval(
            ClaudeAuthPolicy.backoff(consecutiveFailures: consecutive429))
        lock.unlock()
        return "Usage temporarily unavailable"
    }

    private func clearRateLimit() {
        lock.lock()
        consecutive429 = 0
        backoffUntil = nil
        lock.unlock()
    }

    /// A deliberate stop, not a failure. Only Sign Out uses it, and only resetCLIProbe undoes
    /// it; nothing in the fetch path may latch this way any more.
    private func rejectCLIToken() {
        lock.lock()
        cliProbe = nil
        cliSignedOut = true
        lock.unlock()
    }

    // Opens the browser for consent; completion gets nil on success, else an error message
    func signIn(completion: @escaping (String?) -> Void) {
        guard settings.isConfigured else {
            completion("Set oauth.clientId in the config first")
            return
        }
        let verifier = Self.randomURLSafe(64)
        let state = Self.randomURLSafe(32)
        pendingVerifier = verifier
        pendingState = state
        signInTimeout?.cancel()
        let timeout = DispatchWorkItem { [weak self] in
            self?.abandonSignIn()
            completion("Sign-in timed out")
        }
        signInTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 300, execute: timeout)
        do {
            try server.start(port: settings.redirectPort) { [weak self] code, retState in
                guard let self else { return }
                self.signInTimeout?.cancel()
                self.signInTimeout = nil
                guard let code, retState == self.pendingState else {
                    self.abandonSignIn()
                    completion("Callback missing code or state mismatch")
                    return
                }
                self.exchange(code: code, completion: completion)
            }
        } catch {
            abandonSignIn()
            completion("Cannot listen on port \(settings.redirectPort): \(error.localizedDescription)")
            return
        }
        guard var comps = URLComponents(string: settings.authorizeUrl) else {
            abandonSignIn()
            completion("Bad authorize URL")
            return
        }
        comps.queryItems = [
            URLQueryItem(name: "client_id", value: settings.clientId),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "redirect_uri", value: redirectUri),
            URLQueryItem(name: "scope", value: settings.scopes),
            URLQueryItem(name: "code_challenge", value: Self.challenge(for: verifier)),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "state", value: state),
        ]
        guard let url = comps.url else {
            abandonSignIn()
            completion("Bad authorize URL")
            return
        }
        NSWorkspace.shared.open(url)
    }

    func signOut() {
        abandonSignIn()
        TokenStore.clear()
        // Also stop using the CLI's token, or Sign Out would appear to do nothing
        rejectCLIToken()
    }

    private func abandonSignIn() {
        server.stop()
        signInTimeout?.cancel()
        signInTimeout = nil
        pendingVerifier = nil
        pendingState = nil
    }

    private func exchange(code: String, completion: @escaping (String?) -> Void) {
        let state = pendingState ?? ""
        let verifier = pendingVerifier ?? ""
        pendingState = nil
        pendingVerifier = nil
        postToken(settings.tokenUrl, body: [
            "grant_type": "authorization_code",
            "code": code,
            "state": state,
            "client_id": settings.clientId,
            "redirect_uri": redirectUri,
            "code_verifier": verifier,
        ]) { json, status, text in
            guard let json, let access = json["access_token"] as? String else {
                completion("Sign-in could not be completed (HTTP \(status)). "
                           + String(text.prefix(120)))
                return
            }
            let expiresIn = (json["expires_in"] as? Double) ?? 3600
            let saved = TokenStore(accessToken: access,
                                   refreshToken: json["refresh_token"] as? String,
                                   expiresAt: Date().addingTimeInterval(expiresIn - 60)).save()
            completion(saved ? nil : "Signed in but could not save token to Keychain")
        }
    }

    private func withValidToken(_ completion: @escaping (String?, String?) -> Void) {
        // Prefer the CLI's token: it is refreshed for us, so it outlives our own grant
        if let cli = cachedCLIToken() {
            completion(cli, nil)
            return
        }
        // Stale rather than absent: ask the CLI to renew its own credential and re-read.
        if useCLIToken, let escalated = escalate() {
            completion(escalated, nil)
            return
        }
        guard let store = TokenStore.load() else {
            completion(nil, tokenUnavailableReason())
            return
        }
        if store.expiresAt > Date() {
            completion(store.accessToken, nil)
            return
        }
        guard let refresh = store.refreshToken else {
            completion(nil, "Token expired; sign in again")
            return
        }
        postToken(settings.tokenUrl, body: [
            "grant_type": "refresh_token",
            "refresh_token": refresh,
            "client_id": settings.clientId,
        ]) { json, status, text in
            guard let json, let access = json["access_token"] as? String else {
                // A rejection is terminal, so drop the dead grant; otherwise the app stays
                // "signed in" and retries a request that cannot succeed, on every poll, with
                // nothing offering Sign In. The old test for this looked for the literal
                // "invalid_grant", which this endpoint does not say.
                let outcome = ClaudeAuthPolicy.classifyRefresh(status: status, body: text)
                if outcome.isTerminal { TokenStore.clear() }
                completion(nil, outcome.message)
                return
            }
            let expiresIn = (json["expires_in"] as? Double) ?? 3600
            TokenStore(accessToken: access,
                       refreshToken: (json["refresh_token"] as? String) ?? refresh,
                       expiresAt: Date().addingTimeInterval(expiresIn - 60)).save()
            completion(access, nil)
        }
    }

    func fetchLimits(completion: @escaping ([LimitWindow]?, String?) -> Void) {
        probeQueue.async { [weak self] in
            guard let self else { return }
            self.refreshCLIProbe()
            self.loadLimits(allowRetry: true, completion: completion)
        }
    }

    private func loadLimits(allowRetry: Bool,
                            completion: @escaping ([LimitWindow]?, String?) -> Void) {
        lock.lock()
        let waiting = backoffUntil.map { $0 > Date() } ?? false
        lock.unlock()
        if waiting {
            completion(nil, "Usage temporarily unavailable")
            return
        }
        let usingCLI = cachedCLIToken() != nil
        withValidToken { [weak self] token, err in
            guard let self, let token else {
                completion(nil, err ?? "Not signed in")
                return
            }
            guard let url = URL(string: self.settings.usageUrl) else {
                completion(nil, "Bad usage URL")
                return
            }
            var req = URLRequest(url: url)
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            req.setValue(self.settings.betaHeader, forHTTPHeaderField: "anthropic-beta")
            URLSession.shared.dataTask(with: req) { data, resp, err in
                if let err {
                    completion(nil, err.localizedDescription)
                    return
                }
                let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
                guard let data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      (200..<300).contains(status) else {
                    // The CLI rotates its token underneath us, so a refusal usually just means
                    // ours is stale: force a fresh read, then climb the ladder. Nothing here
                    // latches, because a refusal is evidence about one token, not about
                    // whether the Keychain can be read at all.
                    if usingCLI, status == 401 || status == 403, allowRetry {
                        self.refreshCLIProbe(force: true)
                        self.loadLimits(allowRetry: false, completion: completion)
                        return
                    }
                    if status == 429 {
                        completion(nil, self.recordRateLimit())
                        return
                    }
                    let snippet = data.flatMap { String(data: $0, encoding: .utf8) }?
                        .prefix(160) ?? ""
                    completion(nil, "Limits HTTP \(status): \(snippet)")
                    return
                }
                self.clearRateLimit()
                completion(LimitParser.claudeUsage(json), nil)
            }.resume()
        }
    }

    /// How the token endpoint wants its body.
    ///
    /// RFC 6749 specifies `application/x-www-form-urlencoded` for token requests, and this code
    /// only ever sent JSON. That is the leading suspect for the refresh failing with the
    /// endpoint's own "Invalid request format" while the code exchange happened to be accepted.
    /// Rather than guess which one this undocumented endpoint honours, the first attempt is the
    /// spec-compliant one and a rejected request is retried the other way; whichever answers is
    /// remembered so the second request happens at most once per launch.
    private enum TokenEncoding {
        case form, json
    }

    private func postToken(_ urlStr: String, body: [String: String],
                           completion: @escaping ([String: Any]?, Int, String) -> Void) {
        let first = preferredTokenEncoding ?? .form
        send(urlStr, body: body, encoding: first) { [weak self] json, status, text in
            guard let self else {
                completion(json, status, text)
                return
            }
            if json != nil {
                self.rememberTokenEncoding(first)
                completion(json, status, text)
                return
            }
            // Only a rejection of the request itself is worth re-sending differently. A 5xx or
            // a rate limit says nothing about the encoding.
            guard (400..<500).contains(status), self.preferredTokenEncoding == nil else {
                completion(json, status, text)
                return
            }
            let other: TokenEncoding = first == .form ? .json : .form
            self.send(urlStr, body: body, encoding: other) { retryJSON, retryStatus, retryText in
                if retryJSON != nil { self.rememberTokenEncoding(other) }
                // The first answer is the one reported when both fail: it is the spec-compliant
                // request, so its rejection is the more meaningful one.
                completion(retryJSON, retryJSON != nil ? retryStatus : status,
                           retryJSON != nil ? retryText : text)
            }
        }
    }

    private var preferredTokenEncoding: TokenEncoding? {
        lock.lock()
        defer { lock.unlock() }
        return tokenEncoding
    }

    private func rememberTokenEncoding(_ encoding: TokenEncoding) {
        lock.lock()
        tokenEncoding = encoding
        lock.unlock()
    }

    /// Reports the parsed body on success, plus the status and the raw text either way, so the
    /// caller can decide what a failure means instead of parsing a prose string.
    private func send(_ urlStr: String, body: [String: String], encoding: TokenEncoding,
                      completion: @escaping ([String: Any]?, Int, String) -> Void) {
        guard let url = URL(string: urlStr) else {
            completion(nil, 0, "Bad URL \(urlStr)")
            return
        }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        switch encoding {
        case .form:
            req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            req.httpBody = Data(FormBody.encoded(body).utf8)
        case .json:
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        URLSession.shared.dataTask(with: req) { data, resp, err in
            if let err {
                completion(nil, 0, err.localizedDescription)
                return
            }
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            let text = data.flatMap { String(data: $0, encoding: .utf8) } ?? ""
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (200..<300).contains(status) else {
                completion(nil, status, String(text.prefix(300)))
                return
            }
            completion(json, status, text)
        }.resume()
    }

    private static func randomURLSafe(_ count: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: count)
        _ = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        return Data(bytes).base64URLEncoded()
    }

    private static func challenge(for verifier: String) -> String {
        Data(SHA256.hash(data: Data(verifier.utf8))).base64URLEncoded()
    }
}

extension Data {
    func base64URLEncoded() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
