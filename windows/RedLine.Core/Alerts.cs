using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedLine.Core;

/// <summary>
/// One thing worth saying, as the engine decided it. Every word a shell posts is here: nothing
/// on this side works out whether a reading deserves interrupting someone, or what to call it.
/// </summary>
public sealed record AlertEvent
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>threshold, limit_reached, projection or reset.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";

    /// <summary>
    /// Whether this one makes a noise. The engine decides, because a limit reached is the
    /// only alert that should pull someone out of what they are doing.
    /// </summary>
    [JsonPropertyName("sound")] public bool Sound { get; init; }

    /// <summary>The percentage crossed, on a threshold. Absent on every other kind.</summary>
    [JsonPropertyName("percent")] public int? Percent { get; init; }
}

/// <summary>
/// One publication. The sequence is how a reader tells a new batch from one it has already
/// posted, and it climbs across restarts of the watcher.
/// </summary>
public sealed record AlertBatch
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<AlertEvent> Events { get; init; } = [];
}

public static class AlertJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static AlertBatch? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AlertBatch>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Watches what the engine decided and hands over anything not yet delivered.
///
/// Two rules, both of which are about not being annoying. A batch already posted is never
/// posted again, and whatever was on disk when this started is treated as delivered: an alert
/// is about now, and a shell that opens to a backlog of yesterday's limits is noise.
/// </summary>
public sealed class AlertMonitor : IDisposable
{
    private readonly string path;
    private readonly string directory;
    private readonly string fileName;
    private readonly TimeSpan debounce;
    private readonly object gate = new();

    private FileSystemWatcher? watcher;
    private Timer? poll;
    private Timer? pending;
    private long delivered;
    private bool disposed;

    /// <summary>Raised with the events of a batch this reader has not seen before.</summary>
    public event Action<IReadOnlyList<AlertEvent>>? Raised;

    /// <summary>The last sequence handed over, or skipped past at startup.</summary>
    public long Delivered
    {
        get { lock (gate) { return delivered; } }
    }

    public AlertMonitor(EnginePaths? paths = null, TimeSpan? pollInterval = null,
                        TimeSpan? debounce = null)
    {
        paths ??= new EnginePaths();
        path = paths.AlertFeedPath;
        directory = Path.GetDirectoryName(path) ?? ".";
        fileName = Path.GetFileName(path);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan PollInterval { get; }

    /// <summary>
    /// Starts watching, having first noted where the feed already is. Nothing already written
    /// is delivered, because it was decided before anyone was listening.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            delivered = Read()?.Seq ?? 0;
        }

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

        // The watcher is an optimisation; the poll is the guarantee
        poll = new Timer(_ => Check(), null, PollInterval, PollInterval);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (disposed) return;
            // An atomic replace arrives as several events
            pending?.Dispose();
            pending = new Timer(_ => Check(), null, debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads, and raises whatever is newer than the last batch handed over.</summary>
    public void Check()
    {
        var batch = Read();
        if (batch is null) return;

        IReadOnlyList<AlertEvent> events;
        lock (gate)
        {
            if (disposed || batch.Seq <= delivered) return;
            delivered = batch.Seq;
            events = batch.Events;
        }
        if (events.Count > 0) Raised?.Invoke(events);
    }

    private AlertBatch? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return AlertJson.Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            // The writer replaces this file, so a read can land mid swap. The next one will do.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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
