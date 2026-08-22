using RedLine.Core;

namespace RedLine.Core.Tests;

public class EngineTests
{
    /// <summary>
    /// The exit codes are the contract. A near-limit run is a success that happens to be
    /// worth a colour change, so treating anything non-zero as failure would be wrong.
    /// </summary>
    [Theory]
    [InlineData(0, EngineStatus.Ok)]
    [InlineData(10, EngineStatus.NearLimit)]
    [InlineData(11, EngineStatus.AtLimit)]
    [InlineData(20, EngineStatus.Indeterminate)]
    [InlineData(30, EngineStatus.NoData)]
    public void KnownExitCodesMapToStatuses(int code, EngineStatus expected)
    {
        Assert.Equal(expected, Engine.Classify(code));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    public void AnUnrecognisedCodeIsNotQuietlyTreatedAsSuccess(int code)
    {
        Assert.Equal(EngineStatus.Unavailable, Engine.Classify(code));
    }

    [Fact]
    public void AMissingSnapshotIsNullRatherThanAThrow()
    {
        var dir = Directory.CreateTempSubdirectory("redline-empty").FullName;
        try
        {
            var paths = new EnginePaths(new Dictionary<string, string>
            {
                ["REDLINE_HOME"] = dir,
            });
            Assert.Null(new Engine(paths).ReadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThePublishedSnapshotIsReadFromWhereTheEngineWroteIt()
    {
        var home = Directory.CreateTempSubdirectory("redline-read").FullName;
        try
        {
            var data = Path.Combine(home, ".local", "share", "redline");
            Directory.CreateDirectory(data);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures",
                                   "snapshot-headless.json"),
                      Path.Combine(data, "snapshot.json"));

            var engine = new Engine(new EnginePaths(new Dictionary<string, string>
            {
                ["REDLINE_HOME"] = home,
            }));
            var snapshot = engine.ReadSnapshot();
            Assert.NotNull(snapshot);
            Assert.Equal(3, snapshot.Limits.Count);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void AnAbsentEngineIsReportedRatherThanCrashing()
    {
        var paths = new EnginePaths(new Dictionary<string, string> { ["PATH"] = "" });
        var result = new Engine(paths).Run("status", "--json");
        Assert.Equal(EngineStatus.Unavailable, result.Status);
        Assert.False(result.Ran);
    }
}
