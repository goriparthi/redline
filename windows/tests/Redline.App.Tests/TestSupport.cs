// Temp homes and a scripted HTTP handler, so no test touches the real profile or network.
using System.Net;

namespace Redline.App.Tests;

public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "redline-app-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public TempHome() => Directory.CreateDirectory(Path);

    public string Join(string relative) => Redline.Core.RedlineHome.Join(Path, relative);

    public string Write(string relative, string text)
    {
        var p = Join(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

public sealed class ScriptedHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var resp = _respond(request);
        resp.RequestMessage ??= request;
        return resp;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
}
