// Statuspage feeds for the hosted providers, fetched only when the user switched them on and
// at most every 15 minutes. Ollama is probed locally; its cloud publishes no feed to read.
using System.Net.Http;
using Redline.Core;

namespace Redline.App.Services;

public sealed class ServiceStatusService
{
    public const double Interval = 900;

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private DateTimeOffset? _startedAt;
    private Timer? _timer;

    public ServiceStatus.Report? Claude { get; private set; }
    public ServiceStatus.Report? Codex { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }

    /// Raised on a pool thread after each feed answers, once per provider.
    public event Action? Changed;

    public ServiceStatusService(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? new HttpClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// Starts both fetches unless disabled or checked within the interval. False when skipped.
    public bool RefreshIfDue(Config config)
    {
        if (!config.StatusChecks) return false;
        var now = _clock();
        lock (_lock)
        {
            if (_startedAt is { } last && (now - last).TotalSeconds < Interval) return false;
            _startedAt = now;
        }
        _ = Task.Run(async () =>
        {
            var claude = FetchAsync(ServiceStatus.ClaudeUrl);
            var codex = FetchAsync(ServiceStatus.CodexUrl);
            Claude = await claude;
            CheckedAt = _clock();
            Changed?.Invoke();
            Codex = await codex;
            CheckedAt = _clock();
            Changed?.Invoke();
        });
        return true;
    }

    /// Polls on a timer while `statusChecks` is on; the check itself enforces the interval.
    public void Start(Func<Config> config)
    {
        Stop();
        _timer = new Timer(_ => RefreshIfDue(config()), null, TimeSpan.Zero, TimeSpan.FromSeconds(Interval));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// A feed that fails to answer or parse reports nothing, rather than a guessed status.
    public async Task<ServiceStatus.Report?> FetchAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var body = await _http.GetStringAsync(url, cts.Token);
            return ServiceStatus.Parse(body);
        }
        catch { return null; }
    }

    /// The snapshot's services list: hosted feeds as read, Ollama from the live local probe.
    public List<Snapshot.Service> SnapshotServices(Config config, Snapshot.OllamaSection? ollama)
    {
        var services = new List<Snapshot.Service>();
        if (Claude is { } c) services.Add(new Snapshot.Service("Claude", c.Indicator, c.Description));
        if (Codex is { } x) services.Add(new Snapshot.Service("Codex", x.Indicator, x.Description));
        // Local, so it needs no network opt-in, and it comes from the live section, never a default
        if (config.Wants("Ollama") && ollama is { } o)
            services.Add(new Snapshot.Service("Ollama", o.Reachable ? "local" : "local-down",
                                              "checked directly, no network leaves this PC"));
        return services;
    }
}
