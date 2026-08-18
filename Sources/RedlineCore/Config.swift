// App configuration loaded from ~/.config/redline/config.json;
// a default file is written on first launch.
import Foundation

public struct ModelPrice: Equatable {
    public let input: Double      // USD per MTok
    public let output: Double
    public let cacheRead: Double

    public init(input: Double, output: Double, cacheRead: Double) {
        self.input = input
        self.output = output
        self.cacheRead = cacheRead
    }
}

public struct OAuthSettings {
    // No default client id: this app is not registered with Anthropic, so borrowing one
    // is the user's deliberate choice. Sign In stays disabled until it is set.
    public var clientId = ""
    public var authorizeUrl = "https://claude.ai/oauth/authorize"
    public var tokenUrl = "https://console.anthropic.com/v1/oauth/token"
    public var usageUrl = "https://api.anthropic.com/api/oauth/usage"
    public var scopes = "user:profile"
    public var betaHeader = "oauth-2025-04-20"
    public var redirectPort: UInt16 = 54545

    public var isConfigured: Bool { !clientId.isEmpty }
}

public struct Config {
    public var pollIntervalSeconds: Double = 300
    public var menuBarDisplay: String = "limits"
    public var limitYellowPct: Double = 60
    public var limitRedPct: Double = 85
    public var providers: [String] = ["Claude", "Codex", "Ollama"]
    // Which provider drives the menu bar readout. "auto" shows whichever is nearest its
    // limit, which is the one that will interrupt you first.
    public var menuBarProvider: String = Config.autoProvider
    // Off by default: reading the Claude CLI's Keychain item is another application's
    // credential, so it is the user's explicit choice, never a silent default.
    public var useCLIToken = false
    public var pricing: [String: ModelPrice] = Config.defaultPricing
    public var oauth = OAuthSettings()

    // Menu bar style, all pick-and-choose so the readout can be as compact as one number
    public var showMenuIcon = true
    public var showResetTimes = true
    /// Which limit windows the menu bar and dropdown report: "all", "session", or "week"
    public var limitWindows = "all"
    // On by default, and the one network request RedLine makes without being asked: an app
    // that installs updates in place is only safe if it learns about them. One call a day to
    // the GitHub releases API, nothing else, and turning it off is one click in the menu.
    public var autoCheckUpdates = true
    // Also off by default, same reasoning: polls the providers' public status feeds
    public var statusChecks = false
    /// Dashboard appearance: "auto" follows the OS, "light" and "dark" force it
    public var dashboardTheme = "auto"

    public init() {}

    // Fable/Mythos default to Opus tier as an estimate; override in config if needed
    public static let defaultPricing: [String: ModelPrice] = [
        "fable":  ModelPrice(input: 15, output: 75, cacheRead: 1.5),
        "mythos": ModelPrice(input: 15, output: 75, cacheRead: 1.5),
        "opus":   ModelPrice(input: 15, output: 75, cacheRead: 1.5),
        "sonnet": ModelPrice(input: 3,  output: 15, cacheRead: 0.3),
        "haiku":  ModelPrice(input: 1,  output: 5,  cacheRead: 0.1),
    ]

    public static let menuBarModes = ["limits", "cost", "tokens", "both", "session"]

    public static var configURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/redline/config.json")
    }

    /// True before the first launch has written a config. Used to offer the setup screen
    /// once, rather than nagging on every start.
    public static func isFirstRun(at url: URL? = nil) -> Bool {
        !FileManager.default.fileExists(atPath: (url ?? configURL).path)
    }

    public static func load(from url: URL? = nil) -> Config {
        let url = url ?? configURL
        let cfg = Config()
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            writeDefault(to: url)
            return cfg
        }
        return apply(json, to: cfg)
    }

    static func apply(_ json: [String: Any], to base: Config) -> Config {
        var cfg = base
        if let n = num(json["pollIntervalSeconds"]), n >= 10 { cfg.pollIntervalSeconds = n }
        if let d = json["menuBarDisplay"] as? String,
           menuBarModes.contains(d) { cfg.menuBarDisplay = d }
        if let n = num(json["limitYellowPct"]), n > 0, n <= 100 { cfg.limitYellowPct = n }
        if let n = num(json["limitRedPct"]), n > 0, n <= 100 { cfg.limitRedPct = n }
        if let b = json["useCLIToken"] as? Bool { cfg.useCLIToken = b }
        if let b = json["showMenuIcon"] as? Bool { cfg.showMenuIcon = b }
        if let b = json["showResetTimes"] as? Bool { cfg.showResetTimes = b }
        if let w = json["limitWindows"] as? String,
           ["all", "session", "week"].contains(w) { cfg.limitWindows = w }
        if let b = json["autoCheckUpdates"] as? Bool { cfg.autoCheckUpdates = b }
        if let b = json["statusChecks"] as? Bool { cfg.statusChecks = b }
        if let t = json["dashboardTheme"] as? String,
           ["auto", "light", "dark"].contains(t) { cfg.dashboardTheme = t }
        if let p = json["providers"] as? [String], !p.isEmpty { cfg.providers = p }
        if let m = json["menuBarProvider"] as? String,
           let match = menuBarProviderChoices.first(where: {
               $0.caseInsensitiveCompare(m) == .orderedSame
           }) {
            cfg.menuBarProvider = match
        }
        if let p = json["pricingPerMTok"] as? [String: [String: Any]] {
            for (key, v) in p {
                guard let i = num(v["input"]), let o = num(v["output"]),
                      let r = num(v["cacheRead"]) else { continue }
                cfg.pricing[key.lowercased()] = ModelPrice(input: i, output: o, cacheRead: r)
            }
        }
        if let o = json["oauth"] as? [String: Any] {
            // OAuth endpoint overrides must be https so tokens never travel in cleartext
            if let s = o["clientId"] as? String { cfg.oauth.clientId = s }
            if let s = httpsURL(o["authorizeUrl"]) { cfg.oauth.authorizeUrl = s }
            if let s = httpsURL(o["tokenUrl"]) { cfg.oauth.tokenUrl = s }
            if let s = httpsURL(o["usageUrl"]) { cfg.oauth.usageUrl = s }
            if let s = o["scopes"] as? String { cfg.oauth.scopes = s }
            if let s = o["betaHeader"] as? String { cfg.oauth.betaHeader = s }
            if let n = num(o["redirectPort"]), n > 0, n < 65536 {
                cfg.oauth.redirectPort = UInt16(n)
            }
        }
        return cfg
    }

    static func num(_ v: Any?) -> Double? {
        if let d = v as? Double { return d }
        if let i = v as? Int { return Double(i) }
        return nil
    }

    static func httpsURL(_ v: Any?) -> String? {
        guard let s = v as? String, let u = URL(string: s),
              u.scheme?.lowercased() == "https" else { return nil }
        return s
    }

    static func writeDefault(to url: URL) {
        let cfg = Config()
        let dict: [String: Any] = [
            "_notes": "pollIntervalSeconds min 10. menuBarDisplay: limits | cost | tokens | both | session. providers selects which sources are read: Claude, Codex, Ollama. menuBarProvider picks which one the menu bar reports: auto (whichever is nearest its limit) or a single provider name. useCLIToken is off by default; setting it true lets RedLine read (never refresh) the Claude CLI's own Keychain token instead of signing in separately. The usage feed needs neither and is the recommended route. Pricing keys match by substring of model name; cache writes billed at 1.25x (5m) and 2x (1h) of the input rate. Models with no pricing key are counted but left out of cost. oauth.clientId is empty by default and Sign In stays disabled until you set one; oauth URLs must be https.",
            "pollIntervalSeconds": 300,
            "menuBarDisplay": "limits",
            "limitYellowPct": 60,
            "limitRedPct": 85,
            "providers": cfg.providers,
            "menuBarProvider": cfg.menuBarProvider,
            "useCLIToken": cfg.useCLIToken,
            "pricingPerMTok": defaultPricing.mapValues {
                ["input": $0.input, "output": $0.output, "cacheRead": $0.cacheRead]
            },
            "oauth": [
                "clientId": cfg.oauth.clientId,
                "authorizeUrl": cfg.oauth.authorizeUrl,
                "tokenUrl": cfg.oauth.tokenUrl,
                "usageUrl": cfg.oauth.usageUrl,
                "scopes": cfg.oauth.scopes,
                "betaHeader": cfg.oauth.betaHeader,
                "redirectPort": Int(cfg.oauth.redirectPort),
            ],
        ]
        try? FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let data = try? JSONSerialization.data(
            withJSONObject: dict, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: url)
        }
    }

    // Returns nil for an unpriced model so cost stays honest instead of guessing a tier
    public func price(for model: String) -> ModelPrice? {
        let m = model.lowercased()
        for (key, p) in pricing where m.contains(key) { return p }
        return nil
    }

    public func wants(_ provider: String) -> Bool {
        providers.contains { $0.caseInsensitiveCompare(provider) == .orderedSame }
    }

    public static let knownProviders = ["Claude", "Codex", "Ollama"]
    public static let autoProvider = "auto"
    public static var menuBarProviderChoices: [String] { [autoProvider] + knownProviders }

    @discardableResult
    public static func setProviders(_ providers: [String], at url: URL? = nil) -> Bool {
        write(["providers": providers], at: url)
    }

    @discardableResult
    public static func setMenuBarProvider(_ provider: String, at url: URL? = nil) -> Bool {
        write(["menuBarProvider": provider], at: url)
    }

    // Rewrites only the given keys so hand-edited notes and pricing survive
    @discardableResult
    /// Patches oauth.clientId in place, preserving the rest of the oauth block. write()
    /// merges at the top level only, so a plain write would drop the sibling URLs.
    public static func setOAuthClientId(_ id: String, at url: URL? = nil) -> Bool {
        let url = url ?? configURL
        var oauth: [String: Any] = [:]
        if let data = try? Data(contentsOf: url),
           let existing = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let block = existing["oauth"] as? [String: Any] {
            oauth = block
        }
        oauth["clientId"] = id
        return write(["oauth": oauth], at: url)
    }

    public static func write(_ values: [String: Any], at url: URL? = nil) -> Bool {
        let url = url ?? configURL
        var json: [String: Any] = [:]
        if let data = try? Data(contentsOf: url),
           let existing = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            json = existing
        }
        for (k, v) in values { json[k] = v }
        guard let out = try? JSONSerialization.data(
            withJSONObject: json, options: [.prettyPrinted, .sortedKeys]) else { return false }
        do {
            try FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try out.write(to: url)
            return true
        } catch {
            return false
        }
    }
}
