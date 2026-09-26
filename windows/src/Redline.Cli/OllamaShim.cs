// `redlinectl ollama-shim`: the C# form of scripts/ollama-shim.sh. Passes everything through to
// the real ollama.exe and counts only `ollama run MODEL` with a piped or single-argument prompt.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Cli;

/// Starts the real binary. A null `stdin` inherits the terminal; bytes are fed and then closed.
public interface IShimProcess
{
    int Run(string executable, IReadOnlyList<string> arguments, byte[]? stdin);
}

public sealed class OllamaShim
{
    /// Identifies RedLine's shim in the first 300 bytes of a file, so a copy is never the "real" one.
    public const string Marker = "RedLine ollama shim";
    public const string DefaultHost = "http://127.0.0.1:11434";

    public enum Shape { PassThrough, Stdin, Argument }

    private readonly IShimProcess _process;
    private readonly HttpMessageHandler? _http;
    private readonly Func<string, string?> _env;
    private readonly string? _dataDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<string> _skipDirs;
    private readonly List<string> _fallbacks;
    private readonly string? _self;

    /// Every argument defaults to the real machine; tests pass fakes for all of them.
    public OllamaShim(IShimProcess? process = null, HttpMessageHandler? http = null,
                      Func<string, string?>? env = null, string? dataDir = null,
                      Func<DateTimeOffset>? clock = null, IEnumerable<string>? skipDirs = null,
                      IEnumerable<string>? fallbacks = null, string? self = null)
    {
        _process = process ?? new RealProcess();
        _http = http;
        _env = env ?? Environment.GetEnvironmentVariable;
        _dataDir = dataDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _skipDirs = (skipDirs ?? DefaultSkipDirs()).Select(Normalize).ToList();
        _fallbacks = (fallbacks ?? DefaultFallbacks()).ToList();
        _self = self ?? Environment.ProcessPath;
    }

    /// The shim's own directory and redlinectl's, which can only ever hold a stand-in.
    private static IEnumerable<string> DefaultSkipDirs() =>
        new[] { ShimInstaller.BinDir(), AppContext.BaseDirectory };

    // Where the Ollama installer puts it, for a PATH that has not caught up yet
    private static IEnumerable<string> DefaultFallbacks()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (local.Length > 0) yield return Path.Combine(local, "Programs", "Ollama", "ollama.exe");
        if (programs.Length > 0) yield return Path.Combine(programs, "Ollama", "ollama.exe");
    }

    public static Shape Classify(IReadOnlyList<string> args, bool stdinIsTerminal)
    {
        if (args.Count < 2 || args[0] != "run" || args[1].Length == 0 || args[1].StartsWith('-'))
            return Shape.PassThrough;
        if (args.Count == 2 && !stdinIsTerminal) return Shape.Stdin;
        if (args.Count == 3 && !args[2].StartsWith('-')) return Shape.Argument;
        return Shape.PassThrough;
    }

    /// The real binary: explicit override first, then PATH minus the shim and any copy of it,
    /// then the usual install locations.
    public string? FindReal()
    {
        if (_env("REDLINE_OLLAMA_BIN") is { Length: > 0 } explicitBin) return explicitBin;
        foreach (var raw in (_env("PATH") ?? "").Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (_skipDirs.Contains(Normalize(dir), StringComparer.OrdinalIgnoreCase)) continue;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir, "ollama.exe")); } catch { continue; }
            if (!File.Exists(candidate) || IsSelf(candidate) || CarriesMarker(candidate)) continue;
            return candidate;
        }
        return _fallbacks.FirstOrDefault(c => File.Exists(c) && !IsSelf(c));
    }

    public int Run(IReadOnlyList<string> args, Stream stdin, bool stdinIsTerminal,
                   TextWriter stdout, TextWriter stderr)
    {
        var real = FindReal();
        if (real is null)
        {
            stderr.Write("redline ollama shim: no ollama binary found; install Ollama or set REDLINE_OLLAMA_BIN\n");
            stderr.Flush();
            return 127;
        }

        var shape = Classify(args, stdinIsTerminal);
        if (shape == Shape.PassThrough) return _process.Run(real, args, null);

        var model = args[1];
        var host = _env("OLLAMA_HOST") is { Length: > 0 } h ? h : DefaultHost;
        if (!host.Contains("://")) host = "http://" + host;
        var logDir = _env("REDLINE_DATA_DIR") is { Length: > 0 } d ? d : _dataDir ?? RedlineHome.DataDir();
        try { Directory.CreateDirectory(logDir); } catch { }
        var log = Path.Combine(logDir, "ollama.jsonl");

        byte[] prompt;
        if (shape == Shape.Argument) prompt = new UTF8Encoding(false).GetBytes(args[2]);
        else
        {
            using var buffer = new MemoryStream();
            try { stdin.CopyTo(buffer); } catch { }
            prompt = buffer.ToArray();
        }

        // Nothing to send is not an error; hand the empty call to the real CLI to answer as it would
        if (prompt.Length == 0) return _process.Run(real, args, prompt);

        var request = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = Encoding.UTF8.GetString(prompt),
            ["stream"] = false,
        }.ToJsonString();

        // stream:false so the response carries final token counts in one object
        var body = Post(host.TrimEnd('/') + "/api/generate", request, Timeout());
        // The API refused; replay through the real binary so the call still succeeds
        if (body is null) return _process.Run(real, new[] { "run", model }, prompt);

        if (Json.ParseObject(body) is not { } o)
        {
            stderr.Write("redline ollama shim: unparseable response\n");
            stderr.Flush();
            return 1;
        }

        var text = Json.Str(o["response"]) ?? "";
        stdout.Write(text.EndsWith('\n') ? text : text + "\n");
        stdout.Flush();

        try { Append(log, Record(o, model, _clock())); }
        catch (Exception e)
        {
            // The answer already reached the caller; an uncounted call is the worst case, never a broken one
            stderr.Write($"redline ollama shim: could not record usage: {e.Message}\n");
            stderr.Flush();
        }
        return 0;
    }

    /// Durations are nanoseconds. Missing counts mean a cached or empty eval, so default to 0.
    internal static JsonObject Record(JsonObject o, string fallbackModel, DateTimeOffset now) => new()
    {
        ["ts"] = PythonIso(now),
        ["model"] = o.ContainsKey("model") ? o["model"]?.DeepClone() : JsonValue.Create(fallbackModel),
        ["prompt_eval_count"] = o.ContainsKey("prompt_eval_count") ? o["prompt_eval_count"]?.DeepClone() : JsonValue.Create(0),
        ["eval_count"] = o.ContainsKey("eval_count") ? o["eval_count"]?.DeepClone() : JsonValue.Create(0),
        ["total_duration_ms"] = Millis(o["total_duration"]),
        ["load_duration_ms"] = Millis(o["load_duration"]),
        ["done_reason"] = o["done_reason"]?.DeepClone(),
    };

    // Python's round() is half to even, and returns an integer
    private static long Millis(JsonNode? ns) =>
        (long)Math.Round((Json.Num(ns) ?? 0) / 1e6, MidpointRounding.ToEven);

    /// datetime.now(timezone.utc).isoformat(): microseconds, omitted when zero, and "+00:00".
    internal static string PythonIso(DateTimeOffset now)
    {
        var u = now.ToUniversalTime();
        var micros = (u.Ticks % TimeSpan.TicksPerSecond) / 10;
        var head = u.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return micros == 0 ? head + "+00:00" : $"{head}.{micros:000000}+00:00";
    }

    // So the "+00:00" in a timestamp is written as itself rather than +
    private static readonly System.Text.Json.JsonSerializerOptions Relaxed =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void Append(string path, JsonObject record)
    {
        var bytes = new UTF8Encoding(false).GetBytes(record.ToJsonString(Relaxed) + "\n");
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes);
    }

    // OLLAMA_TIMEOUT in seconds, as curl --max-time takes it
    private TimeSpan Timeout() =>
        double.TryParse(_env("OLLAMA_TIMEOUT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(600);

    /// The response body, or null for anything curl --fail-with-body would call a failure.
    private string? Post(string url, string json, TimeSpan timeout)
    {
        try
        {
            using var client = _http is null ? new HttpClient() : new HttpClient(_http, disposeHandler: false);
            client.Timeout = timeout;
            using var content = new StringContent(json, new UTF8Encoding(false), "application/json");
            using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private bool IsSelf(string candidate) =>
        _self is not null && string.Equals(Normalize(candidate), Normalize(_self), StringComparison.OrdinalIgnoreCase);

    internal static bool CarriesMarker(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[300];
            var n = fs.Read(head, 0, head.Length);
            return Encoding.UTF8.GetString(head, 0, n).Contains(Marker, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.TrimEnd('\\', '/'); }
    }

    /// Stdout and stderr are inherited, so interactive chat and progress bars behave as without us.
    private sealed class RealProcess : IShimProcess
    {
        public int Run(string executable, IReadOnlyList<string> arguments, byte[]? stdin)
        {
            var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = stdin is not null };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            Process? process;
            try { process = Process.Start(psi); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"redline ollama shim: could not start {executable}: {e.Message}");
                return 127;
            }
            if (process is null) return 127;
            using (process)
            {
                if (stdin is not null)
                {
                    try
                    {
                        process.StandardInput.BaseStream.Write(stdin);
                        process.StandardInput.Close();
                    }
                    catch { }
                }
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
