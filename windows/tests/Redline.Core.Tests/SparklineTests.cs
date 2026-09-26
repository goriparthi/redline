// Menu text bars, plus the grouped aggregation the menu columns read from.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class SparklineTests
{
    [Fact]
    public void BarIsAlwaysExactlyTheRequestedWidth()
    {
        foreach (var share in new[] { 0.0, 0.001, 0.13, 0.5, 0.99, 1.0, 2.0, -1.0 })
            // width must be fixed or later columns misalign
            Assert.Equal(10, Sparkline.Bar(share, 10).Length);
    }

    [Fact]
    public void FullAndEmpty()
    {
        Assert.Equal("█████", Sparkline.Bar(1, 5));
        Assert.Equal("     ", Sparkline.Bar(0, 5));
    }

    [Fact]
    public void TinyShareStillShowsSomething()
    {
        var bar = Sparkline.Bar(0.004, 10);
        Assert.False(string.IsNullOrEmpty(bar.Trim(' ')), "a real but small share must not render as empty");
    }

    [Fact]
    public void HalfUsesAFullHalfOfTheWidth()
    {
        Assert.StartsWith("█████", Sparkline.Bar(0.5, 10));
    }

    [Fact]
    public void PercentPadsAndFlagsSubOnePercent()
    {
        Assert.Equal(" 50%", Sparkline.Percent(0.5));
        Assert.Equal("100%", Sparkline.Percent(1.0));
        Assert.Equal(" <1%", Sparkline.Percent(0.004)); // 0% would read as unused when it is not
        Assert.Equal("  0%", Sparkline.Percent(0));
    }

    [Fact]
    public void PadTruncatesWithEllipsisAndFills()
    {
        Assert.Equal("abc  ", Sparkline.Pad("abc", 5));
        Assert.Equal("abc…", Sparkline.Pad("abcdef", 4));
        Assert.Equal("   42", Sparkline.Pad("42", 5, alignRight: true));
        Assert.Equal("abc", Sparkline.Pad("abc", 3));
    }

    [Fact]
    public void ShortModelDropsVendorPrefixOnly()
    {
        Assert.Equal("opus-5", Sparkline.ShortModel("claude-opus-5"));
        Assert.Equal("5.3-codex", Sparkline.ShortModel("gpt-5.3-codex"));
        Assert.Equal("qwen3-coder:30b", Sparkline.ShortModel("qwen3-coder:30b"));
    }
}

public sealed class GroupedAggregationTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static Entry MakeEntry(string provider, string model, int io) =>
        new(provider, null, T, model, io, 0, 0, 0, 0);

    private static readonly Config Cfg = new()
    {
        Pricing = new() { ["sonnet"] = new ModelPrice(3, 15, 0.3) },
    };

    [Fact]
    public void ModelsNestUnderTheirOwnProvider()
    {
        var a = Usage.Aggregate(new[] { MakeEntry("Claude", "claude-sonnet-5", 100),
                                        MakeEntry("Codex", "gpt-5.3-codex", 50) }, T, Cfg);
        Assert.Equal(new[] { "claude-sonnet-5" }, a.Providers["Claude"].Models.Keys);
        // a Codex model must never appear under Claude
        Assert.Equal(new[] { "gpt-5.3-codex" }, a.Providers["Codex"].Models.Keys);
    }

    [Fact]
    public void RankedProvidersLargestFirst()
    {
        var a = Usage.Aggregate(new[] { MakeEntry("Claude", "claude-sonnet-5", 10),
                                        MakeEntry("Codex", "gpt-5.3-codex", 900) }, T, Cfg);
        Assert.Equal(new[] { "Codex", "Claude" }, a.RankedProviders.Select(p => p.Provider));
    }

    [Fact]
    public void RankedModelsLargestFirst()
    {
        var a = Usage.Aggregate(new[] { MakeEntry("Claude", "claude-sonnet-5", 10),
                                        MakeEntry("Claude", "claude-opus-5", 900) }, T, Cfg);
        Assert.Equal(new[] { "claude-opus-5", "claude-sonnet-5" },
                     a.Providers["Claude"].RankedModels.Select(m => m.Model));
    }

    [Fact]
    public void UnpricedModelIsFlaggedPerModel()
    {
        var a = Usage.Aggregate(new[] { MakeEntry("Codex", "gpt-5.3-codex", 50) }, T, Cfg);
        Assert.False(a.Providers["Codex"].Models["gpt-5.3-codex"].Priced);
        Assert.True(a.HasUnpriced);
    }

    [Fact]
    public void ShareOfTotal()
    {
        var a = Usage.Aggregate(new[] { MakeEntry("Claude", "claude-sonnet-5", 75),
                                        MakeEntry("Codex", "gpt-5.3-codex", 25) }, T, Cfg);
        Assert.Equal(0.75, a.Share(a.Providers["Claude"].Io), 4);
    }

    [Fact]
    public void ShareIsZeroWhenNothingRecorded()
    {
        Assert.Equal(0, new Agg().Share(0)); // must not divide by zero
    }
}

public sealed class ProviderCacheTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void CacheFiguresAreTrackedPerProvider()
    {
        var entries = new[]
        {
            new Entry("Claude", null, T, "claude-sonnet-5", 10, 1, 500, 20, 5),
            new Entry("Codex", null, T, "gpt-5.3-codex", 5, 1, 7, 0, 0),
        };
        var a = Usage.Aggregate(entries, T, new Config());
        Assert.Equal(500, a.Providers["Claude"].CacheRead);
        Assert.Equal(25, a.Providers["Claude"].CacheWrite);
        Assert.Equal(7, a.Providers["Codex"].CacheRead); // a focused tile must not show another provider's cache
        Assert.Equal(507, a.CacheRead); // the global figure still sums every provider
    }
}
