// What "Uninstall RedLine..." does: the removals scripts/uninstall.sh performs, then the install
// directory itself once this process has exited. Claude, Codex and Ollama files are never touched.
using System.IO;
using System.Diagnostics;
using Redline.Core;

namespace Redline.App.Services;

/// Removes the ollama shim only when it is RedLine's own; a real binary parked there stays.
public interface IOllamaShimRemover
{
    /// The paths removed, empty when nothing there was ours.
    IReadOnlyList<string> RemoveIfOurs(string? home = null);
}

/// Redline.Cli's own ShimInstaller: removes ~/.local/bin/ollama.cmd only when it carries the
/// shim marker, and takes that directory back off the user PATH when nothing else lives there.
public sealed class CliShimRemover : IOllamaShimRemover
{
    public IReadOnlyList<string> RemoveIfOurs(string? home = null)
    {
        var result = Redline.Cli.ShimInstaller.Uninstall(home);
        if (result.Status == Redline.Cli.ShimInstallStatus.Failed)
            Diag.Log.Error("uninstall.shim_failed", "could not remove the ollama shim", new() { ["error"] = result.Message ?? "" });
        return result.Status == Redline.Cli.ShimInstallStatus.Removed ? new[] { result.ShimPath } : Array.Empty<string>();
    }
}

public sealed record UninstallReport(IReadOnlyList<string> Removed, IReadOnlyList<string> Problems, bool InstallDirScheduled);

public sealed class Uninstaller
{
    private readonly string? _home;
    private readonly LaunchAtLogin _login;
    private readonly ICredentialStore _store;
    private readonly IOllamaShimRemover _shim;
    private readonly Action<string, int>? _scheduleDelete;
    private readonly Func<string?> _productCode;
    private readonly Action<string, int> _runMsi;

    public Uninstaller(string? home = null, LaunchAtLogin? login = null, ICredentialStore? store = null,
                       IOllamaShimRemover? shim = null, Action<string, int>? scheduleDelete = null,
                       Func<string?>? productCode = null, Action<string, int>? runMsi = null)
    {
        _productCode = productCode ?? (() => MsiInstall.ProductCode());
        _runMsi = runMsi ?? ((args, pid) => MsiInstall.RunAfterExit(args, pid));
        _home = home;
        _login = login ?? new LaunchAtLogin();
        _store = store ?? WindowsCredentialStore.Shared;
        _shim = shim ?? new CliShimRemover();
        _scheduleDelete = scheduleDelete;
    }

    /// Everything RedLine put outside its install directory, then the directory after exit.
    /// The caller signs OAuth out first and exits the process straight after.
    public UninstallReport Run(bool purge, string? installDir = null)
    {
        var removed = new List<string>();
        var problems = new List<string>();

        if (_login.Disable()) removed.Add($@"HKCU\{RegistryRunKey.SubKey}\{LaunchAtLogin.ValueName}");
        else problems.Add("Could not remove the login item");

        TokenStore.Clear(_store);
        removed.Add($"Credential Manager item \"{TokenStore.Target}\"");

        // Unconditional on Windows, unlike macOS: the feed runs redlinectl.exe from the install
        // directory, so leaving it wired would break the statusline once that directory goes.
        if (StatuslineInstaller.Uninstall(_home)) removed.Add(StatuslineInstaller.SettingsPath(_home) + " statusLine");
        else if (StatuslineInstaller.IsInstalled(_home)) problems.Add("Could not unwire the Claude usage feed");
        StatuslineInstaller.RemoveScript(_home);

        // The shim calls into the install directory too, so it goes for the same reason
        removed.AddRange(_shim.RemoveIfOurs(_home));

        if (purge)
        {
            var root = _home ?? RedlineHome.Url;
            foreach (var dir in new[] { RedlineHome.Join(root, ".config/redline"), RedlineHome.DataDir(root) })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    Directory.Delete(dir, recursive: true);
                    removed.Add(dir);
                }
                catch (Exception e) { problems.Add($"Could not remove {dir}: {e.Message}"); }
            }
        }

        var scheduled = false;
        var target = installDir ?? Updates.InstallDir;
        // An MSI install is removed by Windows Installer, so Settings > Apps loses its entry too
        if (_productCode() is { } code)
        {
            try
            {
                _runMsi($"/x {code} /qn /norestart", Environment.ProcessId);
                scheduled = true;
            }
            catch (Exception e) { problems.Add($"Could not start the Windows Installer removal: {e.Message}"); }
        }
        else if (Updates.IsDevelopmentBuild(target))
        {
            problems.Add("Development build; the build folder was left in place");
        }
        else if (Updates.UnsafeTarget(target, Updates.ExeName) is { } why)
        {
            problems.Add($"Install directory left in place: {why}");
        }
        else
        {
            try
            {
                (_scheduleDelete ?? ScheduleDelete)(Path.GetFullPath(target), Environment.ProcessId);
                scheduled = true;
            }
            catch (Exception e) { problems.Add($"Could not schedule removal of {target}: {e.Message}"); }
        }
        return new UninstallReport(removed, problems, scheduled);
    }

    internal const string DeleteScript = """
        param([int]$ProcessId, [string]$Target)
        # RedLine uninstall helper: removes the install directory once RedLine has exited.
        for ($i = 0; $i -lt 150; $i++) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
        }
        for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $Target); $i++) {
            Remove-Item -LiteralPath $Target -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $Target) { Start-Sleep -Milliseconds 500 }
        }
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    /// The helper lives in %TEMP%, outside the directory it deletes, and outlives this process.
    public static void ScheduleDelete(string target, int pid)
    {
        var script = Path.Combine(Path.GetTempPath(), "redline-uninstall-" + Guid.NewGuid().ToString("N")[..8] + ".ps1");
        File.WriteAllText(script, DeleteScript);
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(File.Exists(shell) ? shell : "powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                                  "-File", script, "-ProcessId", pid.ToString(), "-Target", target })
            psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }
}
