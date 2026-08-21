using System.Diagnostics;

namespace RedLine.Core;

/// <summary>
/// The engine's exit codes, which are part of its contract. A script should not have to parse
/// prose and neither should this.
/// </summary>
public enum EngineStatus
{
    Ok = 0,
    NearLimit = 10,
    AtLimit = 11,
    /// <summary>Nothing to report, which is different from nothing to read.</summary>
    Indeterminate = 20,
    NoData = 30,
    /// <summary>The engine could not be run at all. Not one of its codes.</summary>
    Unavailable = -1,
}

public sealed record EngineResult(EngineStatus Status, string Output)
{
    public bool Ran => Status != EngineStatus.Unavailable;
}

/// <summary>
/// Reads what the engine publishes, and asks it directly when the snapshot does not carry the
/// answer. Nothing here parses a transcript: that is the engine's job in every shell, so the
/// two can never report different numbers for the same day.
/// </summary>
public sealed class Engine
{
    private readonly EnginePaths paths;
    private readonly Func<string, IReadOnlyList<string>, EngineResult>? runner;

    public Engine(EnginePaths? paths = null,
                  Func<string, IReadOnlyList<string>, EngineResult>? runner = null)
    {
        this.paths = paths ?? new EnginePaths();
        this.runner = runner;
    }

    /// <summary>
    /// The published snapshot, or null when there is none to read. A snapshot being absent is
    /// the normal state before `redline watch` has ever run.
    /// </summary>
    public Snapshot? ReadSnapshot()
    {
        var text = ReadSnapshotText();
        return text is null ? null : SnapshotJson.Parse(text);
    }

    /// <summary>
    /// The snapshot as written, or null when there is none. Exposed because the bytes are the
    /// only cheap way to tell "unchanged" from "changed": Snapshot is a record whose synthesised
    /// equality compares its collections by reference, so two identical readings never match.
    /// </summary>
    public string? ReadSnapshotText()
    {
        try
        {
            var path = paths.SnapshotPath;
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            // The writer replaces this file, so a read can land mid-swap. Next poll will do.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public EngineResult Run(params string[] arguments) => Run(arguments, TimeSpan.FromSeconds(30));

    public EngineResult Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (runner is not null) return runner(arguments.FirstOrDefault() ?? "", arguments);

        var executable = paths.FindEngine();
        if (executable is null)
        {
            return new EngineResult(EngineStatus.Unavailable, "redline was not found");
        }

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // The child has to be pointed at the same home this object resolves paths against,
        // or the two read different directories and only one of them is wrong out loud.
        foreach (var (name, value) in paths.Overrides) info.Environment[name] = value;

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return new EngineResult(EngineStatus.Unavailable, "redline would not start");
            }
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // A wedged engine must not wedge the UI with it
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new EngineResult(EngineStatus.Unavailable, "redline did not finish");
            }
            return new EngineResult(Classify(process.ExitCode), output);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
                                          or InvalidOperationException or IOException)
        {
            return new EngineResult(EngineStatus.Unavailable, error.Message);
        }
    }

    /// <summary>An unrecognised code is reported rather than mapped onto a familiar one.</summary>
    public static EngineStatus Classify(int exitCode) => exitCode switch
    {
        0 => EngineStatus.Ok,
        10 => EngineStatus.NearLimit,
        11 => EngineStatus.AtLimit,
        20 => EngineStatus.Indeterminate,
        30 => EngineStatus.NoData,
        _ => EngineStatus.Unavailable,
    };
}
