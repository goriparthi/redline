// App configuration loaded from ~/.config/redline/config.json;
// a default file is written on first launch.
using System.Text.Json.Nodes;

namespace Redline.Core;

public sealed record ModelPrice(double Input, double Output, double CacheRead); // USD per MTok

public sealed class OAuthSettings
{
    // No default client id: this app is not registered with Anthropic
    public string ClientId { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "https://claude.ai/oauth/authorize";
    public string TokenUrl { get; set; } = "https://console.anthropic.com/v1/oauth/token";
    public string UsageUrl { get; set; } = "https://api.anthropic.com/api/oauth/usage";
    public string Scopes { get; set; } = "user:profile";
    public string BetaHeader { get; set; } = "oauth-2025-04-20";
    public int RedirectPort { get; set; } = 54545;
    public bool IsConfigured => ClientId.Length > 0;
    public OAuthSettings Clone() => (OAuthSettings)MemberwiseClone();
}

public sealed class Config
{
    public double PollIntervalSeconds { get; set; } = 300;
    public string MenuBarDisplay { get; set; } = "limits";
    public double LimitYellowPct { get; set; } = 60;
    public double LimitRedPct { get; set; } = 85;
    public List<string> Providers { get; set; } = new() { "Claude", "Codex", "Ollama" };
    // "auto" shows whichever provider is nearest its limit
    public string MenuBarProvider { get; set; } = AutoProvider;
    // Off by default: the Claude CLI's credential belongs to another application
    public bool UseCLIToken { get; set; }
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new(DefaultPricing);
    public OAuthSettings OAuth { get; set; } = new();
    public bool ShowMenuIcon { get; set; } = true;
    public bool ShowResetTimes { get; set; } = true;
    public string LimitWindows { get; set; } = "all";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool StatusChecks { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public string DashboardTheme { get; set; } = "auto";
    public bool Alerts { get; set; } = true;
    public bool RecordHistory { get; set; } = true;
    public bool PublishSidecar { get; set; } = true;
    public string ExternalUsagePath { get; set; } = "";
    public bool FindingsScans { get; set; } = true;
    public bool MindfulCues { get; set; } = true;
    public double StretchMinutes { get; set; } = 90;
    public int LateHour { get; set; } = 23;
    public int StreakDays { get; set; } = 7;
    public int FindingsSnoozeDays { get; set; } = 14;
    public bool AgentFleet { get; set; } = true;
    /// Windows only: start RedLine when you sign in (HKCU Run key).
    public bool LaunchAtLogin { get; set; } = true;
    /// Windows only: Claude's session and week "stacked" in one tray icon, or "split" into two.
    public string TrayLayout { get; set; } = "stacked";

    public Config Clone()
    {
        var c = (Config)MemberwiseClone();
        c.Providers = new List<string>(Providers);
        c.Pricing = new Dictionary<string, ModelPrice>(Pricing);
        c.OAuth = OAuth.Clone();
        return c;
    }

    // Fable/Mythos default to Opus tier as an estimate; override in config if needed
    public static readonly IReadOnlyDictionary<string, ModelPrice> DefaultPricing = new Dictionary<string, ModelPrice>
    {
        ["fable"] = new(15, 75, 1.5),
        ["mythos"] = new(15, 75, 1.5),
        ["opus"] = new(15, 75, 1.5),
        ["sonnet"] = new(3, 15, 0.3),
        ["haiku"] = new(1, 5, 0.1),
    };

    public static readonly string[] MenuBarModes = { "limits", "cost", "tokens", "both", "session" };
    public static readonly string[] KnownProviders = { "Claude", "Codex", "Ollama" };
    public const string AutoProvider = "auto";
    public static string[] MenuBarProviderChoices => new[] { AutoProvider }.Concat(KnownProviders).ToArray();

    public static string ConfigPath => RedlineHome.PathFor(".config/redline/config.json");

    public static bool IsFirstRun(string? path = null) => !File.Exists(path ?? ConfigPath);

    public static Config Load(string? path = null)
    {
        path ??= ConfigPath;
        var cfg = new Config();
        string text;
        try
        {
            if (!File.Exists(path)) { WriteDefault(path); return cfg; }
            text = File.ReadAllText(path);
        }
        catch { return cfg; }
        if (Json.ParseObject(text) is not { } json)
        {
            // Every preference is about to be replaced with a default; that leaves a record
            Diag.Log.Error("config.corrupt", "config did not parse; defaults written over it",
                new() { ["path"] = path, ["bytes"] = text.Length.ToString() });
            WriteDefault(path);
            return cfg;
        }
        return Apply(json, cfg);
    }

    internal static Config Apply(JsonObject json, Config baseCfg)
    {
        var cfg = baseCfg.Clone();
        if (Json.Num(json["pollIntervalSeconds"]) is { } poll && poll >= 10) cfg.PollIntervalSeconds = poll;
        if (Json.Str(json["menuBarDisplay"]) is { } d && MenuBarModes.Contains(d)) cfg.MenuBarDisplay = d;
        if (Json.Num(json["limitYellowPct"]) is { } y && y > 0 && y <= 100) cfg.LimitYellowPct = y;
        if (Json.Num(json["limitRedPct"]) is { } r && r > 0 && r <= 100) cfg.LimitRedPct = r;
        if (Json.Bool(json["useCLIToken"]) is { } b1) cfg.UseCLIToken = b1;
        if (Json.Bool(json["showMenuIcon"]) is { } b2) cfg.ShowMenuIcon = b2;
        if (Json.Bool(json["showResetTimes"]) is { } b3) cfg.ShowResetTimes = b3;
        if (Json.Str(json["limitWindows"]) is { } w && new[] { "all", "session", "week" }.Contains(w)) cfg.LimitWindows = w;
        if (Json.Bool(json["autoCheckUpdates"]) is { } b4) cfg.AutoCheckUpdates = b4;
        if (Json.Bool(json["statusChecks"]) is { } b5) cfg.StatusChecks = b5;
        if (Json.Str(json["updateChannel"]) is { } ch && (ch == "stable" || ch == "beta")) cfg.UpdateChannel = ch;
        if (Json.Str(json["dashboardTheme"]) is { } t && new[] { "auto", "light", "dark" }.Contains(t)) cfg.DashboardTheme = t;
        if (Json.Bool(json["alerts"]) is { } b6) cfg.Alerts = b6;
        if (Json.Bool(json["recordHistory"]) is { } b7) cfg.RecordHistory = b7;
        if (Json.Bool(json["publishSidecar"]) is { } b8) cfg.PublishSidecar = b8;
        if (Json.Bool(json["findingsScans"]) is { } b9) cfg.FindingsScans = b9;
        if (Json.Bool(json["mindfulCues"]) is { } b10) cfg.MindfulCues = b10;
        if (Json.Bool(json["agentFleet"]) is { } b11) cfg.AgentFleet = b11;
        if (Json.Bool(json["launchAtLogin"]) is { } b12) cfg.LaunchAtLogin = b12;
        if (Json.Str(json["trayLayout"]) is { } tl && (tl == "stacked" || tl == "split")) cfg.TrayLayout = tl;
        if (Json.Int(json["findingsSnoozeDays"]) is { } sn && sn >= 1 && sn <= 365) cfg.FindingsSnoozeDays = sn;
        // Clamped rather than rejected: a nonsense value lands on the nearest sane one
        if (Json.Num(json["stretchMinutes"]) is { } sm) cfg.StretchMinutes = Math.Min(600, Math.Max(15, sm));
        if (Json.Num(json["lateHour"]) is { } lh) cfg.LateHour = Math.Min(23, Math.Max(18, (int)lh));
        if (Json.Num(json["streakDays"]) is { } sd) cfg.StreakDays = Math.Min(90, Math.Max(2, (int)sd));
        if (Json.Str(json["externalUsagePath"]) is { } p && (p.Length == 0 || ValidExternalPath(p) is not null))
            cfg.ExternalUsagePath = p;
        if (Json.Strings(json["providers"]) is { Count: > 0 } provs) cfg.Providers = provs;
        if (Json.Str(json["menuBarProvider"]) is { } m &&
            MenuBarProviderChoices.FirstOrDefault(c => string.Equals(c, m, StringComparison.OrdinalIgnoreCase)) is { } match)
            cfg.MenuBarProvider = match;
        if (json["pricingPerMTok"] is JsonObject pricing)
        {
            foreach (var (key, v) in pricing)
            {
                if (v is not JsonObject o) continue;
                if (Json.Num(o["input"]) is not { } i || Json.Num(o["output"]) is not { } op ||
                    Json.Num(o["cacheRead"]) is not { } cr) continue;
                cfg.Pricing[key.ToLowerInvariant()] = new ModelPrice(i, op, cr);
            }
        }
        if (json["oauth"] is JsonObject oa)
        {
            // OAuth endpoint overrides must be https so tokens never travel in cleartext
            if (Json.Str(oa["clientId"]) is { } id) cfg.OAuth.ClientId = id;
            if (HttpsUrl(oa["authorizeUrl"]) is { } au) cfg.OAuth.AuthorizeUrl = au;
            if (HttpsUrl(oa["tokenUrl"]) is { } tu) cfg.OAuth.TokenUrl = tu;
            if (HttpsUrl(oa["usageUrl"]) is { } uu) cfg.OAuth.UsageUrl = uu;
            if (Json.Str(oa["scopes"]) is { } sc) cfg.OAuth.Scopes = sc;
            if (Json.Str(oa["betaHeader"]) is { } bh) cfg.OAuth.BetaHeader = bh;
            if (Json.Num(oa["redirectPort"]) is { } port && port > 0 && port < 65536) cfg.OAuth.RedirectPort = (int)port;
        }
        return cfg;
    }

    /// An external sidecar path must be absolute and name a .json file.
    public static string? ValidExternalPath(string raw) => Sidecar.ValidExternalPath(raw);

    internal static string? HttpsUrl(JsonNode? v) =>
        Json.Str(v) is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u) &&
        u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? s : null;

    public static void WriteDefault(string path)
    {
        var cfg = new Config();
        var pricing = new JsonObject();
        foreach (var (k, v) in DefaultPricing.OrderBy(kv => kv.Key))
            pricing[k] = new JsonObject { ["cacheRead"] = v.CacheRead, ["input"] = v.Input, ["output"] = v.Output };
        var dict = new JsonObject
        {
            ["_notes"] = "pollIntervalSeconds min 10. menuBarDisplay: limits | cost | tokens | both | session. providers selects which sources are read: Claude, Codex, Ollama. menuBarProvider picks which one the tray reports: auto (whichever is nearest its limit) or a single provider name. useCLIToken is off by default; setting it true lets RedLine read (never refresh) the Claude CLI's own credential instead of signing in separately. The usage feed needs neither and is the recommended route. Pricing keys match by substring of model name; cache writes billed at 1.25x (5m) and 2x (1h) of the input rate. Models with no pricing key are counted but left out of cost. oauth.clientId is empty by default and Sign In stays disabled until you set one; oauth URLs must be https. updateChannel: stable | beta. alerts posts a notification when a window crosses a threshold or resets. recordHistory keeps a daily rollup under ~/.local/share/redline/history. publishSidecar writes the current windows to ~/.local/share/redline/usage-snapshot.json. externalUsagePath reads another tool's sidecar as a fallback; it must be an absolute path to a .json file. findingsScans looks through transcripts for setup findings in the background. mindfulCues says how the work is spread out and never makes a sound. agentFleet lists the Claude Code sessions running on this PC. findingsSnoozeDays is 1 to 365. launchAtLogin starts RedLine when you sign in to Windows. trayLayout: stacked (Claude's session over week in one tray icon) | split (two icons, session and week).",
            ["agentFleet"] = cfg.AgentFleet,
            ["alerts"] = cfg.Alerts,
            ["externalUsagePath"] = cfg.ExternalUsagePath,
            ["findingsScans"] = cfg.FindingsScans,
            ["findingsSnoozeDays"] = cfg.FindingsSnoozeDays,
            ["lateHour"] = cfg.LateHour,
            ["launchAtLogin"] = cfg.LaunchAtLogin,
            ["limitRedPct"] = 85,
            ["limitYellowPct"] = 60,
            ["menuBarDisplay"] = "limits",
            ["menuBarProvider"] = cfg.MenuBarProvider,
            ["mindfulCues"] = cfg.MindfulCues,
            ["oauth"] = new JsonObject
            {
                ["authorizeUrl"] = cfg.OAuth.AuthorizeUrl,
                ["betaHeader"] = cfg.OAuth.BetaHeader,
                ["clientId"] = cfg.OAuth.ClientId,
                ["redirectPort"] = cfg.OAuth.RedirectPort,
                ["scopes"] = cfg.OAuth.Scopes,
                ["tokenUrl"] = cfg.OAuth.TokenUrl,
                ["usageUrl"] = cfg.OAuth.UsageUrl,
            },
            ["pollIntervalSeconds"] = 300,
            ["pricingPerMTok"] = pricing,
            ["providers"] = new JsonArray(cfg.Providers.Select(p => (JsonNode)p).ToArray()),
            ["publishSidecar"] = cfg.PublishSidecar,
            ["recordHistory"] = cfg.RecordHistory,
            ["streakDays"] = cfg.StreakDays,
            ["trayLayout"] = cfg.TrayLayout,
            ["stretchMinutes"] = cfg.StretchMinutes,
            ["updateChannel"] = cfg.UpdateChannel,
            ["useCLIToken"] = cfg.UseCLIToken,
        };
        // A config that silently fails to save is the worst kind of bug
        Diag.Log.Attempt("config.write_failed", new() { ["path"] = path },
            () => Json.WriteAtomic(path, dict.ToJsonString(Json.Pretty)));
    }

    // Returns null for an unpriced model so cost stays honest instead of guessing a tier
    public ModelPrice? Price(string model)
    {
        var m = model.ToLowerInvariant();
        foreach (var (key, p) in Pricing.OrderByDescending(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            if (m.Contains(key)) return p;
        return null;
    }

    public bool Wants(string provider) =>
        Providers.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));

    public static bool SetProviders(IEnumerable<string> providers, string? path = null) =>
        Write(new() { ["providers"] = new JsonArray(providers.Select(p => (JsonNode)p).ToArray()) }, path);

    public static bool SetMenuBarProvider(string provider, string? path = null) =>
        Write(new() { ["menuBarProvider"] = provider }, path);

    /// Patches oauth.clientId in place, preserving the rest of the oauth block.
    public static bool SetOAuthClientId(string id, string? path = null)
    {
        path ??= ConfigPath;
        var oauth = (Json.ReadObject(path)?["oauth"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        oauth["clientId"] = id;
        return Write(new() { ["oauth"] = oauth }, path);
    }

    // Rewrites only the given keys so hand-edited notes and pricing survive
    public static bool Write(Dictionary<string, JsonNode?> values, string? path = null)
    {
        path ??= ConfigPath;
        var json = Json.ReadObject(path) ?? new JsonObject();
        foreach (var (k, v) in values) json[k] = v?.DeepClone();
        var sorted = new JsonObject();
        foreach (var kv in json.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[kv.Key] = kv.Value?.DeepClone();
        try { Json.WriteAtomic(path, sorted.ToJsonString(Json.Pretty)); return true; }
        catch { return false; }
    }
}
