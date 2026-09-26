// The feeder run the way Claude Code runs it: one payload per draw. StatuslineFeedTests covers
// the parser against hand-typed payloads; this covers the half that writes the file.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class StatuslineFeederTests : IDisposable
{
    private readonly string _dir;
    private readonly string _out;
    private readonly List<(string Command, string Stdin)> _chained = new();

    public StatuslineFeederTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-feeder-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _out = Path.Combine(_dir, "claude-usage.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private StatuslineFeeder Feeder(string? chain = "") =>
        new(_out, chain, (cmd, stdin) => { _chained.Add((cmd, stdin)); return "drawn line"; });

    /// Draws one statusline payload and returns the sidecar afterwards, null while none exists.
    private string? Draw(string payload)
    {
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();
        // The statusline must never fail a draw
        Assert.Equal(0, Feeder().Run(stdin, stdout));
        return File.Exists(_out) ? File.ReadAllText(_out) : null;
    }

    private static List<LimitWindow> Windows(string? sidecar)
    {
        Assert.NotNull(sidecar);
        var snap = StatuslineFeed.Parse(sidecar!);
        Assert.NotNull(snap);
        return snap!.Windows.ToList();
    }

    private const string Live = """
        {"session_id":"s","cwd":"/tmp","rate_limits":{
          "five_hour":{"used_percentage":42,"resets_at":4102444800},
          "seven_day":{"used_percentage":7,"resets_at":4102444800}}}
        """;
    private const string Windowless =
        """{"session_id":"s","rate_limits":{"five_hour":null,"seven_day":null,"model_scoped":null}}""";

    /// The whole point of the file: what the feeder writes is what the parser reads.
    [Fact]
    public void APayloadWithWindowsRoundTripsThroughTheParser()
    {
        var parsed = Windows(Draw(Live));
        Assert.Equal(new[] { "five_hour", "seven_day" }, parsed.Select(w => w.Key));
        Assert.Equal(42, parsed[0].Utilization);
        Assert.Equal(7, parsed[1].Utilization);
        Assert.Equal("Claude", parsed[0].Provider);
    }

    /// The regression. Claude Code sends rate_limits with every window null on draws that made
    /// no API call, and writing that blanked the menu until the next real reading.
    [Fact]
    public void AWindowlessPayloadLeavesTheLastReadingAlone()
    {
        var good = Draw(Live);
        Assert.NotNull(good);
        Assert.Equal(good, Draw(Windowless));
        Assert.Equal(2, Windows(Draw(Windowless)).Count);
    }

    [Fact]
    public void APayloadWithoutRateLimitsLeavesTheLastReadingAlone()
    {
        var good = Draw(Live);
        Assert.NotNull(good);
        Assert.Equal(good, Draw("""{"session_id":"s"}"""));
    }

    /// Nothing to report and nothing reported before: a sidecar that never appears reads as
    /// "no reading yet", where an all-null one read as a broken source.
    [Fact]
    public void AWindowlessPayloadWritesNoSidecarAtAll()
    {
        Assert.Null(Draw(Windowless));
        Assert.Null(Draw("""{"rate_limits":{"five_hour":null,"model_scoped":[]}}"""));
        Assert.False(File.Exists(_out));
    }

    /// Model-scoped weeks arrive without the other two on some plans, and they are a reading.
    [Fact]
    public void ModelScopedAloneCountsAsAReading()
    {
        var sidecar = Draw("""
            {"rate_limits":{"five_hour":null,"seven_day":null,
              "model_scoped":[{"display_name":"Fable","utilization":12}]}}
            """);
        Assert.Equal(new[] { "seven_day_fable" }, Windows(sidecar).Select(w => w.Key));
    }

    /// A payload the feeder cannot make sense of must not take the previous reading with it.
    [Fact]
    public void MalformedPayloadsLeaveTheLastReadingAlone()
    {
        var good = Draw(Live);
        Assert.NotNull(good);
        Assert.Equal(good, Draw("not json{"));
        Assert.Equal(good, Draw("""{"rate_limits":"nope"}"""));
        Assert.Equal(good, Draw(""));
    }

    // Windows additions: the chain half of the script, which the Swift suite never exercised

    /// Privacy: only the rate-limit block reaches disk; cwd and session id are discarded.
    [Fact]
    public void OnlyTheRateLimitBlockIsWritten()
    {
        var sidecar = Draw(Live);
        Assert.NotNull(sidecar);
        Assert.DoesNotContain("session_id", sidecar);
        Assert.DoesNotContain("cwd", sidecar);
        Assert.Contains("updated_at", sidecar);
    }

    [Fact]
    public void TheChainGetsTheUntouchedPayloadAndOwnsTheLine()
    {
        var feeder = Feeder("my-statusline --flag");
        using var stdout = new StringWriter();
        Assert.Equal(0, feeder.Run(new StringReader(Live + "\n"), stdout));
        Assert.Equal("drawn line", stdout.ToString());
        var (command, stdin) = Assert.Single(_chained);
        Assert.Equal("my-statusline --flag", command);
        // Like bash's $(cat), only the trailing newline is dropped
        Assert.Equal(Live, stdin);
    }

    [Fact]
    public void NoChainPrintsNothingAndRunsNothing()
    {
        using var stdout = new StringWriter();
        Assert.Equal(0, Feeder("").Run(new StringReader(Live), stdout));
        Assert.Equal("", stdout.ToString());
        Assert.Empty(_chained);
    }

    [Fact]
    public void AFailingChainStillDrawsAndStillWrites()
    {
        var feeder = new StatuslineFeeder(_out, "boom", (_, _) => throw new InvalidOperationException());
        using var stdout = new StringWriter();
        Assert.Equal(0, feeder.Run(new StringReader(Live), stdout));
        Assert.True(File.Exists(_out));
    }

    [Fact]
    public void BashLookingChainsAreRecognized()
    {
        Assert.True(StatuslineFeeder.LooksLikeBash("bash ~/.claude/statusline-command.sh"));
        Assert.True(StatuslineFeeder.LooksLikeBash("\"C:\\Program Files\\Git\\bin\\bash.exe\" -c x"));
        Assert.True(StatuslineFeeder.LooksLikeBash("~/.claude/statusline.sh"));
        Assert.False(StatuslineFeeder.LooksLikeBash("npx ccstatusline"));
        Assert.False(StatuslineFeeder.LooksLikeBash("powershell -File line.ps1"));
    }
}
