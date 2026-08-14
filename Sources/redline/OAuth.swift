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
        guard let data = try? JSONEncoder().encode(self) else { return false }
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
        return try? JSONDecoder().decode(TokenStore.self, from: data)
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

// Reads the CLI's own credential item. A background LSUIElement agent is denied silently
// rather than prompted, so a failure here is expected until access is granted once.
enum CLICredentials {
    static let service = "Claude Code-credentials"

    // Can block on a Keychain consent prompt, so never call from the main thread
    static func accessToken() -> String? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return CredentialScan.accessToken(in: json)
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
    private var cliProbe: (token: String?, at: Date)?
    private var cliRejected = false
    // The usage endpoint rate-limits hard and stays limited, so back off rather than
    // hammering it every poll. See anthropics/claude-code issues 31021 and 31637.
    private var backoffUntil: Date?
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
        cliRejected = false
        lock.unlock()
    }

    // Reads only the cached probe; the Keychain itself is touched on probeQueue
    var isSignedIn: Bool { cachedCLIToken() != nil || TokenStore.load() != nil }

    // Sign In is only offered when a client id has been configured
    var canSignIn: Bool { settings.isConfigured }

    var usingCLIToken: Bool { cachedCLIToken() != nil }

    private var redirectUri: String { "http://localhost:\(settings.redirectPort)/callback" }

    private func cachedCLIToken() -> String? {
        lock.lock()
        defer { lock.unlock() }
        return cliRejected ? nil : cliProbe?.token
    }

    // Caches the miss as well as the hit so a denied prompt is not re-asked on every poll
    private func refreshCLIProbe() {
        guard useCLIToken else { return }
        lock.lock()
        let have = cliProbe != nil
        let rejected = cliRejected
        lock.unlock()
        guard !rejected, !have else { return }
        // One Keychain read, kept until something invalidates it. A time-based re-read
        // fired the macOS consent prompt on every menu open for anyone who clicked plain
        // Allow, which grants a single read.
        let token = CLICredentials.accessToken()
        lock.lock()
        cliProbe = (token, Date())
        lock.unlock()
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
        let delay = min(300 * pow(2, Double(consecutive429 - 1)), 1800)
        backoffUntil = Date().addingTimeInterval(delay)
        lock.unlock()
        return "Usage temporarily unavailable"
    }

    private func clearRateLimit() {
        lock.lock()
        consecutive429 = 0
        backoffUntil = nil
        lock.unlock()
    }

    private func rejectCLIToken() {
        lock.lock()
        cliRejected = true
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
        postJSON(settings.tokenUrl, body: [
            "grant_type": "authorization_code",
            "code": code,
            "state": state,
            "client_id": settings.clientId,
            "redirect_uri": redirectUri,
            "code_verifier": verifier,
        ]) { json, err in
            guard let json, let access = json["access_token"] as? String else {
                completion("Token exchange failed: \(err ?? "no access_token")")
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
        guard let store = TokenStore.load() else {
            completion(nil, settings.isConfigured ? "Not signed in"
                                                 : "No Claude token available")
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
        postJSON(settings.tokenUrl, body: [
            "grant_type": "refresh_token",
            "refresh_token": refresh,
            "client_id": settings.clientId,
        ]) { json, err in
            guard let json, let access = json["access_token"] as? String else {
                // A rejected grant is terminal, so drop the dead token; otherwise the app
                // stays "signed in" and retries it forever instead of offering Sign In.
                if let err, err.contains("invalid_grant") {
                    TokenStore.clear()
                    completion(nil, "Sign-in expired; sign in again")
                    return
                }
                completion(nil, "Token refresh failed: \(err ?? "no access_token")")
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
                    // The CLI rotates its token underneath us, so a refusal usually just
                    // means ours is stale: read the Keychain once more before giving up
                    // on it. If the fresh token is refused too, the next pass falls back
                    // to this app's own grant via the rejection latch.
                    if usingCLI, status == 401 || status == 403 {
                        if allowRetry {
                            self.resetCLIProbe()
                            self.refreshCLIProbe()
                        } else {
                            self.rejectCLIToken()
                        }
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

    private func postJSON(_ urlStr: String, body: [String: Any],
                          completion: @escaping ([String: Any]?, String?) -> Void) {
        guard let url = URL(string: urlStr) else {
            completion(nil, "Bad URL \(urlStr)")
            return
        }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        URLSession.shared.dataTask(with: req) { data, resp, err in
            if let err {
                completion(nil, err.localizedDescription)
                return
            }
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (200..<300).contains(status) else {
                let snippet = data.flatMap { String(data: $0, encoding: .utf8) }?.prefix(160) ?? ""
                completion(nil, "HTTP \(status): \(snippet)")
                return
            }
            completion(json, nil)
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
