// Talks to a local Ollama server. Loopback only, no auth; an OLLAMA_HOST that names another
// machine is ignored so nothing here can reach off the machine by accident.
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Net.Http;
using Redline.Core;

namespace Redline.App.Services;

public sealed record OllamaState
{
    public bool Reachable { get; init; }
    public IReadOnlyList<OllamaModel> Models { get; init; } = Array.Empty<OllamaModel>();
    public IReadOnlyList<OllamaRunningModel> Running { get; init; } = Array.Empty<OllamaRunningModel>();
    public string? Version { get; init; }
    public string? Error { get; init; }
    public IReadOnlySet<string> Busy { get; init; } = new HashSet<string>();
    public DateTimeOffset? CheckedAt { get; init; }
}

public sealed class OllamaService
{
    public const string DefaultHost = "http://127.0.0.1:11434";

    private readonly Uri _host;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private OllamaState _state = new();

    /// Raised after every change, on whatever thread made it; the UI marshals.
    public event Action<OllamaState>? StateChanged;

    public OllamaService(string? host = null, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _host = LoopbackHost(host ?? Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public OllamaState State { get { lock (_lock) return _state; } }

    public string HostDescription => _host.AbsoluteUri;

    /// A malformed or non-loopback override falls back to the default rather than going elsewhere.
    public static Uri LoopbackHost(string? raw)
    {
        var fallback = new Uri(DefaultHost);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var text = raw.Trim();
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return fallback;
        var authority = text.Split("://", 2)[1].Split('/')[0];
        // Ollama reads a host with no port as its own 11434, not as port 80
        var port = authority.LastIndexOf(':') > authority.LastIndexOf(']') ? u.Port : 11434;
        // Ollama's own "listen everywhere" spelling still means this machine for a client
        if (u.Host is "0.0.0.0" or "[::]" or "::") return new Uri($"http://127.0.0.1:{port}/");
        var loopback = u.IsLoopback || (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return loopback ? new UriBuilder(u.Scheme, u.Host, port).Uri : fallback;
    }

    private void Set(Func<OllamaState, OllamaState> mutate)
    {
        OllamaState next;
        lock (_lock) next = _state = mutate(_state);
        StateChanged?.Invoke(next);
    }

    public async Task ReloadAsync()
    {
        var version = GetJsonAsync("/api/version");
        var tags = GetJsonAsync("/api/tags");
        var ps = GetJsonAsync("/api/ps");
        await Task.WhenAll(version, tags, ps);
        var (v, t, p) = (version.Result, tags.Result, ps.Result);
        Set(s => s with
        {
            CheckedAt = _clock(),
            // /api/version answering is the signal the server is up
            Reachable = v is not null,
            Version = v is null ? null : Json.Str(v["version"]),
            Models = t is null ? new List<OllamaModel>() : OllamaParse.Models(t),
            Running = p is null ? new List<OllamaRunningModel>() : OllamaParse.Running(p),
            Error = v is null ? "Ollama is not running" : null,
        });
    }

    /// Loads a model with an empty prompt so it becomes resident without generating anything.
    public async Task StartAsync(string model, string keepAlive = "30m")
    {
        Mark(model, true);
        await PostJsonAsync("/api/generate", new JsonObject { ["model"] = model, ["prompt"] = "", ["keep_alive"] = keepAlive });
        await ReloadAsync();
        Mark(model, false);
    }

    /// keep_alive 0 unloads immediately. It frees memory; nothing here removes downloaded weights.
    public async Task StopAsync(string model)
    {
        Mark(model, true);
        await PostJsonAsync("/api/generate", new JsonObject { ["model"] = model, ["prompt"] = "", ["keep_alive"] = 0 });
        await ReloadAsync();
        Mark(model, false);
    }

    /// Starts `ollama serve` hidden when the server is not answering. False when there is no
    /// Ollama install to start, or it is already up.
    public async Task<bool> StartServerAsync()
    {
        if (await GetJsonAsync("/api/version") is not null) return false;
        if (FindOllama() is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("serve");
            using var _ = Process.Start(psi);
        }
        catch (Exception e)
        {
            Diag.Log.Error("ollama.serve_failed", "could not start ollama serve", new() { ["error"] = e.Message });
            return false;
        }
        // The server takes a moment to bind; the caller sees it on the next reload either way
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            if (await GetJsonAsync("/api/version") is not null) break;
        }
        await ReloadAsync();
        return true;
    }

    /// The real binary, never RedLine's own shim in ~/.local/bin, which would log `serve` as usage.
    public static string? FindOllama(string? home = null)
    {
        var shimDir = RedlineHome.Join(home ?? RedlineHome.Url, ".local/bin");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (local.Length > 0) candidates.Add(Path.Combine(local, "Programs", "Ollama", "ollama.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var d = dir.Trim().Trim('"');
                if (string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(shimDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(Path.Combine(d, "ollama.exe"));
            }
            catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Mark(string model, bool busy) =>
        Set(s =>
        {
            var set = new HashSet<string>(s.Busy);
            if (busy) set.Add(model); else set.Remove(model);
            return s with { Busy = set };
        });

    /// Snapshot form for the widget, which has no network access of its own.
    public async Task<Snapshot.OllamaSection> SnapshotSectionAsync()
    {
        var version = GetJsonAsync("/api/version");
        var tags = GetJsonAsync("/api/tags");
        var ps = GetJsonAsync("/api/ps");
        await Task.WhenAll(version, tags, ps);
        var (v, t, p) = (version.Result, tags.Result, ps.Result);
        // Unreachable is said outright, so no reader mistakes silence for zero models
        if (v is null && t is null && p is null)
            return new Snapshot.OllamaSection(false, null, new List<Snapshot.OllamaSection.RunningModel>(), 0, 0);
        var models = t is null ? new List<OllamaModel>() : OllamaParse.Models(t);
        var running = (p is null ? new List<OllamaRunningModel>() : OllamaParse.Running(p))
            .Select(r => new Snapshot.OllamaSection.RunningModel(r.Name, r.SizeBytes, r.VramShare)).ToList();
        return new Snapshot.OllamaSection(v is not null, v is null ? null : Json.Str(v["version"]), running,
            models.Count, models.Sum(m => m.SizeBytes), models.Count(m => OllamaLocality.IsCloud(m.Name)));
    }

    private Uri Url(string path) => new(_host, path);

    private async Task<JsonObject?> GetJsonAsync(string path)
    {
        try
        {
            using var resp = await _http.GetAsync(Url(path));
            if (!resp.IsSuccessStatusCode) return null;
            return Json.ParseObject(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private async Task<JsonObject?> PostJsonAsync(string path, JsonObject body)
    {
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(Url(path), content);
            return Json.ParseObject(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e)
        {
            Set(s => s with { Error = $"Request failed: {e.Message}" });
            return null;
        }
    }
}
