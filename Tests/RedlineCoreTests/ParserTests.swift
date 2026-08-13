// These pin three undocumented on-disk formats. If a vendor changes a shape, a test here
// should fail rather than the menu bar silently reporting zero.
import XCTest
@testable import RedlineCore

final class LimitParserTests: XCTestCase {
    func testClaudeUsageParsesNestedWindows() {
        let json: [String: Any] = [
            "five_hour": ["utilization": 12.5, "resets_at": "2026-08-12T21:00:00Z"],
            "seven_day": ["utilization": 41, "resets_at": "2026-08-14T09:30:00.000Z"],
        ]
        let out = LimitParser.claudeUsage(json)
        XCTAssertEqual(out.count, 2)
        XCTAssertEqual(out[0].key, "five_hour")
        XCTAssertEqual(out[0].utilization, 12.5)
        XCTAssertEqual(out[0].provider, "Claude")
        XCTAssertNotNil(out[0].resetsAt)
        // Integer utilization must parse too; the endpoint is inconsistent about it
        XCTAssertEqual(out[1].utilization, 41)
        XCTAssertNotNil(out[1].resetsAt, "fractional-seconds ISO8601 must parse")
    }

    func testClaudeUsageFindsWindowsOneLevelDown() {
        let json: [String: Any] = ["limits": ["five_hour": ["utilization": 5]]]
        XCTAssertEqual(LimitParser.claudeUsage(json).first?.key, "five_hour")
    }

    func testClaudeUsageIgnoresUnrelatedKeys() {
        let json: [String: Any] = ["account": ["email": "x@y.z"], "count": 3]
        XCTAssertTrue(LimitParser.claudeUsage(json).isEmpty)
    }

    func testCodexRateLimitsMapWindowsAndEpochSeconds() {
        let rl: [String: Any] = [
            "primary": ["used_percent": 3.0, "window_minutes": 300, "resets_at": 1771462627],
            "secondary": ["used_percent": 1.0, "window_minutes": 10080,
                          "resets_at": 1772049427],
        ]
        let out = LimitParser.codexRateLimits(rl)
        XCTAssertEqual(out.map(\.key), ["five_hour", "seven_day"])
        XCTAssertEqual(out.map(\.provider), ["Codex", "Codex"])
        XCTAssertEqual(out[0].resetsAt, Date(timeIntervalSince1970: 1771462627))
        XCTAssertEqual(out[1].utilization, 1.0)
    }

    func testCodexUnknownWindowIsKeptNotDropped() {
        let rl: [String: Any] = ["primary": ["used_percent": 9, "window_minutes": 60]]
        XCTAssertEqual(LimitParser.codexRateLimits(rl).first?.key, "window_60m")
        XCTAssertEqual(LimitParser.key(forWindowMinutes: 4320), "window_3d")
    }

    func testCodexSkipsSlotWithoutPercent() {
        let rl: [String: Any] = ["primary": ["window_minutes": 300]]
        XCTAssertTrue(LimitParser.codexRateLimits(rl).isEmpty)
    }

    func testSortPutsSessionBeforeWeek() {
        let w = [
            LimitWindow(provider: "C", key: "seven_day", utilization: 1, resetsAt: nil),
            LimitWindow(provider: "C", key: "five_hour", utilization: 2, resetsAt: nil),
        ]
        XCTAssertEqual(LimitParser.sorted(w).map(\.key), ["five_hour", "seven_day"])
    }
}

final class CredentialScanTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_700_000_000)

    func testFindsNestedTokenRegardlessOfKeyPath() {
        let json: [String: Any] = [
            "someOauthBlock": [
                "accessToken": "tok-abc",
                "expiresAt": (1_700_000_000 + 3600) * 1000,
            ]
        ]
        XCTAssertEqual(CredentialScan.accessToken(in: json, now: now), "tok-abc")
    }

    func testRejectsExpiredToken() {
        let json: [String: Any] = [
            "o": ["accessToken": "tok", "expiresAt": (1_700_000_000 - 10) * 1000]
        ]
        XCTAssertNil(CredentialScan.accessToken(in: json, now: now))
    }

    func testRejectsTokenInsideExpiryMargin() {
        let json: [String: Any] = [
            "o": ["accessToken": "tok", "expiresAt": (1_700_000_000 + 30) * 1000]
        ]
        XCTAssertNil(CredentialScan.accessToken(in: json, now: now),
                     "a token expiring inside the margin must not be used")
    }

    func testMissingExpiryTreatedAsNonExpiring() {
        XCTAssertEqual(CredentialScan.accessToken(in: ["accessToken": "t"], now: now), "t")
    }

    func testEmptyOrAbsentTokenIsNil() {
        XCTAssertNil(CredentialScan.accessToken(in: ["accessToken": ""], now: now))
        XCTAssertNil(CredentialScan.accessToken(in: ["refreshToken": "r"], now: now))
    }

    func testDoesNotRecurseForever() {
        // Deeper than the scan depth: must return nil rather than hang or crash
        let deep: [String: Any] = ["a": ["b": ["c": ["d": ["accessToken": "too-deep"]]]]]
        XCTAssertNil(CredentialScan.accessToken(in: deep, now: now))
    }
}

final class AggregateTests: XCTestCase {
    private let t = Date(timeIntervalSince1970: 1_700_000_000)

    private func entry(_ provider: String, _ model: String,
                       input: Int = 0, output: Int = 0, cacheRead: Int = 0,
                       c5m: Int = 0, c1h: Int = 0) -> Entry {
        Entry(provider: provider, key: nil, ts: t, model: model, input: input,
              output: output, cacheRead: cacheRead, cache5m: c5m, cache1h: c1h)
    }

    func testCostUsesInputOutputAndCacheMultipliers() {
        var cfg = Config()
        cfg.pricing = ["sonnet": ModelPrice(input: 3, output: 15, cacheRead: 0.3)]
        let e = entry("Claude", "claude-sonnet-5", input: 1_000_000, output: 1_000_000,
                      cacheRead: 1_000_000, c5m: 1_000_000, c1h: 1_000_000)
        let a = aggregate([e], since: t, config: cfg)
        // 3 + 15 + 0.3 + (3 * 1.25) + (3 * 2) = 28.05
        XCTAssertEqual(a.cost, 28.05, accuracy: 0.0001)
        XCTAssertEqual(a.io, 2_000_000, "io counts input+output only")
        XCTAssertEqual(a.cacheWrite, 2_000_000)
    }

    func testUnpricedModelIsCountedButNotCostedAndIsFlagged() {
        var cfg = Config()
        cfg.pricing = ["sonnet": ModelPrice(input: 3, output: 15, cacheRead: 0.3)]
        let a = aggregate([entry("Codex", "gpt-5.3-codex", input: 1000, output: 10)],
                          since: t, config: cfg)
        XCTAssertEqual(a.io, 1010)
        XCTAssertEqual(a.cost, 0, "guessing a price tier would misreport spend")
        XCTAssertTrue(a.hasUnpriced)
    }

    func testGroupsByProviderAndModel() {
        var cfg = Config()
        cfg.pricing = ["sonnet": ModelPrice(input: 3, output: 0, cacheRead: 0)]
        let a = aggregate([entry("Claude", "claude-sonnet-5", input: 1_000_000),
                           entry("Codex", "gpt-5.3-codex", input: 500)],
                          since: t, config: cfg)
        XCTAssertEqual(a.providers["Claude"]?.io, 1_000_000)
        XCTAssertEqual(a.providers["Codex"]?.io, 500)
        // Models are reachable only through their own provider now
        XCTAssertEqual(a.providers["Claude"]?.models["claude-sonnet-5"]?.cost ?? 0, 3,
                       accuracy: 0.0001)
        XCTAssertNil(a.providers["Claude"]?.models["gpt-5.3-codex"])
    }

    func testEntriesBeforeSinceAreExcluded() {
        let old = Entry(provider: "Claude", key: nil, ts: t.addingTimeInterval(-10),
                        model: "claude-sonnet-5", input: 5, output: 5,
                        cacheRead: 0, cache5m: 0, cache1h: 0)
        XCTAssertEqual(aggregate([old], since: t, config: Config()).io, 0)
    }

    func testPriceMatchesBySubstringAndReturnsNilOtherwise() {
        let cfg = Config()
        XCTAssertEqual(cfg.price(for: "claude-opus-5")?.input, 15)
        XCTAssertEqual(cfg.price(for: "claude-haiku-4-5-20251001")?.input, 1)
        XCTAssertNil(cfg.price(for: "some-unknown-model"))
    }
}

final class FormatTests: XCTestCase {
    func testTokenFormatting() {
        XCTAssertEqual(fmtTokens(999), "999")
        XCTAssertEqual(fmtTokens(1_500), "1.5K")
        XCTAssertEqual(fmtTokens(5_728_245), "5.7M")
        XCTAssertEqual(fmtTokens(3_682_642_412), "3.7B")
    }

    func testCostFormatting() {
        XCTAssertEqual(fmtCost(0), "$0.00")
        XCTAssertEqual(fmtCost(6734.061), "$6734.06")
    }
}

final class ConfigTests: XCTestCase {
    func testNoDefaultClientIdSoSignInStaysDisabled() {
        XCTAssertFalse(Config().oauth.isConfigured,
                       "shipping a borrowed client id by default is not ours to do")
    }

    func testRejectsNonHttpsOAuthOverrides() {
        let json: [String: Any] = ["oauth": ["tokenUrl": "http://evil.example/token"]]
        let cfg = Config.apply(json, to: Config())
        XCTAssertEqual(cfg.oauth.tokenUrl, "https://console.anthropic.com/v1/oauth/token")
    }

    func testRejectsOutOfRangeAndUnknownValues() {
        let json: [String: Any] = [
            "pollIntervalSeconds": 2,
            "menuBarDisplay": "bogus",
            "limitRedPct": 900,
        ]
        let cfg = Config.apply(json, to: Config())
        XCTAssertEqual(cfg.pollIntervalSeconds, 300)
        XCTAssertEqual(cfg.menuBarDisplay, "limits")
        XCTAssertEqual(cfg.limitRedPct, 85)
    }

    func testProviderSelection() {
        let cfg = Config.apply(["providers": ["Claude"]], to: Config())
        XCTAssertTrue(cfg.wants("claude"), "provider match must be case-insensitive")
        XCTAssertFalse(cfg.wants("Codex"))
    }

    func testEmptyProviderListIsIgnored() {
        XCTAssertTrue(Config.apply(["providers": []], to: Config()).wants("Claude"))
    }
}

final class WindowRecognitionTests: XCTestCase {
    private func w(_ key: String, _ util: Double, resets: Date? = nil,
                   provider: String = "Claude") -> LimitWindow {
        LimitWindow(provider: provider, key: key, utilization: util, resetsAt: resets)
    }

    func testKnownWindowsAreRecognized() {
        XCTAssertTrue(w("five_hour", 1).isRecognized)
        XCTAssertTrue(w("seven_day", 1).isRecognized)
        XCTAssertTrue(w("seven_day_opus", 1).isRecognized)
    }

    // Claude has returned internal codenames such as nimbus_quill from the usage endpoint
    func testUndocumentedCodenameIsUnrecognized() {
        XCTAssertFalse(w("nimbus_quill", 0).isRecognized)
        XCTAssertEqual(w("nimbus_quill", 0).displayName, "nimbus quill")
    }

    func testEmptyUnnamedWindowIsUninformative() {
        XCTAssertTrue(w("nimbus_quill", 0).isUninformative)
    }

    func testUnnamedWindowWithUsageIsKept() {
        XCTAssertFalse(w("nimbus_quill", 4).isUninformative,
                       "a window actually being consumed is news, even unnamed")
    }

    func testUnnamedWindowWithResetTimeIsKept() {
        XCTAssertFalse(w("nimbus_quill", 0, resets: Date()).isUninformative)
    }

    func testKnownWindowAtZeroIsNeverHidden() {
        XCTAssertFalse(w("five_hour", 0).isUninformative)
    }

    func testWeekLabelDropsModelQualifierForOtherProviders() {
        XCTAssertEqual(w("seven_day", 1).displayName, "Week (all models)")
        XCTAssertEqual(w("seven_day", 1, provider: "Codex").displayName, "Week",
                       "only Claude splits the week by model")
    }
}

final class MenuBarProviderTests: XCTestCase {
    func testDefaultsToAuto() {
        XCTAssertEqual(Config().menuBarProvider, Config.autoProvider)
    }

    func testAcceptsKnownProviderCaseInsensitivelyAndCanonicalises() {
        let cfg = Config.apply(["menuBarProvider": "codex"], to: Config())
        XCTAssertEqual(cfg.menuBarProvider, "Codex", "stored value should be canonical")
    }

    func testRejectsUnknownProviderAndKeepsAuto() {
        let cfg = Config.apply(["menuBarProvider": "gemini"], to: Config())
        XCTAssertEqual(cfg.menuBarProvider, Config.autoProvider)
    }

    func testAutoIsAValidChoice() {
        XCTAssertEqual(Config.apply(["menuBarProvider": "auto"], to: Config()).menuBarProvider,
                       Config.autoProvider)
        XCTAssertEqual(Config.menuBarProviderChoices.count, 4)
    }

    func testWriteMergesWithoutDroppingOtherKeys() throws {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-cfg-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: url) }
        try Data(#"{"pollIntervalSeconds":600,"providers":["Codex"]}"#.utf8).write(to: url)

        XCTAssertTrue(Config.setMenuBarProvider("Codex", at: url))
        let cfg = Config.load(from: url)
        XCTAssertEqual(cfg.menuBarProvider, "Codex")
        XCTAssertEqual(cfg.pollIntervalSeconds, 600, "unrelated keys must survive the write")
        XCTAssertFalse(cfg.wants("Claude"))
    }
}

final class CredentialPolicyTests: XCTestCase {
    func testBorrowingTheCLITokenIsOptIn() {
        XCTAssertFalse(Config().useCLIToken,
                       "reading another app's Keychain item must never be a silent default")
    }

    func testExplicitOptInIsHonoured() {
        XCTAssertTrue(Config.apply(["useCLIToken": true], to: Config()).useCLIToken)
    }

    func testNoClientIdShipsByDefault() {
        XCTAssertFalse(Config().oauth.isConfigured)
    }
}
