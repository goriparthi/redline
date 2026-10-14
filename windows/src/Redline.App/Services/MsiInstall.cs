// Whether an MSI owns this install, and running msiexec once RedLine has exited. The MSI writes
// its ProductCode under HKCU\Software\RedLine\Installer and removes it on uninstall.
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Redline.Core;

namespace Redline.App.Services;

public static class MsiInstall
{
    public const string KeyPath = @"Software\RedLine\Installer";

    /// The installed product's code, or null for a zip or source install.
    public static string? ProductCode(Func<string?>? read = null)
    {
        var raw = read is not null ? read() : ReadRegistry();
        return raw is { Length: 38 } s && s[0] == '{' && s[^1] == '}' && Guid.TryParse(s, out _) ? s : null;
    }

    public static bool IsMsiInstall => ProductCode() is not null;

    private static string? ReadRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("ProductCode") as string;
        }
        catch { return null; }
    }

    public static string MsiExec =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    internal const string WaitScript = """
        param([int]$ProcessId, [string]$MsiExec, [string]$Arguments, [string]$Cleanup, [string]$Relaunch, [string]$Result)
        # RedLine installer helper: runs msiexec once RedLine has exited. Safe to delete.
        for ($i = 0; $i -lt 150; $i++) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
        }
        # 1618 means another install holds Windows Installer, so wait for it rather than give up
        $code = -1; $attempts = 0
        do {
            if ($attempts -gt 0) { Start-Sleep -Seconds 10 }
            $attempts++
            $code = (Start-Process -FilePath $MsiExec -ArgumentList $Arguments -Wait -PassThru).ExitCode
        } while ($code -eq 1618 -and $attempts -lt 30)
        if ($Result) {
            @{ exitCode = $code; attempts = $attempts; at = (Get-Date).ToUniversalTime().ToString('o') } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath $Result -Encoding UTF8 -ErrorAction SilentlyContinue
        }
        # A good install starts RedLine itself; if nothing is running from the folder, start what is there
        if ($Relaunch) {
            Start-Sleep -Seconds 5
            $dir = Split-Path -Parent $Relaunch
            $name = [IO.Path]::GetFileNameWithoutExtension($Relaunch)
            $running = Get-Process -Name $name -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) }
            if (-not $running -and (Test-Path -LiteralPath $Relaunch)) { Start-Process -FilePath $Relaunch }
        }
        if ($Cleanup) { Remove-Item -LiteralPath $Cleanup -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    /// Where the helper leaves msiexec's outcome and verbose log for the next launch to read.
    public static string ResultPath(string? home = null) => Path.Combine(RedlineHome.DataDir(home), "update-result.json");
    public static string LogPath(string? home = null) => Path.Combine(RedlineHome.DataDir(home), "update-msi.log");

    /// What the last update's msiexec returned. 3010 and 1641 succeeded and want a restart.
    public sealed record Outcome(int ExitCode, int Attempts)
    {
        public bool Succeeded => ExitCode is 0 or 3010 or 1641;
    }

    /// Reads and removes the helper's result file; null when there is none or it is unreadable.
    public static Outcome? TakeResult(string? path = null)
    {
        path ??= ResultPath();
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            File.Delete(path);
            return ParseResult(text);
        }
        catch { return null; }
    }

    internal static Outcome? ParseResult(string text)
    {
        try
        {
            if (JsonNode.Parse(text.Trim('\uFEFF', ' ', '\r', '\n')) is not JsonObject o) return null;
            return o["exitCode"]?.GetValue<int>() is int code
                ? new Outcome(code, o["attempts"]?.GetValue<int>() ?? 1)
                : null;
        }
        catch { return null; }
    }

    /// Starts a hidden helper that waits for `pid` to exit, then runs msiexec with `arguments`.
    /// With `relaunch`, that exe is started again if the install leaves nothing running.
    public static void RunAfterExit(string arguments, int pid, string? cleanup = null,
                                    string? relaunch = null, string? resultFile = null)
    {
        var script = Path.Combine(Path.GetTempPath(), "redline-msi-" + Guid.NewGuid().ToString("N")[..8] + ".ps1");
        File.WriteAllText(script, WaitScript);
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(File.Exists(shell) ? shell : "powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                                  "-File", script, "-ProcessId", pid.ToString(), "-MsiExec", MsiExec, "-Arguments", arguments })
            psi.ArgumentList.Add(a);
        // An empty "" can reach -File as a missing value, so optional parameters are only passed when set
        foreach (var (name, value) in new[] { ("-Cleanup", cleanup), ("-Relaunch", relaunch), ("-Result", resultFile) })
            if (!string.IsNullOrEmpty(value)) { psi.ArgumentList.Add(name); psi.ArgumentList.Add(value); }
        using var _ = Process.Start(psi);
    }

    /// Other RedLine processes, stopped before the MSI removes their files.
    public static void StopOtherInstances()
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("RedLine"))
        {
            using (p)
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }
    }
}
