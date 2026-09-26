// Whether an MSI owns this install, and running msiexec once RedLine has exited. The MSI writes
// its ProductCode under HKCU\Software\RedLine\Installer and removes it on uninstall.
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

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
        param([int]$ProcessId, [string]$MsiExec, [string]$Arguments, [string]$Cleanup)
        # RedLine installer helper: runs msiexec once RedLine has exited. Safe to delete.
        for ($i = 0; $i -lt 150; $i++) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
        }
        Start-Process -FilePath $MsiExec -ArgumentList $Arguments -Wait
        if ($Cleanup) { Remove-Item -LiteralPath $Cleanup -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    /// Starts a hidden helper that waits for `pid` to exit, then runs msiexec with `arguments`.
    public static void RunAfterExit(string arguments, int pid, string? cleanup = null)
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
                                  "-File", script, "-ProcessId", pid.ToString(), "-MsiExec", MsiExec,
                                  "-Arguments", arguments, "-Cleanup", cleanup ?? "" })
            psi.ArgumentList.Add(a);
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
