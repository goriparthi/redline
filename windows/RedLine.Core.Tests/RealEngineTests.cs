using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// Runs the actual engine binary rather than a stub. Only when CI hands us one in
/// REDLINE_TEST_BIN: on a developer machine there may be no build to point at, and a test
/// that invented a path would be testing the invention.
/// </summary>
public class RealEngineTests
{
    private static string? Binary
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("REDLINE_TEST_BIN");
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
    }

    private static (Engine Engine, string Home) Fresh(string binary)
    {
        var home = Directory.CreateTempSubdirectory("redline-real").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects", "demo"));
        var paths = new EnginePaths(new Dictionary<string, string>
        {
            ["REDLINE_HOME"] = home,
            ["REDLINE_BIN"] = binary,
        });
        return (new Engine(paths), home);
    }

    [Fact]
    public void TheEngineReportsItsVersion()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var result = engine.Run("--version");
            Assert.True(result.Ran, result.Output);
            Assert.Contains("redline", result.Output);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// An empty machine must say so rather than report a zero. Code 30 is that answer, and
    /// the shell needs to be able to tell it apart from a successful reading of nothing.
    /// </summary>
    [Fact]
    public void AnEmptyMachineReportsNoDataRatherThanZero()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            Assert.Equal(EngineStatus.NoData, engine.Run("status", "--json").Status);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    /// <summary>
    /// The whole point of the shell: a transcript on disk turns into numbers on screen,
    /// through the engine, with nothing here parsing a transcript itself.
    /// </summary>
    [Fact]
    public void ATranscriptBecomesHistoryThroughTheEngine()
    {
        if (Binary is not { } binary) return;
        var (engine, home) = Fresh(binary);
        try
        {
            var stamp = DateTimeOffset.UtcNow.AddMinutes(-30)
                .ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var line = "{\"timestamp\":\"" + stamp + "\",\"requestId\":\"req_a\",\"message\":"
                + "{\"id\":\"a\",\"model\":\"claude-sonnet-5\",\"usage\":"
                + "{\"input_tokens\":1000,\"output_tokens\":100,\"cache_read_input_tokens\":0}}}";
            File.WriteAllText(
                Path.Combine(home, ".claude", "projects", "demo", "session.jsonl"),
                line + "\n");

            var ingested = engine.Run("ingest", "--json");
            Assert.Equal(EngineStatus.Ok, ingested.Status);
            Assert.Contains("\"added\" : 1", ingested.Output);

            // The second pass must add nothing, which is the property incremental reading exists for
            Assert.Contains("\"added\" : 0", engine.Run("ingest", "--json").Output);

            var history = engine.Run("history");
            Assert.Equal(EngineStatus.Ok, history.Status);
            Assert.Contains("1.1K", history.Output);
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
