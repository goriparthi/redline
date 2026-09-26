using System.Text;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class StatuslineFeedTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_755_400_000);

    private StatuslineSnapshot? Parse(string json) => StatuslineFeed.Parse(json, _now);

    /// Byte-for-byte what the feeder emits, so a change to either side breaks here rather
    /// than silently in the menu.
    [Fact]
    public void ParsesTheFeederOutput()
    {
        var snap = Parse("""
            {"updated_at":"2026-08-17T19:49:14Z",
             "five_hour":{"used_percentage":75,"resets_at":1755450000},
             "seven_day":{"used_percentage":41.5,"resets_at":1755900000},
             "model_scoped":[{"display_name":"Fable","utilization":12,
                              "resets_at":"2026-08-24T00:00:00Z"}]}
            """);
        Assert.NotNull(snap);
        Assert.Equal(3, snap!.Windows.Count);
        Assert.Equal("five_hour", snap.Windows[0].Key);
        Assert.Equal(75, snap.Windows[0].Utilization);
        Assert.Equal("Claude", snap.Windows[0].Provider);
        Assert.Equal("seven_day", snap.Windows[1].Key);
        Assert.Equal(41.5, snap.Windows[1].Utilization);
        Assert.Equal("seven_day_fable", snap.Windows[2].Key);
        Assert.NotNull(snap.UpdatedAt);
    }

    /// Fresh within FreshFor of its own stamp, stale past it, unusable with no stamp at all.
    [Fact]
    public void FreshnessFollowsTheSidecarStamp()
    {
        var stamp = DiagnosticsLog.Stamp(_now.AddSeconds(-60));
        var fresh = Parse($$$"""
            {"updated_at":"{{{stamp}}}",
             "five_hour":{"used_percentage":10,"resets_at":1755450000}}
            """);
        Assert.NotNull(fresh);
        Assert.True(fresh!.IsFresh(_now));
        Assert.False(fresh.IsFresh(_now.AddSeconds(StatuslineFeed.FreshFor + 61)));

        var unstamped = Parse("""{"five_hour":{"used_percentage":10,"resets_at":1755450000}}""");
        Assert.NotNull(unstamped);
        // Age is the only thing that qualifies a sidecar; no stamp, no trust
        Assert.False(unstamped!.IsFresh(_now));
    }

    /// The raw statusline payload, so a feeder that files the whole block still parses
    [Fact]
    public void AcceptsTheRawRateLimitsWrapper()
    {
        var snap = Parse("""
            {"session_id":"x","rate_limits":{"five_hour":{"used_percentage":10,
             "resets_at":1755450000}}}
            """);
        Assert.NotNull(snap);
        Assert.Equal(new[] { "five_hour" }, snap!.Windows.Select(w => w.Key));
        Assert.Equal(10, snap.Windows[0].Utilization);
    }

    /// A window that has already rolled over reports a percentage that no longer exists.
    [Fact]
    public void DropsWindowsWhoseResetHasPassed()
    {
        var snap = Parse("""
            {"five_hour":{"used_percentage":99,"resets_at":1755000000},
             "seven_day":{"used_percentage":20,"resets_at":1755900000}}
            """);
        Assert.Equal(new[] { "seven_day" }, snap!.Windows.Select(w => w.Key));
    }

    [Fact]
    public void AcceptsEitherPercentageSpelling()
    {
        var snap = Parse("""{"five_hour":{"utilization":33,"resets_at":1755450000}}""");
        Assert.Equal(33, snap?.Windows.FirstOrDefault()?.Utilization);
    }

    [Fact]
    public void ResetsAtAcceptsSecondsMillisecondsAndISO()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_755_450_000), StatuslineFeed.Date(1_755_450_000L));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_755_450_000), StatuslineFeed.Date(1_755_450_000_000L));
        Assert.NotNull(StatuslineFeed.Date("2026-08-24T00:00:00Z"));
        Assert.NotNull(StatuslineFeed.Date("2026-08-24T00:00:00.500Z"));
        Assert.Null(StatuslineFeed.Date(null));
        Assert.Null(StatuslineFeed.Date(0));
    }

    [Fact]
    public void MalformedInputIsNotAnError()
    {
        Assert.Null(StatuslineFeed.Parse(Encoding.UTF8.GetBytes("not json"), _now));
        Assert.True(Parse("{}")?.IsEmpty ?? false);
    }

    [Fact]
    public void ScopedKeySortsBesideTheOtherWeeklyWindows()
    {
        Assert.Equal("seven_day_fable", StatuslineFeed.ScopedKey("Fable"));
        Assert.Equal("seven_day_claude_opus", StatuslineFeed.ScopedKey("Claude Opus"));
        Assert.Equal("seven_day_scoped", StatuslineFeed.ScopedKey("!!"));
        // IsRecognized keeps them out of the unrecognized bucket the UI hides
        Assert.True(new LimitWindow("Claude", "seven_day_fable", 1, null).IsRecognized);
    }
}

public sealed class CredentialScanExtractionTests
{
    private static JsonObject Blob() => new()
    {
        ["claudeAiOauth"] = new JsonObject
        {
            ["accessToken"] = "sk-ant-oat-live",
            ["refreshToken"] = "sk-ant-ort-refresh",
            ["expiresAt"] = 1_755_450_000_000L,
        },
    };

    [Fact]
    public void ExtractsTheWholeCredentialFromANestedBlob()
    {
        var c = CredentialScan.Credential(Blob());
        Assert.NotNull(c);
        Assert.Equal("sk-ant-oat-live", c!.AccessToken);
        Assert.Equal("sk-ant-ort-refresh", c.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_755_450_000), c.ExpiresAt);
        Assert.True(c.CanRefresh);
    }

    /// An expired credential must still come back: "needs renewing" is not "signed out".
    [Fact]
    public void ExpiredCredentialIsStillReturned()
    {
        var c = CredentialScan.Credential(Blob());
        Assert.NotNull(c);
        Assert.False(c!.IsFresh(DateTimeOffset.FromUnixTimeSeconds(1_755_460_000)));
        Assert.True(c.IsFresh(DateTimeOffset.FromUnixTimeSeconds(1_755_400_000)));
    }

    /// The old entry point keeps filtering on freshness, so existing callers are unchanged
    [Fact]
    public void AccessTokenHelperStillFiltersExpired()
    {
        Assert.Null(CredentialScan.AccessToken(Blob(), DateTimeOffset.FromUnixTimeSeconds(1_755_460_000)));
        Assert.Equal("sk-ant-oat-live",
            CredentialScan.AccessToken(Blob(), DateTimeOffset.FromUnixTimeSeconds(1_755_400_000)));
    }

    [Fact]
    public void MissingExpiryIsTreatedAsNonExpiring()
    {
        var c = CredentialScan.Credential(new JsonObject { ["accessToken"] = "t" });
        Assert.NotNull(c);
        Assert.True(c!.IsFresh(DateTimeOffset.MaxValue.AddDays(-1)));
        Assert.False(c.CanRefresh);
    }

    [Fact]
    public void EmptyOrAbsentTokenYieldsNothing()
    {
        Assert.Null(CredentialScan.Credential(new JsonObject { ["accessToken"] = "" }));
        Assert.Null(CredentialScan.Credential(new JsonObject
        {
            ["mcpOAuth"] = new JsonObject { ["serverName"] = "x" },
        }));
    }
}

public sealed class SecurityCLIOutputTests
{
    /// `security -w` hex-dumps any payload it cannot return as a clean C-string.
    [Fact]
    public void DecodesAHexDump()
    {
        const string json = """{"accessToken":"x"}""";
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(json)).ToLowerInvariant();
        Assert.Equal(json, SecurityCLIOutput.Decode(hex));
        Assert.Equal(json, SecurityCLIOutput.Decode(hex + "\n"));
    }

    [Fact]
    public void LeavesPlainJSONAlone()
    {
        const string json = """{"accessToken":"x"}""";
        Assert.Equal(json, SecurityCLIOutput.Decode(json));
    }

    /// An odd length or a non-hex character means it was never a dump
    [Fact]
    public void AmbiguousInputIsNotDecoded()
    {
        Assert.Equal("abc", SecurityCLIOutput.Decode("abc"));
        Assert.Equal("zz", SecurityCLIOutput.Decode("zz"));
        Assert.Equal("", SecurityCLIOutput.Decode(""));
    }
}

public sealed class ClaudeAuthPolicyTests
{
    // Only the usage-endpoint backoff remains: RedLine no longer spends the CLI's refresh token
    [Fact]
    public void BackoffGrowsThenCaps()
    {
        Assert.Equal(0, ClaudeAuthPolicy.Backoff(0));
        Assert.Equal(300, ClaudeAuthPolicy.Backoff(1));
        Assert.Equal(600, ClaudeAuthPolicy.Backoff(2));
        Assert.Equal(1800, ClaudeAuthPolicy.Backoff(9));
    }
}

public sealed class CredentialOutcomeTests
{
    /// Only a signed-out CLI is durable news; a locked store treated the same forced daily reconnects.
    [Fact]
    public void OnlyNotFoundIsTerminal()
    {
        Assert.True(new CredentialOutcome.NotFound().IsTerminal);
        Assert.False(new CredentialOutcome.AccessDenied().IsTerminal);
        Assert.False(new CredentialOutcome.Unreadable().IsTerminal);
        Assert.False(new CredentialOutcome.Found(new BorrowedCredential("t", null, null)).IsTerminal);
    }
}

/// The chain-quoting round trip lives in the app, so this pins the pure half of it: what
/// `shellQuote` writes must be exactly what the unwire step can read back.
public sealed class ShellQuoteRoundTripTests
{
    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string? Unchain(string command)
    {
        const string marker = "REDLINE_STATUSLINE_CHAIN='";
        var start = command.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var rest = command[(start + marker.Length)..];
        var output = new StringBuilder();
        int q;
        while ((q = rest.IndexOf('\'')) >= 0)
        {
            output.Append(rest[..q]);
            var after = rest[(q + 1)..];
            if (after.StartsWith("\\''"))
            {
                output.Append('\'');
                rest = after[3..];
                continue;
            }
            return output.Length == 0 ? null : output.ToString();
        }
        return null;
    }

    private static string? RoundTrip(string original) =>
        Unchain($"REDLINE_STATUSLINE_CHAIN={Quote(original)} '/path/claude-statusline.sh'");

    [Fact]
    public void OrdinaryCommandSurvives()
    {
        Assert.Equal("bash ~/.claude/statusline-command.sh", RoundTrip("bash ~/.claude/statusline-command.sh"));
    }

    /// A statusline with quotes in it is the case that would silently truncate
    [Fact]
    public void QuotesAndPipesSurvive()
    {
        const string original = "jq -r '.model.display_name' | sed \"s/x/y/\"";
        Assert.Equal(original, RoundTrip(original));
    }

    [Fact]
    public void NoChainYieldsNothingToRestore()
    {
        Assert.Null(Unchain("'/path/claude-statusline.sh'"));
    }
}
