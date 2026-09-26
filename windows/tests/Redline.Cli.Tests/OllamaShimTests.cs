// The ollama shim. The worst case it may produce is an uncounted call, never a broken one, and
// the prompt must never reach argv or the environment.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Redline.Cli;
using Redline.Core;

namespace Redline.Cli.Tests;

public sealed class OllamaShimTests : IDisposable
{
    private readonly string _root;
    private readonly string _shimDir;
    private readonly string _realDir;
    private readonly string _dataDir;
    private readonly string _real;
    private readonly Dictionary<string, string?> _env = new();
    private readonly FakeProcess _process = new();
    private readonly FakeHttp _http = new();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    public OllamaShimTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "redline-shim-" + Guid.NewGuid());
        _shimDir = Path.Combine(_root, "shim");
        _realDir = Path.Combine(_root, "real");
        _dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(_shimDir);
        Directory.CreateDirectory(_realDir);
        File.WriteAllText(Path.Combine(_shimDir, "ollama.exe"), "a stand-in parked beside the shim");
        _real = Path.Combine(_realDir, "ollama.exe");
        File.WriteAllText(_real, "MZ the real one");
        _env["PATH"] = string.Join(Path.PathSeparator, _shimDir, _realDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private OllamaShim Shim() => new(_process, _http, k => _env.GetValueOrDefault(k), _dataDir,
                                     () => Now, new[] { _shimDir }, Array.Empty<string>(), self: "none");

    private (int Code, string Out, string Err) Run(string[] args, string? stdin = null, bool terminal = false)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin ?? ""));
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Shim().Run(args, input, terminal, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string LogPath => Path.Combine(_dataDir, "ollama.jsonl");

    private const string Answer = """
        {"model":"qwen3:8b","response":"hello there","done":true,"done_reason":"stop",
         "prompt_eval_count":26,"eval_count":7,"total_duration":1234567890,"load_duration":2500000}
        """;

    // Which invocations are counted

    [Theory]
    [InlineData(new[] { "run", "qwen3:8b" }, false, OllamaShim.Shape.Stdin)]
    [InlineData(new[] { "run", "qwen3:8b" }, true, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "run", "qwen3:8b", "hi" }, true, OllamaShim.Shape.Argument)]
    [InlineData(new[] { "run", "qwen3:8b", "hi" }, false, OllamaShim.Shape.Argument)]
    [InlineData(new[] { "run", "qwen3:8b", "--verbose" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "run", "--verbose", "qwen3:8b" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "run", "qwen3:8b", "one", "two" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "run", "" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "run" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new[] { "list" }, false, OllamaShim.Shape.PassThrough)]
    [InlineData(new string[0], false, OllamaShim.Shape.PassThrough)]
    public void OnlyTheTwoPlainRunShapesAreIntercepted(string[] args, bool terminal, OllamaShim.Shape expected) =>
        Assert.Equal(expected, OllamaShim.Classify(args, terminal));

    // Finding the real binary

    [Fact]
    public void TheShimsOwnDirectoryAndMarkedCopiesAreSkipped()
    {
        var copyDir = Path.Combine(_root, "copy");
        Directory.CreateDirectory(copyDir);
        File.WriteAllText(Path.Combine(copyDir, "ollama.exe"), "@echo off\r\nrem RedLine ollama shim: a copy\r\n");
        _env["PATH"] = string.Join(Path.PathSeparator, _shimDir, copyDir, "", "\"" + _realDir + "\"");
        Assert.Equal(_real, Shim().FindReal());
    }

    [Fact]
    public void AnExplicitBinaryWins()
    {
        _env["REDLINE_OLLAMA_BIN"] = @"D:\tools\ollama.exe";
        Assert.Equal(@"D:\tools\ollama.exe", Shim().FindReal());
    }

    [Fact]
    public void FallsBackToTheInstallLocation()
    {
        _env["PATH"] = _shimDir;
        var shim = new OllamaShim(_process, _http, k => _env.GetValueOrDefault(k), _dataDir, () => Now,
                                  new[] { _shimDir }, new[] { Path.Combine(_root, "nope.exe"), _real }, "none");
        Assert.Equal(_real, shim.FindReal());
    }

    [Fact]
    public void NoBinaryExits127WithAReason()
    {
        _env["PATH"] = _shimDir;
        var r = Run(new[] { "list" });
        Assert.Equal(127, r.Code);
        Assert.Contains("no ollama binary found", r.Err);
        Assert.Empty(_process.Calls);
    }

    // Pass through

    [Fact]
    public void EverythingElsePassesThroughUntouched()
    {
        _process.ExitCode = 3;
        var r = Run(new[] { "pull", "qwen3:8b" });
        Assert.Equal(3, r.Code);
        var call = Assert.Single(_process.Calls);
        Assert.Equal(_real, call.Exe);
        Assert.Equal(new[] { "pull", "qwen3:8b" }, call.Args);
        Assert.Null(call.Stdin);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void InteractiveChatIsNotCounted()
    {
        Run(new[] { "run", "qwen3:8b" }, terminal: true);
        Assert.Null(Assert.Single(_process.Calls).Stdin);
        Assert.Empty(_http.Requests);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void AnEmptyPromptGoesToTheRealCli()
    {
        Run(new[] { "run", "qwen3:8b" }, stdin: "");
        var call = Assert.Single(_process.Calls);
        Assert.Equal(new[] { "run", "qwen3:8b" }, call.Args);
        Assert.Empty(call.Stdin!);
        Assert.Empty(_http.Requests);
    }

    // Counted calls

    [Fact]
    public void APipedPromptIsAnsweredOverTheApiAndCounted()
    {
        _http.Respond(HttpStatusCode.OK, Answer);
        var r = Run(new[] { "run", "qwen3:8b" }, stdin: "Summarize this build log.\n");
        Assert.Equal(0, r.Code);
        Assert.Equal("hello there\n", r.Out);
        Assert.Empty(_process.Calls);

        var req = Assert.Single(_http.Requests);
        Assert.Equal("http://127.0.0.1:11434/api/generate", req.Url);
        var body = JsonNode.Parse(req.Body)!.AsObject();
        Assert.Equal("qwen3:8b", Json.Str(body["model"]));
        Assert.Equal("Summarize this build log.\n", Json.Str(body["prompt"]));
        Assert.Equal(false, Json.Bool(body["stream"]));

        Assert.Contains("\"ts\":\"2026-08-18T12:00:00+00:00\"", File.ReadAllText(LogPath));
        var rec = JsonNode.Parse(File.ReadAllText(LogPath).Trim())!.AsObject();
        Assert.Equal(new[] { "ts", "model", "prompt_eval_count", "eval_count", "total_duration_ms",
                             "load_duration_ms", "done_reason" }, rec.Select(kv => kv.Key));
        Assert.Equal("2026-08-18T12:00:00+00:00", Json.Str(rec["ts"]));
        Assert.Equal("qwen3:8b", Json.Str(rec["model"]));
        Assert.Equal(26, Json.Int(rec["prompt_eval_count"]));
        Assert.Equal(7, Json.Int(rec["eval_count"]));
        Assert.Equal(1235, Json.Int(rec["total_duration_ms"]));
        Assert.Equal(2, Json.Int(rec["load_duration_ms"]));
        Assert.Equal("stop", Json.Str(rec["done_reason"]));

        // The record is exactly what the store reads back
        var entries = new OllamaStore(LogPath).Scan(7, Now);
        var e = Assert.Single(entries);
        Assert.Equal((26, 7, "qwen3:8b"), (e.Input, e.Output, e.Model));
    }

    [Fact]
    public void AnArgumentPromptIsCountedAndTheHostIsHonoured()
    {
        _env["OLLAMA_HOST"] = "localhost:9999";
        _http.Respond(HttpStatusCode.OK, """{"response":"done\n","eval_count":3}""");
        var r = Run(new[] { "run", "qwen3:8b", "say hi" });
        Assert.Equal("done\n", r.Out);
        var req = Assert.Single(_http.Requests);
        Assert.Equal("http://localhost:9999/api/generate", req.Url);
        Assert.Equal("say hi", Json.Str(JsonNode.Parse(req.Body)!["prompt"]));
        var rec = JsonNode.Parse(File.ReadAllText(LogPath).Trim())!.AsObject();
        // Missing counts mean a cached or empty eval, and the asked-for model stands in
        Assert.Equal(0, Json.Int(rec["prompt_eval_count"]));
        Assert.Equal(0, Json.Int(rec["total_duration_ms"]));
        Assert.Equal("qwen3:8b", Json.Str(rec["model"]));
        Assert.Null(rec["done_reason"]);
        Assert.True(rec.ContainsKey("done_reason"));
    }

    [Fact]
    public void TheDataDirectoryCanBeMoved()
    {
        var moved = Path.Combine(_root, "moved");
        _env["REDLINE_DATA_DIR"] = moved;
        _http.Respond(HttpStatusCode.OK, Answer);
        Run(new[] { "run", "qwen3:8b", "hi" });
        Assert.True(File.Exists(Path.Combine(moved, "ollama.jsonl")));
    }

    [Fact]
    public void RecordsAppend()
    {
        _http.Respond(HttpStatusCode.OK, Answer);
        Run(new[] { "run", "qwen3:8b", "one" });
        Run(new[] { "run", "qwen3:8b", "two" });
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    // Failures replay through the real binary

    [Fact]
    public void ARefusedCallIsReplayedWithThePromptOnStdin()
    {
        _http.Respond(HttpStatusCode.NotFound, """{"error":"model not found"}""");
        _process.ExitCode = 1;
        var r = Run(new[] { "run", "qwen3:8b", "a private prompt" });
        Assert.Equal(1, r.Code);
        var call = Assert.Single(_process.Calls);
        Assert.Equal(new[] { "run", "qwen3:8b" }, call.Args);
        Assert.Equal("a private prompt", Encoding.UTF8.GetString(call.Stdin!));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void AnUnreachableServerIsReplayed()
    {
        _http.Throw = true;
        var r = Run(new[] { "run", "qwen3:8b" }, stdin: "piped");
        Assert.Equal(0, r.Code);
        Assert.Equal("piped", Encoding.UTF8.GetString(Assert.Single(_process.Calls).Stdin!));
    }

    [Fact]
    public void AnUnparseableAnswerFailsLoudly()
    {
        _http.Respond(HttpStatusCode.OK, "<html>not json</html>");
        var r = Run(new[] { "run", "qwen3:8b", "hi" });
        Assert.Equal(1, r.Code);
        Assert.Contains("unparseable response", r.Err);
        Assert.Empty(_process.Calls);
    }

    [Fact]
    public void TimestampsMatchPythonsIsoformat()
    {
        Assert.Equal("2026-08-18T12:00:00+00:00", OllamaShim.PythonIso(Now));
        Assert.Equal("2026-08-18T12:00:00.123456+00:00", OllamaShim.PythonIso(Now.AddTicks(1_234_560)));
    }

    private sealed class FakeProcess : IShimProcess
    {
        public readonly List<(string Exe, string[] Args, byte[]? Stdin)> Calls = new();
        public int ExitCode;

        public int Run(string executable, IReadOnlyList<string> arguments, byte[]? stdin)
        {
            Calls.Add((executable, arguments.ToArray(), stdin?.ToArray()));
            return ExitCode;
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Requests = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        public bool Throw;

        public void Respond(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            if (Throw) throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
