// Config validation beyond the cases ParserTests pins: out-of-range values fall back, unknown
// values are rejected, OAuth overrides must be https, and the cue settings clamp.
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class ConfigValidationTests
{
    private static Config Apply(string json) => Config.Apply(JsonNode.Parse(json)!.AsObject(), new Config());

    [Fact]
    public void OutOfRangeNumbersFallBackToTheDefault()
    {
        var cfg = Apply("""{"pollIntervalSeconds": 2, "limitYellowPct": 0, "limitRedPct": 900}""");
        Assert.Equal(300.0, cfg.PollIntervalSeconds);
        Assert.Equal(60.0, cfg.LimitYellowPct);
        Assert.Equal(85.0, cfg.LimitRedPct);
        Assert.Equal(10.0, Apply("""{"pollIntervalSeconds": 10}""").PollIntervalSeconds);
    }

    [Theory]
    [InlineData("{\"trayLayout\":\"split\"}", "split")]
    [InlineData("{\"trayLayout\":\"stacked\"}", "stacked")]
    [InlineData("{\"trayLayout\":\"sideways\"}", "stacked")]
    [InlineData("{\"trayLayout\":2}", "stacked")]
    [InlineData("{}", "stacked")]
    public void TrayLayoutIsStackedUnlessSplitIsNamed(string json, string expected) =>
        Assert.Equal(expected, Apply(json).TrayLayout);

    [Fact]
    public void UnknownChoicesAreRejected()
    {
        var cfg = Apply("""
            {"menuBarDisplay": "bogus", "limitWindows": "month", "updateChannel": "nightly",
             "dashboardTheme": "neon", "menuBarProvider": "gemini"}
            """);
        Assert.Equal("limits", cfg.MenuBarDisplay);
        Assert.Equal("all", cfg.LimitWindows);
        Assert.Equal("stable", cfg.UpdateChannel);
        Assert.Equal("auto", cfg.DashboardTheme);
        Assert.Equal(Config.AutoProvider, cfg.MenuBarProvider);
    }

    [Fact]
    public void KnownChoicesAreAccepted()
    {
        var cfg = Apply("""{"menuBarDisplay": "cost", "limitWindows": "week", "updateChannel": "beta", "dashboardTheme": "dark"}""");
        Assert.Equal("cost", cfg.MenuBarDisplay);
        Assert.Equal("week", cfg.LimitWindows);
        Assert.Equal("beta", cfg.UpdateChannel);
        Assert.Equal("dark", cfg.DashboardTheme);
    }

    [Fact]
    public void WrongTypesAreIgnoredRatherThanCoerced()
    {
        var cfg = Apply("""{"alerts": "no", "pollIntervalSeconds": "600", "providers": "Claude"}""");
        Assert.True(cfg.Alerts);
        Assert.Equal(300.0, cfg.PollIntervalSeconds);
        Assert.True(cfg.Wants("Codex"));
    }

    [Fact]
    public void EveryOAuthUrlMustBeHttps()
    {
        var cfg = Apply("""
            {"oauth": {"authorizeUrl": "http://evil.example/a", "tokenUrl": "ftp://evil.example/t",
                       "usageUrl": "not a url", "clientId": "abc", "redirectPort": 70000}}
            """);
        var defaults = new OAuthSettings();
        Assert.Equal(defaults.AuthorizeUrl, cfg.OAuth.AuthorizeUrl);
        Assert.Equal(defaults.TokenUrl, cfg.OAuth.TokenUrl);
        Assert.Equal(defaults.UsageUrl, cfg.OAuth.UsageUrl);
        Assert.Equal(defaults.RedirectPort, cfg.OAuth.RedirectPort);
        // The rest of the block still applies
        Assert.Equal("abc", cfg.OAuth.ClientId);
        Assert.True(cfg.OAuth.IsConfigured);
    }

    [Fact]
    public void HttpsOAuthOverridesAreAccepted()
    {
        var cfg = Apply("""{"oauth": {"tokenUrl": "https://example.test/token", "redirectPort": 8080}}""");
        Assert.Equal("https://example.test/token", cfg.OAuth.TokenUrl);
        Assert.Equal(8080, cfg.OAuth.RedirectPort);
    }

    [Theory]
    [InlineData(5, 15)]
    [InlineData(45, 45)]
    [InlineData(10_000, 600)]
    public void StretchMinutesClampsRatherThanRejects(double given, double expected) =>
        Assert.Equal(expected, Apply($$"""{"stretchMinutes": {{given}}}""").StretchMinutes);

    [Theory]
    [InlineData(3, 18)]
    [InlineData(21, 21)]
    [InlineData(30, 23)]
    public void LateHourClampsToTheEvening(int given, int expected) =>
        Assert.Equal(expected, Apply($$"""{"lateHour": {{given}}}""").LateHour);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(10, 10)]
    [InlineData(500, 90)]
    public void StreakDaysClamps(int given, int expected) =>
        Assert.Equal(expected, Apply($$"""{"streakDays": {{given}}}""").StreakDays);

    [Fact]
    public void ExternalUsagePathMustBeAnAbsoluteJsonFile()
    {
        Assert.Equal("", Apply("""{"externalUsagePath": "relative/usage.json"}""").ExternalUsagePath);
        Assert.Equal("", Apply("""{"externalUsagePath": "C:\\data\\usage.txt"}""").ExternalUsagePath);
        Assert.Equal(@"C:\data\usage.json",
            Apply("""{"externalUsagePath": "C:\\data\\usage.json"}""").ExternalUsagePath);
    }

    [Fact]
    public void PricingOverridesNeedAllThreeRatesAndLowercaseTheKey()
    {
        var cfg = Apply("""
            {"pricingPerMTok": {"Nova": {"input": 2, "output": 8, "cacheRead": 0.2},
                                "partial": {"input": 2}}}
            """);
        Assert.Equal(new ModelPrice(2, 8, 0.2), cfg.Price("some-nova-model"));
        Assert.Null(cfg.Price("partial-model"));
    }

    [Fact]
    public void ApplyNeverMutatesTheBase()
    {
        var baseCfg = new Config();
        Config.Apply(JsonNode.Parse("""{"providers": ["Claude"], "oauth": {"clientId": "x"}}""")!.AsObject(), baseCfg);
        Assert.True(baseCfg.Wants("Codex"));
        Assert.False(baseCfg.OAuth.IsConfigured);
    }
}
