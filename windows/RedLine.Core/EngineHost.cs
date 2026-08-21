using System.Diagnostics;

namespace RedLine.Core;

/// <summary>
/// Keeps the engine's watcher running for as long as the app is.
///
/// On macOS the app is the watcher, so history stays current simply because RedLine is open.
/// Here the engine is a separate process, and without this the app would show whatever was
/// last published and quietly go stale, which is worse than showing nothing.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly EnginePaths paths;
    private readonly TimeSpan restartDelay;
    private readonly int maxRestarts;
    private readonly object gate = new();

    private Process? process;
    private Timer? restart;
    private int restarts;
    private bool stopping;
    private bool disposed;

    /// <summary>Diagnostics from the watcher, a line at a time. Never the numbers themselves,
    /// which arrive through the published snapshot.</summary>
    public event Action<string>? Output;

    /// <summary>Raised when the watcher will not be restarted again, with the reason.</summary>
    public event Action<string>? GaveUp;

    public EngineHost(EnginePaths? paths = null, TimeSpan? restartDelay = null,
                      int maxRestarts = 5)
    {
        this.paths = paths ?? new EnginePaths();
        this.restartDelay = restartDelay ?? TimeSpan.FromSeconds(5);
        this.maxRestarts = maxRestarts;
    }

    public bool IsRunning
    {
        get
        {
            lock (gate) { return process is { HasExited: false }; }
        }
    }

    /// <summary>
    /// False when there is no engine to run, which the caller should surface rather than
    /// swallow: an app with no engine shows nothing and cannot explain why.
    /// </summary>
    public bool Start()
    {
        lock (gate)
        {
            if (disposed || process is { HasExited: false }) return true;
            stopping = false;
        }
        return Launch();
    }

    private bool Launch()
    {
        var executable = paths.FindEngine();
        if (executable is null)
        {
            GaveUp?.Invoke("redline was not found next to the app or on PATH");
            return false;
        }

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("watch");
        foreach (var (name, value) in paths.Overrides) info.Environment[name] = value;

        try
        {
            var started = new Process { StartInfo = info, EnableRaisingEvents = true };
            started.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 }) Output?.Invoke(e.Data);
            };
            started.Exited += (sender, _) => OnExited((sender as Process)?.ExitCode ?? 0);
            if (!started.Start())
            {
                GaveUp?.Invoke("the engine would not start");
                return false;
            }
            started.BeginOutputReadLine();
            lock (gate) { process = started; }
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
                                          or InvalidOperationException or IOException)
        {
            GaveUp?.Invoke(error.Message);
            return false;
        }
    }

    private void OnExited(int exitCode)
    {
        lock (gate)
        {
            if (disposed || stopping) return;
            if (exitCode == 0)
            {
                // A clean exit is a decision, not a crash. The watcher exits zero when another
                // one already holds the lock, and restarting it would spin forever.
                GaveUp?.Invoke("the engine exited cleanly; another watcher already has the lock");
                return;
            }
            if (restarts >= maxRestarts)
            {
                // Giving up out loud. A watcher that keeps dying is a real problem, and
                // restarting it forever would hide it behind a busy machine.
                GaveUp?.Invoke($"the engine exited {restarts} times; not restarting again");
                return;
            }
            restarts++;
            restart?.Dispose();
            restart = new Timer(_ => Launch(), null, restartDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        Process? running;
        lock (gate)
        {
            stopping = true;
            restart?.Dispose();
            restart = null;
            running = process;
            process = null;
        }
        if (running is null) return;
        try
        {
            if (!running.HasExited) running.Kill(entireProcessTree: true);
            running.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome asked for
        }
        finally
        {
            running.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop();
    }
}
