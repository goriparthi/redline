using Redline.Core;

namespace Redline.Core.Tests;

public sealed class AvailabilityTests : IDisposable
{
    private readonly string _home;

    public AvailabilityTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "redline-home-" + Guid.NewGuid());
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private void Make(string path) => Directory.CreateDirectory(RedlineHome.Join(_home, path));

    [Fact]
    public void NothingInstalledIsDetectedAsEmpty()
    {
        var a = ProviderAvailability.Detect(_home);
        Assert.True(a.IsEmpty);
        Assert.False(a.HasChoice);
    }

    // A claude.ai user with no CLI has nothing under ~/.claude, but a signed-in account
    // still has rate limits worth showing
    [Fact]
    public void ClaudeAccountCountsWithNothingLocal()
    {
        var a = ProviderAvailability.Detect(_home, claudeAccount: true);
        Assert.True(a.Has("Claude"));
        Assert.False(a.Has("Codex"));
    }

    [Fact]
    public void NoClaudeAccountAndNothingLocalMeansNoClaude()
    {
        var a = ProviderAvailability.Detect(_home, claudeAccount: false);
        Assert.False(a.Has("Claude"));
    }

    [Fact]
    public void SingleProviderNeedsNoAllOption()
    {
        Make(".codex/sessions");
        var a = ProviderAvailability.Detect(_home);
        Assert.Equal(new[] { "Codex" }, a.Installed);
        Assert.False(a.HasChoice); // one track means there is nothing to choose between
        Assert.Equal(new[] { "Codex" }, a.TrackChoices); // an 'all providers' entry would be meaningless here
    }

    [Fact]
    public void SeveralProvidersKeepTheAllOption()
    {
        Make(".claude/projects");
        Make(".codex/sessions");
        var a = ProviderAvailability.Detect(_home);
        Assert.Equal(new[] { "Claude", "Codex" }, a.Installed); // canonical order, not filesystem order
        Assert.True(a.HasChoice);
        Assert.Equal(Config.AutoProvider, a.TrackChoices.First());
        Assert.Equal(3, a.TrackChoices.Count);
    }

    [Fact]
    public void OllamaCountsWhenRunningEvenWithNoDataDirectory()
    {
        var a = ProviderAvailability.Detect(_home, ollamaReachable: true);
        // a fresh Ollama install may not have written anything yet
        Assert.Equal(new[] { "Ollama" }, a.Installed);
    }

    [Fact]
    public void OllamaCountsFromTheWrapperLogAlone()
    {
        Make(".local/share/redline");
        File.WriteAllBytes(RedlineHome.Join(_home, ".local/share/redline/ollama.jsonl"), Array.Empty<byte>());
        Assert.True(ProviderAvailability.Detect(_home).Has("Ollama"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Make(".claude");
        Assert.True(ProviderAvailability.Detect(_home).Has("claude"));
    }
}

public sealed class FirstRunTests
{
    [Fact]
    public void FirstRunIsTrueUntilAConfigExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redline-fr-{Guid.NewGuid()}.json");
        try
        {
            Assert.True(Config.IsFirstRun(path));
            File.WriteAllText(path, "{}");
            Assert.False(Config.IsFirstRun(path)); // setup must not reappear on every launch
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
