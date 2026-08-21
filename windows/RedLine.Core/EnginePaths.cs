namespace RedLine.Core;

/// <summary>
/// Where the engine keeps things. Mirrors Swift's AppPaths, and has to keep mirroring it:
/// if these two disagree the app reads an empty directory and reports nothing at all.
/// </summary>
public sealed class EnginePaths
{
    private readonly IReadOnlyDictionary<string, string> environment;

    public EnginePaths(IReadOnlyDictionary<string, string>? environment = null)
    {
        this.environment = environment ?? Read();
    }

    private static Dictionary<string, string> Read()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry
                 in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) map[key] = value;
        }
        return map;
    }

    private string? Get(string name) =>
        environment.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// The data directory. An explicit REDLINE_HOME wins over everything, exactly as it does
    /// in the engine, so a test profile on either side sees the same files.
    /// </summary>
    public string DataDirectory
    {
        get
        {
            var home = Get("REDLINE_HOME");
            if (home is not null && Directory.Exists(home))
            {
                return Path.Combine(home, ".local", "share", "redline");
            }
            var local = Get("LOCALAPPDATA");
            if (local is not null) return Path.Combine(local, "RedLine");
            return Path.Combine(Home(), ".local", "share", "redline");
        }
    }

    public string ConfigDirectory
    {
        get
        {
            var home = Get("REDLINE_HOME");
            if (home is not null && Directory.Exists(home))
            {
                return Path.Combine(home, ".config", "redline");
            }
            var roaming = Get("APPDATA");
            if (roaming is not null) return Path.Combine(roaming, "RedLine");
            return Path.Combine(Home(), ".config", "redline");
        }
    }

    public string SnapshotPath => Path.Combine(DataDirectory, "snapshot.json");
    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    private string Home() =>
        Get("USERPROFILE") ?? Get("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The engine binary. An explicit REDLINE_BIN wins, then a copy sitting beside this app,
    /// then PATH. Returns null rather than guessing, because a wrong guess would be silent.
    /// </summary>
    public string? FindEngine(string? appDirectory = null)
    {
        var explicitPath = Get("REDLINE_BIN");
        if (explicitPath is not null && File.Exists(explicitPath)) return explicitPath;

        var names = OperatingSystem.IsWindows()
            ? new[] { "redline.exe" }
            : new[] { "redline", "redline-cli" };

        var beside = appDirectory ?? AppContext.BaseDirectory;
        foreach (var name in names)
        {
            var candidate = Path.Combine(beside, name);
            if (File.Exists(candidate)) return candidate;
        }

        var path = Get("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
