namespace RedLine.Core;

/// <summary>
/// Keeps a current snapshot in hand, without polling hard enough to be noticed.
///
/// The engine replaces the file atomically, so a watcher on the file itself would be orphaned
/// by the first write. The directory is watched instead, and a slow poll sits underneath
/// because a file watcher is an optimisation and never a guarantee.
/// </summary>
public sealed class SnapshotMonitor : IDisposable
{
    private readonly Engine engine;
    private readonly string directory;
    private readonly string fileName;
    private readonly TimeSpan debounce;
    private readonly object gate = new();

    private FileSystemWatcher? watcher;
    private Timer? poll;
    private Timer? pending;
    private Snapshot? current;
    /// The bytes behind <see cref="current"/>, which is what "unchanged" is decided on
    private string? currentText;
    private bool disposed;

    /// <summary>Raised when the snapshot changes. Never raised for an unchanged reading.</summary>
    public event Action<Snapshot?>? Updated;

    public Snapshot? Current
    {
        get { lock (gate) { return current; } }
    }

    public SnapshotMonitor(Engine? engine = null, EnginePaths? paths = null,
                           TimeSpan? pollInterval = null, TimeSpan? debounce = null)
    {
        paths ??= new EnginePaths();
        this.engine = engine ?? new Engine(paths);
        var path = paths.SnapshotPath;
        directory = Path.GetDirectoryName(path) ?? ".";
        fileName = Path.GetFileName(path);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(60);
    }

    public TimeSpan PollInterval { get; }

    /// <summary>Reads once, then keeps reading. The first read is synchronous so a window has
    /// something to draw before it is shown.</summary>
    public void Start()
    {
        Reread();

        // A directory that does not exist yet is normal: nothing has published into it. The
        // poll will pick it up once it does.
        if (Directory.Exists(directory))
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;
        }

        poll = new Timer(_ => Reread(), null, PollInterval, PollInterval);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (disposed) return;
            // One logical write arrives as several events, and an atomic replace always does
            pending?.Dispose();
            pending = new Timer(_ => Reread(), null, debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads and reports only if something actually changed.</summary>
    public void Reread()
    {
        // Compared as text, and parsed only when it moved: the engine rewrites this on every
        // sweep whether or not anything changed, and a UI bound to it should not redraw for
        // that.
        var text = engine.ReadSnapshotText();
        Snapshot? read;
        lock (gate)
        {
            if (disposed) return;
            if (text == currentText) return;
            read = text is null ? null : SnapshotJson.Parse(text);
            currentText = text;
            current = read;
        }
        Updated?.Invoke(read);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Renamed -= OnChanged;
            watcher.Dispose();
        }
        poll?.Dispose();
        pending?.Dispose();
    }
}
