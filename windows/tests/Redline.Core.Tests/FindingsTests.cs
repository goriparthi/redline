// The findings checks, and the transcript reading behind them. A finding may report no
// saving, and must never report one it cannot support.
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class FindingsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
    private readonly Config _config = new();

    private static ToolUse Tool(string name, string? file = null, string? subject = null, string? id = null) =>
        new(id, name, file, subject, Now);

    private static SessionScan Session(IEnumerable<ToolUse> tools, Dictionary<string, int>? results = null,
                                       string? model = "claude-sonnet-5", string? cwd = "/tmp/project",
                                       IEnumerable<string>? commands = null) =>
        new($"/tmp/session-{Guid.NewGuid()}.jsonl", cwd, model, Now, Now, tools.ToList(),
            results ?? new Dictionary<string, int>(), (commands ?? Array.Empty<string>()).ToList());

    private static FindingsInput Input(IEnumerable<SessionScan> sessions, string[]? mcp = null,
                                       string[]? skills = null, string[]? agents = null,
                                       string[]? commands = null, MemoryFile[]? memory = null) =>
        new(sessions, mcp ?? Array.Empty<string>(), skills ?? Array.Empty<string>(),
            agents ?? Array.Empty<string>(), commands ?? Array.Empty<string>(),
            memory ?? Array.Empty<MemoryFile>(), 14, Now);

    private static Finding Find(FindingsReport report, string id)
    {
        var f = report.Findings.FirstOrDefault(x => x.Id == id);
        Assert.NotNull(f);
        return f!;
    }

    // MARK: MCP

    [Fact]
    public void UnusedMCPServerIsReportedWithoutAFabricatedSaving()
    {
        var used = Session(new[] { Tool("mcp__atlassian__search") });
        var report = Findings.Report(Input(new[] { used }, mcp: new[] { "atlassian", "playwright" }), _config);
        var finding = Find(report, "mcp-unused");
        Assert.Equal(new[] { "playwright" }, finding.Evidence.Select(e => e.Label));
        Assert.Null(finding.EstimatedUSD); // the schema overhead is real but not visible from here
        Assert.Equal(FindingBasis.Measured, finding.Basis);
    }

    [Fact]
    public void ServerNameSpellingDifferencesStillCountAsUsed()
    {
        var used = Session(new[] { Tool("mcp__chrome_devtools__click") });
        var report = Findings.Report(Input(new[] { used }, mcp: new[] { "chrome-devtools" }), _config);
        Assert.DoesNotContain(report.Findings, f => f.Id == "mcp-unused");
    }

    [Fact]
    public void NoSessionsMeansNoMCPClaim()
    {
        var report = Findings.Report(Input(Array.Empty<SessionScan>(), mcp: new[] { "atlassian" }), _config);
        // with nothing scanned, "never used" would be a claim about nothing
        Assert.Empty(report.Findings);
    }

    // MARK: re-reads

    [Fact]
    public void RepeatedReadsAreCountedFromTheSecondReadOn()
    {
        var reads = Enumerable.Range(1, 4).Select(i => Tool("Read", file: "/tmp/a.ts", id: $"r{i}"));
        var chars = new Dictionary<string, int> { ["r1"] = 8000, ["r2"] = 8000, ["r3"] = 8000, ["r4"] = 8000 };
        var report = Findings.Report(Input(new[] { Session(reads, chars) }), _config);
        var finding = Find(report, "reread-files");
        // Three extra reads of 8000 characters, at four characters per token
        Assert.Equal(6000, finding.EstimatedTokens);
        Assert.Equal(FindingBasis.Estimated, finding.Basis);
        Assert.NotNull(finding.EstimatedUSD);
    }

    [Fact]
    public void TwoReadsIsNotAPattern()
    {
        var reads = new[] { Tool("Read", file: "/tmp/a.ts", id: "r1"), Tool("Read", file: "/tmp/a.ts", id: "r2") };
        var report = Findings.Report(
            Input(new[] { Session(reads, new() { ["r1"] = 9000, ["r2"] = 9000 }) }), _config);
        Assert.DoesNotContain(report.Findings, f => f.Id == "reread-files");
    }

    [Fact]
    public void UnpricedModelLeavesTheDollarFigureOff()
    {
        var reads = Enumerable.Range(1, 3).Select(i => Tool("Read", file: "/tmp/a.ts", id: $"r{i}"));
        var report = Findings.Report(
            Input(new[] { Session(reads, new() { ["r1"] = 4000, ["r2"] = 4000, ["r3"] = 4000 },
                                  model: "some-unlisted-model") }), _config);
        var finding = Find(report, "reread-files");
        Assert.NotNull(finding.EstimatedTokens);
        Assert.Null(finding.EstimatedUSD);
    }

    // MARK: memory files

    [Fact]
    public void HeavyMemoryFileIsReported()
    {
        var report = Findings.Report(
            Input(new[] { Session(Array.Empty<ToolUse>()) },
                  memory: new[] { new MemoryFile("/Users/x/.claude/CLAUDE.md", 40_000, 1, true) }), _config);
        var finding = Find(report, "memory-heavy");
        Assert.Equal(10_000, finding.EstimatedTokens);
        Assert.Equal(FindingKind.Fyi, finding.Kind);
    }

    [Fact]
    public void AProjectFileIsChargedOnlyToItsOwnSessions()
    {
        // Two files of the same size, one loaded eight times as often. Pricing both against
        // every session on the machine is how a project's CLAUDE.md gets blamed for the lot.
        var sessions = Enumerable.Range(0, 8).Select(_ => Session(Array.Empty<ToolUse>()));
        var report = Findings.Report(
            Input(sessions, memory: new[]
            {
                new MemoryFile("/Users/x/.claude/CLAUDE.md", 40_000, 8, true),
                new MemoryFile("/repo/CLAUDE.md", 40_000, 1, false),
            }), _config);
        var finding = Find(report, "memory-heavy");
        Assert.NotNull(finding.EstimatedUSD);
        // 10k tokens at Sonnet input x1.25, nine session loads between the two files
        Assert.Equal(10_000.0 / 1_000_000 * 3 * 1.25 * 9, finding.EstimatedUSD!.Value, 4);
        Assert.Contains(finding.Evidence, e => e.Value?.Contains("every project") == true);
    }

    [Fact]
    public void SmallMemoryFileIsNotWorthMentioning()
    {
        var report = Findings.Report(
            Input(new[] { Session(Array.Empty<ToolUse>()) },
                  memory: new[] { new MemoryFile("/Users/x/.claude/CLAUDE.md", 1_000, 1, true) }), _config);
        Assert.DoesNotContain(report.Findings, f => f.Id == "memory-heavy");
    }

    // MARK: ghosts

    [Fact]
    public void UnusedSkillsAgentsAndCommandsAreListed()
    {
        var s = Session(new[] { Tool("Skill", subject: "pg-voice"), Tool("Task", subject: "Explore") },
                        commands: new[] { "scrum" });
        var report = Findings.Report(
            Input(new[] { s }, skills: new[] { "pg-voice", "who-am-i" }, agents: new[] { "Explore", "Plan" },
                  commands: new[] { "scrum", "deploy" }), _config);
        var finding = Find(report, "ghost-definitions");
        Assert.Equal(new[] { "who-am-i", "Plan", "deploy" }, finding.Evidence.Select(e => e.Label));
        Assert.Equal(new[] { "skill", "agent", "command" }, finding.Evidence.Select(e => e.Value));
        Assert.Null(finding.EstimatedUSD);
    }

    [Fact]
    public void ASkillInvokedAsASlashCommandCountsAsUsed()
    {
        var s = Session(Array.Empty<ToolUse>(), commands: new[] { "pg-voice" });
        var report = Findings.Report(Input(new[] { s }, skills: new[] { "pg-voice" }), _config);
        Assert.DoesNotContain(report.Findings, f => f.Id == "ghost-definitions");
    }

    // MARK: transcript parsing

    [Fact]
    public void ParsesToolUsesResultsAndCwd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"redline-findings-{Guid.NewGuid()}");
        var dir = Path.Combine(root, "project");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "session.jsonl");
            var lines = new[]
            {
                "{\"type\":\"assistant\",\"cwd\":\"/Users/x/repo\",\"timestamp\":\"2026-08-18T10:00:00.000Z\"," +
                "\"message\":{\"model\":\"claude-opus-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\"," +
                "\"name\":\"Read\",\"input\":{\"file_path\":\"/Users/x/repo/a.ts\"}}]}}",
                "{\"type\":\"user\",\"timestamp\":\"2026-08-18T10:00:01.000Z\",\"message\":{\"content\":" +
                "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"0123456789\"}]}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-18T10:00:02.000Z\",\"message\":" +
                "{\"model\":\"claude-opus-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\"," +
                "\"name\":\"mcp__atlassian__search\",\"input\":{}}]}}",
            };
            File.WriteAllText(file, string.Join("\n", lines));

            var scanner = new TranscriptScanner(root);
            var sessions = scanner.Scan(30, DateTimeOffset.UtcNow);
            var scan = Assert.Single(sessions);
            Assert.Equal("/Users/x/repo", scan.Cwd);
            Assert.Equal("claude-opus-5", scan.LastModel);
            Assert.Equal(2, scan.Tools.Count);
            Assert.Equal("/Users/x/repo/a.ts", scan.Tools[0].FilePath);
            Assert.Equal(10, scan.ResultChars["t1"]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void CommandTagsAreExtracted()
    {
        var body = "<command-message>x</command-message><command-name>/scrum</command-name>";
        Assert.Equal(new[] { "scrum" }, TranscriptScanner.CommandNames(body));
    }

    [Fact]
    public void SummaryCountsWhatItSays()
    {
        var report = new FindingsReport(Now, 14, 3, new[]
        {
            new Finding("a", FindingKind.FixNow, FindingBasis.Measured, "t", "d"),
            new Finding("b", FindingKind.Fyi, FindingBasis.Measured, "t", "d"),
        });
        Assert.Equal("2 findings · 1 to fix", report.Summary);
    }
}
