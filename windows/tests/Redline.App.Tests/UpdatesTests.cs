using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Redline.App.Services;

namespace Redline.App.Tests;

public class UpdatesTests
{
    private const string Download = "https://github.com/goriparthi/redline/releases/download";

    private static JsonObject Release(string tag, bool draft = false, bool prerelease = false, params string[] assets) => new()
    {
        ["tag_name"] = tag,
        ["draft"] = draft,
        ["prerelease"] = prerelease,
        ["html_url"] = $"https://github.com/goriparthi/redline/releases/tag/{tag}",
        ["assets"] = new JsonArray(assets.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a,
            ["browser_download_url"] = a.StartsWith("http") ? a : $"{Download}/{tag}/{a}",
        }).ToArray()),
    };

    [Fact]
    public void StableFindsTheWindowsPackageAndItsChecksum()
    {
        var body = Release("v0.9.0", assets: new[] { "RedLine-0.9.0.dmg", "RedLine-0.9.0-win-x64.zip", "RedLine-0.9.0-win-x64.zip.sha256" }).ToJsonString();
        var r = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(body, beta: false, currentVersion: "0.8.2"));
        Assert.Equal("0.9.0", r.Version);
        Assert.Equal("RedLine-0.9.0-win-x64.zip", r.Package!.Name);
        Assert.Equal("RedLine-0.9.0-win-x64.zip.sha256", r.Checksum!.Name);
        Assert.True(r.CanInstall);
    }

    [Fact]
    public void AnMsiInstallTakesTheMsiAndAZipInstallTheZip()
    {
        var body = Release("v0.9.0", assets: new[] { "RedLine-0.9.0-win-x64.zip", "RedLine-0.9.0-win-x64.zip.sha256",
                                                     "RedLine-0.9.0-x64.msi", "RedLine-0.9.0-x64.msi.sha256" }).ToJsonString();
        var msi = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(body, false, "0.8.5", msi: true));
        Assert.Equal("RedLine-0.9.0-x64.msi", msi.Package!.Name);
        Assert.Equal("RedLine-0.9.0-x64.msi.sha256", msi.Checksum!.Name);
        var zip = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(body, false, "0.8.5"));
        Assert.Equal("RedLine-0.9.0-win-x64.zip", zip.Package!.Name);
    }

    [Fact]
    public void AnMsiInstallWithNoMsiAssetOpensThePageInstead()
    {
        var body = Release("v0.9.0", assets: new[] { "RedLine-0.9.0-win-x64.zip", "RedLine-0.9.0-win-x64.zip.sha256" }).ToJsonString();
        var r = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(body, false, "0.8.5", msi: true));
        Assert.False(r.CanInstall);
    }

    [Theory]
    [InlineData("{859B3A4C-3298-49F8-BE26-2845C71F3693}", true)]
    [InlineData("859B3A4C-3298-49F8-BE26-2845C71F3693", false)]
    [InlineData("{not-a-guid-at-all-but-38-characters!}", false)]
    [InlineData(null, false)]
    public void ProductCodeMustBeABracedGuid(string? raw, bool accepted) =>
        Assert.Equal(accepted, MsiInstall.ProductCode(() => raw) is not null);

    [Fact]
    public void NotNewerIsUpToDate()
    {
        var body = Release("v0.9.0").ToJsonString();
        Assert.IsType<Updates.CheckResult.UpToDate>(Updates.Interpret(body, false, "0.9.0"));
        Assert.IsType<Updates.CheckResult.UpToDate>(Updates.Interpret(body, false, "0.10.0"));
    }

    [Fact]
    public void AnythingButTheExactAssetFromThisRepoIsIgnored()
    {
        var body = Release("v1.0.0", assets: new[]
        {
            "RedLine-1.0.0-win-arm64.zip",
            "https://evil.example/goriparthi/redline/releases/download/v1.0.0/RedLine-1.0.0-win-x64.zip",
            "http://github.com/goriparthi/redline/releases/download/v1.0.0/RedLine-1.0.0-win-x64.zip.sha256",
        }).ToJsonString();
        var r = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(body, false, "0.9.0"));
        Assert.Null(r.Package);
        Assert.Null(r.Checksum);
        Assert.False(r.CanInstall);
    }

    [Fact]
    public void BetaPicksTheNewestNonDraftByVersion()
    {
        var list = new JsonArray(
            Release("v0.9.0-beta.2", prerelease: true),
            Release("v0.9.1-beta.1", draft: true),
            Release("v0.8.5"),
            Release("v0.9.0-beta.10", prerelease: true)).ToJsonString();
        var r = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(list, beta: true, currentVersion: "0.8.0"));
        Assert.Equal("0.9.0-beta.10", r.Version);
    }

    [Fact]
    public void BetaLetsAStableHotfixOutrankOlderBetas()
    {
        var list = new JsonArray(Release("v0.9.0-beta.3", prerelease: true), Release("v0.9.0")).ToJsonString();
        var r = Assert.IsType<Updates.CheckResult.Available>(Updates.Interpret(list, true, "0.9.0-beta.3"));
        Assert.Equal("0.9.0", r.Version);
    }

    [Fact]
    public void UnexpectedPayloadsFail()
    {
        Assert.IsType<Updates.CheckResult.Failed>(Updates.Interpret("[]", true, "0.1.0"));
        Assert.IsType<Updates.CheckResult.Failed>(Updates.Interpret("not json", false, "0.1.0"));
        Assert.IsType<Updates.CheckResult.Failed>(Updates.Interpret("{\"message\":\"x\"}", false, "0.1.0"));
    }

    [Theory]
    [InlineData("https://github.com/goriparthi/redline/releases/download/v1/x.zip", true)]
    [InlineData("http://github.com/goriparthi/redline/releases/download/v1/x.zip", false)]
    [InlineData("https://github.com/someone/redline/releases/download/v1/x.zip", false)]
    [InlineData("https://github.com.evil.io/goriparthi/redline/releases/download/v1/x.zip", false)]
    [InlineData("https://objects.githubusercontent.com/x.zip", false)]
    public void TrustsOnlyThisRepositorysDownloads(string url, bool trusted) =>
        Assert.Equal(trusted, Updates.IsTrustedDownloadUrl(url));

    [Fact]
    public void ParsesChecksumFilesInTheCommonShapes()
    {
        var digest = new string('a', 64);
        var other = new string('b', 64);
        Assert.Equal(digest, Updates.ParseSha256(digest.ToUpperInvariant() + "\n", "x.zip"));
        Assert.Equal(digest, Updates.ParseSha256($"{other}  y.zip\n{digest} *x.zip\n", "x.zip"));
        Assert.Equal(digest, Updates.ParseSha256($"{digest}  x.zip\r\n", "x.zip"));
        Assert.Null(Updates.ParseSha256($"{other}  y.zip\n", "x.zip"));
        Assert.Null(Updates.ParseSha256("abc123  x.zip", "x.zip"));
    }

    [Fact]
    public void VerifiesTheHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "redline");
            var good = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("redline"))).ToLowerInvariant();
            Assert.True(Updates.VerifyHash(path, good));
            Assert.True(Updates.VerifyHash(path, good.ToUpperInvariant()));
            Assert.False(Updates.VerifyHash(path, new string('0', 64)));
            Assert.False(Updates.VerifyHash(path, "nothex"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SignaturePolicy()
    {
        var ours = new SignatureInfo(true, true, "CN=Goriparthi");
        Assert.Null(Updates.SignatureProblem(SignatureInfo.Unsigned, SignatureInfo.Unsigned));
        Assert.Null(Updates.SignatureProblem(ours, ours));
        Assert.NotNull(Updates.SignatureProblem(ours, SignatureInfo.Unsigned));
        Assert.NotNull(Updates.SignatureProblem(ours, new SignatureInfo(true, false, "CN=Goriparthi")));
        Assert.NotNull(Updates.SignatureProblem(ours, new SignatureInfo(true, true, "CN=Someone Else")));
        // A self-signed local build pins its subject even though nothing trusts it
        var local = new SignatureInfo(true, false, "CN=Dev");
        Assert.Null(Updates.SignatureProblem(local, local));
        Assert.NotNull(Updates.SignatureProblem(local, new SignatureInfo(true, false, "CN=Other")));
    }

    private sealed class FixedSignatures : ISignatureVerifier
    {
        public Func<string, SignatureInfo> For { get; init; } = _ => SignatureInfo.Unsigned;
        public SignatureInfo Inspect(string path) => For(path);
    }

    private static byte[] Package(string exeName)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry($"RedLine/{exeName}").Open())) w.Write("new exe");
            using (var w = new StreamWriter(zip.CreateEntry("RedLine/redlinectl.exe").Open())) w.Write("new ctl");
        }
        return ms.ToArray();
    }

    private static (Updates Updates, Updates.CheckResult.Available Update) Staging(byte[] zip, string digest, ISignatureVerifier? sig = null)
    {
        var handler = new ScriptedHandler(req => req.RequestUri!.AbsolutePath.EndsWith(".sha256")
            ? ScriptedHandler.Json($"{digest}  RedLine-1.0.0-win-x64.zip\n")
            : ScriptedHandler.Bytes(zip));
        var update = new Updates.CheckResult.Available("1.0.0", "https://github.com/goriparthi/redline/releases/tag/v1.0.0",
            new ReleaseAsset("RedLine-1.0.0-win-x64.zip", $"{Download}/v1.0.0/RedLine-1.0.0-win-x64.zip"),
            new ReleaseAsset("RedLine-1.0.0-win-x64.zip.sha256", $"{Download}/v1.0.0/RedLine-1.0.0-win-x64.zip.sha256"));
        return (new Updates(new HttpClient(handler), sig ?? new FixedSignatures()), update);
    }

    [Fact]
    public async Task StagesAVerifiedPackageBesideTheInstall()
    {
        using var home = new TempHome();
        var install = Path.Combine(home.Path, "RedLine");
        home.Write("RedLine/RedLine.exe", "old exe");
        var zip = Package("RedLine.exe");
        var (updates, update) = Staging(zip, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());

        var result = await updates.StageAsync(update, install, exeName: "RedLine.exe");
        var ready = Assert.IsType<Updates.StageResult.Ready>(result);
        Assert.Equal(home.Path, Path.GetDirectoryName(ready.WorkDir));
        Assert.True(File.Exists(Path.Combine(ready.WorkDir, "swap.ps1")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "RedLine.exe")));
    }

    [Fact]
    public async Task RefusesAChecksumMismatchAndCleansUp()
    {
        using var home = new TempHome();
        home.Write("RedLine/RedLine.exe", "old exe");
        var (updates, update) = Staging(Package("RedLine.exe"), new string('0', 64));

        var failed = Assert.IsType<Updates.StageResult.Failed>(
            await updates.StageAsync(update, Path.Combine(home.Path, "RedLine"), exeName: "RedLine.exe"));
        Assert.Contains("checksum", failed.Message);
        Assert.Empty(Directory.GetDirectories(home.Path, ".redline-update-*"));
    }

    [Fact]
    public async Task RefusesAnUnsignedPackageWhenTheRunningCopyIsSigned()
    {
        using var home = new TempHome();
        var install = Path.Combine(home.Path, "RedLine");
        home.Write("RedLine/RedLine.exe", "old exe");
        var zip = Package("RedLine.exe");
        var sig = new FixedSignatures
        {
            For = p => p.StartsWith(install, StringComparison.OrdinalIgnoreCase)
                ? new SignatureInfo(true, true, "CN=Goriparthi") : SignatureInfo.Unsigned,
        };
        var (updates, update) = Staging(zip, Convert.ToHexString(SHA256.HashData(zip)), sig);

        var failed = Assert.IsType<Updates.StageResult.Failed>(await updates.StageAsync(update, install, exeName: "RedLine.exe"));
        Assert.Contains("not signed", failed.Message);
    }

    [Fact]
    public async Task RefusesAPackageWithoutTheExe()
    {
        using var home = new TempHome();
        home.Write("RedLine/RedLine.exe", "old exe");
        var zip = Package("Other.exe");
        var (updates, update) = Staging(zip, Convert.ToHexString(SHA256.HashData(zip)));
        Assert.IsType<Updates.StageResult.Failed>(
            await updates.StageAsync(update, Path.Combine(home.Path, "RedLine"), exeName: "RedLine.exe"));
    }

    [Fact]
    public void GuardsTheInstallDirectory()
    {
        Assert.NotNull(Updates.UnsafeTarget(@"C:\", "RedLine.exe"));
        Assert.NotNull(Updates.UnsafeTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RedLine.exe"));
        using var home = new TempHome();
        Assert.NotNull(Updates.UnsafeTarget(home.Path, "RedLine.exe"));
        home.Write("RedLine.exe", "x");
        Assert.Null(Updates.UnsafeTarget(home.Path, "RedLine.exe"));
        Assert.True(Updates.IsDevelopmentBuild(@"C:\src\redline\windows\src\Redline.App\bin\Debug\net8.0-windows"));
    }
}
