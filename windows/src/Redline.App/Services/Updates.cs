// Update check against the GitHub releases API, daily by default and on demand. The only
// request RedLine makes without being asked; `autoCheckUpdates: false` stops it.
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Net.Http;
using Redline.Core;

namespace Redline.App.Services;

public sealed record ReleaseAsset(string Name, string Url);

/// What Authenticode says about one file. Subject is the leaf signer's distinguished name.
public sealed record SignatureInfo(bool Signed, bool Trusted, string? Subject)
{
    public static readonly SignatureInfo Unsigned = new(false, false, null);
}

public interface ISignatureVerifier
{
    SignatureInfo Inspect(string path);
}

public sealed class Updates
{
    public const string Owner = "goriparthi";
    public const string Repo = "redline";
    public const string RepoUrl = "https://github.com/goriparthi/redline";
    public const string ReleasesUrl = "https://api.github.com/repos/goriparthi/redline/releases/latest";
    // Beta channel: the list endpoint is the only one that includes prereleases
    public const string AllReleasesUrl = "https://api.github.com/repos/goriparthi/redline/releases?per_page=20";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxChecksumBytes = 64 * 1024;

    public abstract record CheckResult
    {
        public sealed record UpToDate : CheckResult;
        public sealed record Available(string Version, string PageUrl, ReleaseAsset? Package, ReleaseAsset? Checksum) : CheckResult
        {
            /// Both assets present is what enables the in-place install; otherwise the page opens.
            public bool CanInstall => Package is not null && Checksum is not null;
        }
        public sealed record Failed(string Message) : CheckResult;
    }

    public abstract record StageResult
    {
        /// Verified and staged. Swap() starts a helper that waits for this process to exit,
        /// replaces the install directory and relaunches; the caller then exits.
        public sealed record Ready(Action Swap, string WorkDir) : StageResult;
        public sealed record Failed(string Message) : StageResult;
    }

    private readonly HttpClient _http;
    private readonly ISignatureVerifier _signatures;
    private Timer? _timer;

    public Updates(HttpClient? http = null, ISignatureVerifier? signatures = null)
    {
        _http = http ?? DefaultClient();
        _signatures = signatures ?? new AuthenticodeVerifier();
    }

    private static HttpClient DefaultClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a user agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RedLine-Windows");
        return c;
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    public static bool IsNewer(string candidate, string than) => VersionCompare.IsNewer(candidate, than);

    public static string PackageName(string version) => $"RedLine-{version}-win-x64.zip";

    /// An MSI install updates through Windows Installer, so Settings > Apps stays accurate.
    public static string MsiName(string version) => $"RedLine-{version}-x64.msi";

    public async Task<CheckResult> CheckAsync(string currentVersion, string channel = "stable", CancellationToken ct = default)
    {
        var beta = channel == "beta";
        using var req = new HttpRequestMessage(HttpMethod.Get, beta ? AllReleasesUrl : ReleasesUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var resp = await _http.SendAsync(req, timeout.Token);
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(timeout.Token);
            // A private repo answers 404 to an unauthenticated request
            if (status is < 200 or >= 300)
                return new CheckResult.Failed(status == 404 ? "No public releases found" : $"Update check failed (HTTP {status})");
            return Interpret(body, beta, currentVersion, MsiInstall.IsMsiInstall);
        }
        catch (Exception e)
        {
            return new CheckResult.Failed($"Update check failed: {e.Message}");
        }
    }

    /// The decision on a releases payload, separated from the fetch so it is testable.
    public static CheckResult Interpret(string body, bool beta, string currentVersion, bool msi = false)
    {
        var release = SelectRelease(Json.Parse(body), beta);
        if (release is null)
            Diag.Log.Error("updates.payload_unparsed", "releases response was not the expected JSON",
                new() { ["bytes"] = body.Length.ToString(), ["channel"] = beta ? "beta" : "stable" });
        if (release is null || Json.Str(release["tag_name"]) is not { } tag)
            return new CheckResult.Failed(beta ? "No releases found" : "Update check failed: unexpected response");
        var latest = tag.StartsWith('v') ? tag[1..] : tag;
        var page = Json.Str(release["html_url"]);
        if (!IsNewer(latest, currentVersion) || page is null || !Uri.TryCreate(page, UriKind.Absolute, out _))
            return new CheckResult.UpToDate();
        var (package, checksum) = SelectAssets(release, latest, msi);
        return new CheckResult.Available(latest, page, package, checksum);
    }

    /// Stable reads one release; beta takes the newest non-draft by version, not list order,
    /// so a stable hotfix outranks older betas.
    public static JsonObject? SelectRelease(JsonNode? payload, bool beta)
    {
        if (!beta) return payload as JsonObject;
        if (payload is not JsonArray list) return null;
        JsonObject? best = null;
        foreach (var r in list.OfType<JsonObject>())
        {
            if (Json.Bool(r["draft"]) == true) continue;
            if (best is null || VersionCompare.IsNewer(TagVersion(r), TagVersion(best))) best = r;
        }
        return best;
    }

    /// Exactly the zip (or, for an MSI install, the .msi) and its `.sha256`, from this repository's
    /// release downloads. Anything else is not an update this app will install.
    public static (ReleaseAsset? Package, ReleaseAsset? Checksum) SelectAssets(JsonObject release, string version, bool msi = false)
    {
        var name = msi ? MsiName(version) : PackageName(version);
        ReleaseAsset? Find(string wanted) =>
            (release["assets"] as JsonArray)?.OfType<JsonObject>()
                .Where(a => Json.Str(a["name"]) == wanted && Json.Str(a["browser_download_url"]) is { } u && IsTrustedDownloadUrl(u))
                .Select(a => new ReleaseAsset(wanted, Json.Str(a["browser_download_url"])!))
                .FirstOrDefault();
        return (Find(name), Find(name + ".sha256"));
    }

    public static bool IsTrustedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        u.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.Ordinal);

    /// The hex digest for `fileName` from sha256sum-style text, or a lone digest.
    public static string? ParseSha256(string text, string fileName)
    {
        string? lone = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!IsDigest(parts[0])) continue;
            if (parts.Length == 1) { lone ??= parts[0].ToLowerInvariant(); continue; }
            if (parts[1].TrimStart('*').Trim() == fileName) return parts[0].ToLowerInvariant();
        }
        return lone;
    }

    private static bool IsDigest(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigit);

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool VerifyHash(string path, string expectedHex)
    {
        if (!IsDigest(expectedHex)) return false;
        var actual = Convert.FromHexString(Sha256Of(path));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHex));
    }

    /// When the running exe is signed, the new one must carry the same signer, trusted if ours
    /// is. An unsigned build (a local one) has no signer to pin, so the hash is the whole check.
    public static string? SignatureProblem(SignatureInfo running, SignatureInfo staged)
    {
        if (!running.Signed) return null;
        if (!staged.Signed) return "Update rejected: the new version is not signed";
        if (running.Trusted && !staged.Trusted) return "Update rejected: its signature is not trusted";
        if (!string.Equals(running.Subject, staged.Subject, StringComparison.Ordinal))
            return "Update rejected: unexpected signing identity";
        return null;
    }

    public static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    public static string ExeName => Path.GetFileName(Environment.ProcessPath ?? "RedLine.exe");

    /// A build run from a project's bin folder is updated with git, not by swapping bin away.
    public static bool IsDevelopmentBuild(string? dir = null)
    {
        var d = (dir ?? InstallDir).Replace('/', '\\');
        return d.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) ||
               d.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase);
    }

    /// Refuses a target whose removal would take more than RedLine with it.
    public static string? UnsafeTarget(string installDir, string exeName)
    {
        string full;
        try { full = Path.GetFullPath(installDir).TrimEnd('\\'); } catch { return "Install directory is not a valid path"; }
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full) return "Install directory is a drive root";
        string[] guarded =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (guarded.Where(g => g.Length > 0).Any(g => string.Equals(g.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)))
            return "Install directory is a shared system folder";
        if (!File.Exists(Path.Combine(full, exeName))) return $"{exeName} is not in the install directory";
        return null;
    }

    /// Downloads, verifies and stages beside the install directory. Nothing touches the
    /// installed copy until the caller runs Swap and exits.
    public async Task<StageResult> StageAsync(CheckResult.Available update, string? installDir = null,
                                              IProgress<string>? status = null, CancellationToken ct = default,
                                              string? exeName = null)
    {
        var target = Path.GetFullPath(installDir ?? InstallDir).TrimEnd('\\');
        var exe = exeName ?? ExeName;
        if (IsDevelopmentBuild(target)) return new StageResult.Failed("Development build; update with git pull instead");
        if (UnsafeTarget(target, exe) is { } unsafeWhy) return new StageResult.Failed(unsafeWhy);
        if (update.Package is not { } package || update.Checksum is not { } checksum)
            return new StageResult.Failed("This release has no Windows package; open the release page instead");
        var isMsi = package.Name == MsiName(update.Version);
        if ((package.Name != PackageName(update.Version) && !isMsi) || !IsTrustedDownloadUrl(package.Url) || !IsTrustedDownloadUrl(checksum.Url))
            return new StageResult.Failed("Update rejected: unexpected download");

        // Beside the target, so the swap is a rename on one volume rather than a copy
        var work = Path.Combine(Path.GetDirectoryName(target)!, ".redline-update-" + Guid.NewGuid().ToString("N")[..12]);
        StageResult Fail(string msg)
        {
            try { Directory.Delete(work, recursive: true); } catch { }
            return new StageResult.Failed(msg);
        }

        try
        {
            Directory.CreateDirectory(work);
            status?.Report("Downloading…");
            var zip = Path.Combine(work, package.Name);
            await DownloadAsync(package.Url, zip, MaxPackageBytes, ct);
            var sumFile = Path.Combine(work, checksum.Name);
            await DownloadAsync(checksum.Url, sumFile, MaxChecksumBytes, ct);

            status?.Report("Verifying…");
            if (ParseSha256(await File.ReadAllTextAsync(sumFile, ct), package.Name) is not { } expected)
                return Fail("Update rejected: the checksum file is not readable");
            if (!VerifyHash(zip, expected)) return Fail("Update rejected: checksum mismatch");

            if (isMsi)
            {
                // Authenticode covers an MSI as it does an exe, so the same signer rule applies
                var runningSig = _signatures.Inspect(Path.Combine(target, exe));
                if (SignatureProblem(runningSig, _signatures.Inspect(zip)) is { } msiWhy) return Fail(msiWhy);
                var self = Environment.ProcessId;
                return new StageResult.Ready(() => MsiInstall.RunAfterExit($"/i \"{zip}\" /qn /norestart", self, work), work);
            }

            var extracted = Path.Combine(work, "extracted");
            // ExtractToDirectory refuses entries that would land outside the destination
            ZipFile.ExtractToDirectory(zip, extracted);
            var staged = SingleRoot(extracted);
            var stagedExe = Path.Combine(staged, exe);
            if (!File.Exists(stagedExe)) return Fail($"Update rejected: no {exe} inside the package");

            var running = _signatures.Inspect(Path.Combine(target, exe));
            if (SignatureProblem(running, _signatures.Inspect(stagedExe)) is { } why) return Fail(why);

            var script = Path.Combine(work, "swap.ps1");
            await File.WriteAllTextAsync(script, SwapScript, ct);
            var pid = Environment.ProcessId;
            return new StageResult.Ready(() => LaunchSwap(script, pid, target, staged, work, exe), work);
        }
        catch (OperationCanceledException) { return Fail("Update cancelled"); }
        catch (Exception e) { return Fail($"Could not stage the update: {e.Message}"); }
    }

    private async Task DownloadAsync(string url, string path, long limit, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        // Redirects to GitHub's asset CDN are followed, but never off https
        if (resp.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("download left https");
        if (resp.Content.Headers.ContentLength is long n && n > limit) throw new InvalidOperationException("download too large");
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidOperationException("download too large");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// A zip holding one top-level folder is that folder; otherwise the extraction root itself.
    internal static string SingleRoot(string dir)
    {
        var dirs = Directory.GetDirectories(dir);
        return dirs.Length == 1 && Directory.GetFiles(dir).Length == 0 ? dirs[0] : dir;
    }

    /// The helper outlives this process: waits for exit, swaps with a rollback, relaunches.
    internal const string SwapScript = """
        param([int]$ProcessId, [string]$Target, [string]$Staged, [string]$Work, [string]$Exe)
        # RedLine update helper. Written and started by RedLine's own installer; safe to delete.
        for ($i = 0; $i -lt 150; $i++) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
        }
        $backup = "$Target.old-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $moved = $false
        for ($i = 0; $i -lt 20 -and -not $moved; $i++) {
            try { Move-Item -LiteralPath $Target -Destination $backup -ErrorAction Stop; $moved = $true }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if ($moved) {
            try { Move-Item -LiteralPath $Staged -Destination $Target -ErrorAction Stop }
            catch { Move-Item -LiteralPath $backup -Destination $Target -ErrorAction SilentlyContinue }
        }
        $exePath = Join-Path $Target $Exe
        if (Test-Path -LiteralPath $exePath) { Start-Process -FilePath $exePath }
        if ($moved -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
        """;

    private static void LaunchSwap(string script, int pid, string target, string staged, string work, string exe)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(File.Exists(shell) ? shell : "powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                                  "-File", script, "-ProcessId", pid.ToString(), "-Target", target,
                                  "-Staged", staged, "-Work", work, "-Exe", exe })
            psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    /// Once now and every 24 hours while enabled; the callback runs on a pool thread.
    public void StartDailyChecks(Func<Config> config, Action<CheckResult> onResult, string? currentVersion = null)
    {
        StopDailyChecks();
        if (!config().AutoCheckUpdates) return;
        _timer = new Timer(async _ =>
        {
            var c = config();
            if (!c.AutoCheckUpdates) return;
            onResult(await CheckAsync(currentVersion ?? CurrentVersion, c.UpdateChannel));
        }, null, TimeSpan.Zero, CheckInterval);
    }

    public void StopDailyChecks()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static string TagVersion(JsonObject release)
    {
        var tag = Json.Str(release["tag_name"]) ?? "0";
        return tag.StartsWith('v') ? tag[1..] : tag;
    }
}

/// WinVerifyTrust for trust, the embedded certificate for the signer. Revocation is not
/// fetched online, because GitHub is the only host this app contacts on its own.
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

    public SignatureInfo Inspect(string path)
    {
        string? subject;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch { return SignatureInfo.Unsigned; }
        return new SignatureInfo(true, Verify(path), subject);
    }

    private static bool Verify(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            Marshal.StructureToPtr(data, dataPtr, false);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            data = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
            data.dwStateAction = 2;        // WTD_STATEACTION_CLOSE
            Marshal.StructureToPtr(data, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            return result == 0;
        }
        catch { return false; }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
