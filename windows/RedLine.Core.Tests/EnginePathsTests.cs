using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// These paths have to agree with Swift's AppPaths exactly. If they drift the app reads an
/// empty directory and reports nothing, which looks like "no usage" rather than like a bug.
/// </summary>
public class EnginePathsTests
{
    private static EnginePaths With(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) map[key] = value;
        return new EnginePaths(map);
    }

    [Fact]
    public void LocalAppDataIsTheWindowsDataDirectory()
    {
        var paths = With(("LOCALAPPDATA", Path.Combine("C:", "Users", "pg", "AppData", "Local")));
        Assert.Equal(Path.Combine("C:", "Users", "pg", "AppData", "Local", "RedLine"),
                     paths.DataDirectory);
    }

    [Fact]
    public void RoamingAppDataHoldsTheConfig()
    {
        var paths = With(("APPDATA", Path.Combine("C:", "Users", "pg", "AppData", "Roaming")));
        Assert.Equal(Path.Combine("C:", "Users", "pg", "AppData", "Roaming", "RedLine"),
                     paths.ConfigDirectory);
    }

    /// <summary>
    /// An explicit home beats everything, on both sides, so a test profile or a second
    /// profile on one machine sees one layout rather than two.
    /// </summary>
    [Fact]
    public void AnExistingRedlineHomeWinsOverLocalAppData()
    {
        var home = Directory.CreateTempSubdirectory("redline-paths").FullName;
        try
        {
            var paths = With(("REDLINE_HOME", home),
                             ("LOCALAPPDATA", Path.Combine("C:", "ignored")));
            Assert.Equal(Path.Combine(home, ".local", "share", "redline"), paths.DataDirectory);
            Assert.Equal(Path.Combine(home, ".config", "redline"), paths.ConfigDirectory);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// The engine ignores a REDLINE_HOME that does not exist, because a typo that silently
    /// creates a fresh empty history somewhere is worse than being ignored. So must this.
    /// </summary>
    [Fact]
    public void ARedlineHomeThatDoesNotExistIsIgnored()
    {
        var paths = With(("REDLINE_HOME", Path.Combine(Path.GetTempPath(), "definitely-not-here")),
                         ("LOCALAPPDATA", Path.Combine("C:", "Local")));
        Assert.Equal(Path.Combine("C:", "Local", "RedLine"), paths.DataDirectory);
    }

    [Fact]
    public void TheSnapshotSitsInTheDataDirectory()
    {
        var paths = With(("LOCALAPPDATA", Path.Combine("C:", "Local")));
        Assert.Equal(Path.Combine("C:", "Local", "RedLine", "snapshot.json"),
                     paths.SnapshotPath);
    }

    [Fact]
    public void AnExplicitEngineBinaryWins()
    {
        var file = Path.GetTempFileName();
        try
        {
            var paths = With(("REDLINE_BIN", file));
            Assert.Equal(file, paths.FindEngine());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AMissingEngineIsNullRatherThanAGuess()
    {
        var paths = With(("PATH", Path.Combine(Path.GetTempPath(), "no-such-directory")));
        Assert.Null(paths.FindEngine(appDirectory: Path.GetTempPath()));
    }

    [Fact]
    public void AnEngineSittingBesideTheAppIsFound()
    {
        var dir = Directory.CreateTempSubdirectory("redline-engine").FullName;
        try
        {
            var name = OperatingSystem.IsWindows() ? "redline.exe" : "redline";
            File.WriteAllText(Path.Combine(dir, name), "");
            var paths = With(("PATH", ""));
            Assert.Equal(Path.Combine(dir, name), paths.FindEngine(appDirectory: dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
