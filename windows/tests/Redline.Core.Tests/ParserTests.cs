// These pin three undocumented on-disk formats. If a vendor changes a shape, a test here
// should fail rather than the tray silently reporting zero.
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class LimitParserTests
{
    [Fact]
    public void ClaudeUsageParsesNestedWindows()
    {
        var json = new JsonObject
        {
            ["five_hour"] = new JsonObject { ["utilization"] = 12.5, ["resets_at"] = "2026-08-12T21:00:00Z" },
            ["seven_day"] = new JsonObject { ["utilization"] = 41, ["resets_at"] = "2026-08-14T09:30:00.000Z" },
        };
        var output = LimitParser.ClaudeUsage(json);
        Assert.Equal(2, output.Count);
        Assert.Equal("five_hour", output[0].Key);
        Assert.Equal(12.5, output[0].Utilization);
        Assert.Equal("Claude", output[0].Provider);
        Assert.NotNull(output[0].ResetsAt);
        // Integer utilization must parse too; the endpoint is inconsistent about it
        Assert.Equal(41.0, output[1].Utilization);
        Assert.NotNull(output[1].ResetsAt); // fractional-seconds ISO8601 must parse
    }

    [Fact]
    public void ClaudeUsageFindsWindowsOneLevelDown()
    {
        var json = new JsonObject { ["limits"] = new JsonObject { ["five_hour"] = new JsonObject { ["utilization"] = 5 } } };
        Assert.Equal("five_hour", LimitParser.ClaudeUsage(json).FirstOrDefault()?.Key);
    }

    [Fact]
    public void ClaudeUsageIgnoresUnrelatedKeys()
    {
        var json = new JsonObject { ["account"] = new JsonObject { ["email"] = "x@y.z" }, ["count"] = 3 };
        Assert.Empty(LimitParser.ClaudeUsage(json));
    }

    [Fact]
    public void CodexRateLimitsMapWindowsAndEpochSeconds()
    {
        var rl = new JsonObject
        {
            ["primary"] = new JsonObject { ["used_percent"] = 3.0, ["window_minutes"] = 300, ["resets_at"] = 1771462627 },
            ["secondary"] = new JsonObject { ["used_percent"] = 1.0, ["window_minutes"] = 10080, ["resets_at"] = 1772049427 },
        };
        var output = LimitParser.CodexRateLimits(rl);
        Assert.Equal(new[] { "five_hour", "seven_day" }, output.Select(w => w.Key));
        Assert.Equal(new[] { "Codex", "Codex" }, output.Select(w => w.Provider));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1771462627), output[0].ResetsAt);
        Assert.Equal(1.0, output[1].Utilization);
    }

    [Fact]
    public void CodexUnknownWindowIsKeptNotDropped()
    {
        var rl = new JsonObject { ["primary"] = new JsonObject { ["used_percent"] = 9, ["window_minutes"] = 60 } };
        Assert.Equal("window_60m", LimitParser.CodexRateLimits(rl).FirstOrDefault()?.Key);
        Assert.Equal("window_3d", LimitParser.KeyForWindowMinutes(4320));
    }

    [Fact]
    public void CodexSkipsSlotWithoutPercent()
    {
        var rl = new JsonObject { ["primary"] = new JsonObject { ["window_minutes"] = 300 } };
        Assert.Empty(LimitParser.CodexRateLimits(rl));
    }

    [Fact]
    public void SortPutsSessionBeforeWeek()
    {
        var w = new[]
        {
            new LimitWindow("C", "seven_day", 1, null),
            new LimitWindow("C", "five_hour", 2, null),
        };
        Assert.Equal(new[] { "five_hour", "seven_day" }, LimitParser.Sorted(w).Select(x => x.Key));
    }
}

public sealed class CredentialScanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void FindsNestedTokenRegardlessOfKeyPath()
    {
        var json = new JsonObject
        {
            ["someOauthBlock"] = new JsonObject
            {
                ["accessToken"] = "tok-abc",
                ["expiresAt"] = (1_700_000_000L + 3600) * 1000,
            },
        };
        Assert.Equal("tok-abc", CredentialScan.AccessToken(json, Now));
    }

    [Fact]
    public void RejectsExpiredToken()
    {
        var json = new JsonObject
        {
            ["o"] = new JsonObject { ["accessToken"] = "tok", ["expiresAt"] = (1_700_000_000L - 10) * 1000 },
        };
        Assert.Null(CredentialScan.AccessToken(json, Now));
    }

    [Fact]
    public void RejectsTokenInsideExpiryMargin()
    {
        var json = new JsonObject
        {
            ["o"] = new JsonObject { ["accessToken"] = "tok", ["expiresAt"] = (1_700_000_000L + 30) * 1000 },
        };
        // a token expiring inside the margin must not be used
        Assert.Null(CredentialScan.AccessToken(json, Now));
    }

    [Fact]
    public void MissingExpiryTreatedAsNonExpiring()
    {
        Assert.Equal("t", CredentialScan.AccessToken(new JsonObject { ["accessToken"] = "t" }, Now));
    }

    [Fact]
    public void EmptyOrAbsentTokenIsNil()
    {
        Assert.Null(CredentialScan.AccessToken(new JsonObject { ["accessToken"] = "" }, Now));
        Assert.Null(CredentialScan.AccessToken(new JsonObject { ["refreshToken"] = "r" }, Now));
    }

    [Fact]
    public void DoesNotRecurseForever()
    {
        // Deeper than the scan depth: must return null rather than hang or crash
        var deep = new JsonObject
        {
            ["a"] = new JsonObject { ["b"] = new JsonObject { ["c"] = new JsonObject { ["d"] = new JsonObject { ["accessToken"] = "too-deep" } } } },
        };
        Assert.Null(CredentialScan.AccessToken(deep, Now));
    }
}

public sealed class AggregateTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static Entry Make(string provider, string model, int input = 0, int output = 0,
                              int cacheRead = 0, int c5m = 0, int c1h = 0) =>
        new(provider, null, T, model, input, output, cacheRead, c5m, c1h);

    [Fact]
    public void CostUsesInputOutputAndCacheMultipliers()
    {
        var cfg = new Config { Pricing = new() { ["sonnet"] = new ModelPrice(3, 15, 0.3) } };
        var e = Make("Claude", "claude-sonnet-5", input: 1_000_000, output: 1_000_000,
                     cacheRead: 1_000_000, c5m: 1_000_000, c1h: 1_000_000);
        var a = Usage.Aggregate(new[] { e }, T, cfg);
        // 3 + 15 + 0.3 + (3 * 1.25) + (3 * 2) = 28.05
        Assert.Equal(28.05, a.Cost, 4);
        Assert.Equal(2_000_000, a.Io); // io counts input+output only
        Assert.Equal(2_000_000, a.CacheWrite);
    }

    [Fact]
    public void UnpricedModelIsCountedButNotCostedAndIsFlagged()
    {
        var cfg = new Config { Pricing = new() { ["sonnet"] = new ModelPrice(3, 15, 0.3) } };
        var a = Usage.Aggregate(new[] { Make("Codex", "gpt-5.3-codex", input: 1000, output: 10) }, T, cfg);
        Assert.Equal(1010, a.Io);
        Assert.Equal(0.0, a.Cost); // guessing a price tier would misreport spend
        Assert.True(a.HasUnpriced);
    }

    [Fact]
    public void GroupsByProviderAndModel()
    {
        var cfg = new Config { Pricing = new() { ["sonnet"] = new ModelPrice(3, 0, 0) } };
        var a = Usage.Aggregate(new[] { Make("Claude", "claude-sonnet-5", input: 1_000_000),
                                        Make("Codex", "gpt-5.3-codex", input: 500) }, T, cfg);
        Assert.Equal(1_000_000, a.Providers["Claude"].Io);
        Assert.Equal(500, a.Providers["Codex"].Io);
        // Models are reachable only through their own provider now
        Assert.Equal(3, a.Providers["Claude"].Models["claude-sonnet-5"].Cost, 4);
        Assert.False(a.Providers["Claude"].Models.ContainsKey("gpt-5.3-codex"));
    }

    [Fact]
    public void EntriesBeforeSinceAreExcluded()
    {
        var old = new Entry("Claude", null, T.AddSeconds(-10), "claude-sonnet-5", 5, 5, 0, 0, 0);
        Assert.Equal(0, Usage.Aggregate(new[] { old }, T, new Config()).Io);
    }

    [Fact]
    public void PriceMatchesBySubstringAndReturnsNilOtherwise()
    {
        var cfg = new Config();
        Assert.Equal(15.0, cfg.Price("claude-opus-5")?.Input);
        Assert.Equal(1.0, cfg.Price("claude-haiku-4-5-20251001")?.Input);
        Assert.Null(cfg.Price("some-unknown-model"));
    }
}

public sealed class FormatTests
{
    [Fact]
    public void TokenFormatting()
    {
        Assert.Equal("999", Usage.FmtTokens(999));
        Assert.Equal("1.5K", Usage.FmtTokens(1_500));
        Assert.Equal("5.7M", Usage.FmtTokens(5_728_245));
        Assert.Equal("3.7B", Usage.FmtTokens(3_682_642_412));
    }

    [Fact]
    public void CostFormatting()
    {
        Assert.Equal("$0.00", Usage.FmtCost(0));
        Assert.Equal("$12.27", Usage.FmtCost(12.274));
        // Grouped past four digits: an ungrouped dollar figure misreads by 10x at a glance
        Assert.Equal("$6,734.06", Usage.FmtCost(6734.061));
        Assert.Equal("$24,320.91", Usage.FmtCost(24320.906));
    }
}

public sealed class ConfigTests
{
    /// A fresh install speaks up: it warns before a cap arrives and says how the day is spread
    /// out. Neither prompts at launch; permission is asked at the first delivery.
    [Fact]
    public void FreshInstallSpeaksUp()
    {
        var cfg = new Config();
        Assert.True(cfg.Alerts); // a monitor that never speaks is a monitor nobody hears
        Assert.True(cfg.MindfulCues);
        // Explicit false in the file still wins; a default is not a policy
        var off = Config.Apply(new JsonObject { ["alerts"] = false, ["mindfulCues"] = false }, new Config());
        Assert.False(off.Alerts);
        Assert.False(off.MindfulCues);
    }

    [Fact]
    public void NoDefaultClientIdSoSignInStaysDisabled()
    {
        // shipping a borrowed client id by default is not ours to do
        Assert.False(new Config().OAuth.IsConfigured);
    }

    [Fact]
    public void RejectsNonHttpsOAuthOverrides()
    {
        var json = new JsonObject { ["oauth"] = new JsonObject { ["tokenUrl"] = "http://evil.example/token" } };
        var cfg = Config.Apply(json, new Config());
        Assert.Equal("https://console.anthropic.com/v1/oauth/token", cfg.OAuth.TokenUrl);
    }

    [Fact]
    public void RejectsOutOfRangeAndUnknownValues()
    {
        var json = new JsonObject
        {
            ["pollIntervalSeconds"] = 2,
            ["menuBarDisplay"] = "bogus",
            ["limitRedPct"] = 900,
        };
        var cfg = Config.Apply(json, new Config());
        Assert.Equal(300.0, cfg.PollIntervalSeconds);
        Assert.Equal("limits", cfg.MenuBarDisplay);
        Assert.Equal(85.0, cfg.LimitRedPct);
    }

    [Fact]
    public void ProviderSelection()
    {
        var cfg = Config.Apply(new JsonObject { ["providers"] = new JsonArray("Claude") }, new Config());
        Assert.True(cfg.Wants("claude")); // provider match must be case-insensitive
        Assert.False(cfg.Wants("Codex"));
    }

    [Fact]
    public void EmptyProviderListIsIgnored()
    {
        Assert.True(Config.Apply(new JsonObject { ["providers"] = new JsonArray() }, new Config()).Wants("Claude"));
    }
}

public sealed class WindowRecognitionTests
{
    private static LimitWindow W(string key, double util, DateTimeOffset? resets = null, string provider = "Claude") =>
        new(provider, key, util, resets);

    [Fact]
    public void KnownWindowsAreRecognized()
    {
        Assert.True(W("five_hour", 1).IsRecognized);
        Assert.True(W("seven_day", 1).IsRecognized);
        Assert.True(W("seven_day_opus", 1).IsRecognized);
    }

    // Claude has returned internal codenames such as nimbus_quill from the usage endpoint
    [Fact]
    public void UndocumentedCodenameIsUnrecognized()
    {
        Assert.False(W("nimbus_quill", 0).IsRecognized);
        Assert.Equal("nimbus quill", W("nimbus_quill", 0).DisplayName);
    }

    [Fact]
    public void EmptyUnnamedWindowIsUninformative() => Assert.True(W("nimbus_quill", 0).IsUninformative);

    [Fact]
    public void UnnamedWindowWithUsageIsKept()
    {
        // a window actually being consumed is news, even unnamed
        Assert.False(W("nimbus_quill", 4).IsUninformative);
    }

    [Fact]
    public void UnnamedWindowWithResetTimeIsKept() =>
        Assert.False(W("nimbus_quill", 0, resets: DateTimeOffset.UtcNow).IsUninformative);

    [Fact]
    public void KnownWindowAtZeroIsNeverHidden() => Assert.False(W("five_hour", 0).IsUninformative);

    [Fact]
    public void WeekLabelDropsModelQualifierForOtherProviders()
    {
        Assert.Equal("Week (all models)", W("seven_day", 1).DisplayName);
        Assert.Equal("Week", W("seven_day", 1, provider: "Codex").DisplayName); // only Claude splits the week by model
    }
}

public sealed class MenuBarProviderTests
{
    [Fact]
    public void DefaultsToAuto() => Assert.Equal(Config.AutoProvider, new Config().MenuBarProvider);

    [Fact]
    public void AcceptsKnownProviderCaseInsensitivelyAndCanonicalises()
    {
        var cfg = Config.Apply(new JsonObject { ["menuBarProvider"] = "codex" }, new Config());
        Assert.Equal("Codex", cfg.MenuBarProvider); // stored value should be canonical
    }

    [Fact]
    public void RejectsUnknownProviderAndKeepsAuto()
    {
        var cfg = Config.Apply(new JsonObject { ["menuBarProvider"] = "gemini" }, new Config());
        Assert.Equal(Config.AutoProvider, cfg.MenuBarProvider);
    }

    [Fact]
    public void AutoIsAValidChoice()
    {
        Assert.Equal(Config.AutoProvider,
                     Config.Apply(new JsonObject { ["menuBarProvider"] = "auto" }, new Config()).MenuBarProvider);
        Assert.Equal(4, Config.MenuBarProviderChoices.Length);
    }

    [Fact]
    public void WriteMergesWithoutDroppingOtherKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redline-cfg-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{\"pollIntervalSeconds\":600,\"providers\":[\"Codex\"]}");
            Assert.True(Config.SetMenuBarProvider("Codex", path));
            var cfg = Config.Load(path);
            Assert.Equal("Codex", cfg.MenuBarProvider);
            Assert.Equal(600.0, cfg.PollIntervalSeconds); // unrelated keys must survive the write
            Assert.False(cfg.Wants("Claude"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

public sealed class CredentialPolicyTests
{
    [Fact]
    public void BorrowingTheCLITokenIsOptIn()
    {
        // reading another app's credential must never be a silent default
        Assert.False(new Config().UseCLIToken);
    }

    [Fact]
    public void ExplicitOptInIsHonoured() =>
        Assert.True(Config.Apply(new JsonObject { ["useCLIToken"] = true }, new Config()).UseCLIToken);

    [Fact]
    public void NoClientIdShipsByDefault() => Assert.False(new Config().OAuth.IsConfigured);
}
