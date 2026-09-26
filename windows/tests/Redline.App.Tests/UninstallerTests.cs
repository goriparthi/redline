using Redline.App.Services;

namespace Redline.App.Tests;

public class UninstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "rl-uninstall-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Args, int Pid)> _msi = new();
    private readonly List<string> _deleted = new();

    public UninstallerTests() => Directory.CreateDirectory(_home);
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private sealed class NoShim : IOllamaShimRemover
    {
        public IReadOnlyList<string> RemoveIfOurs(string? home = null) => Array.Empty<string>();
    }

    private Uninstaller Make(string? productCode) => new(_home, new LaunchAtLogin(new MemoryRunKey(), @"C:\x\RedLine.exe"),
        new MemoryCredentialStore(), new NoShim(), (dir, _) => _deleted.Add(dir), () => productCode,
        (args, pid) => _msi.Add((args, pid)));

    [Fact]
    public void AnMsiInstallIsRemovedByWindowsInstaller()
    {
        var report = Make("{859B3A4C-3298-49F8-BE26-2845C71F3693}").Run(purge: false, installDir: @"C:\nowhere\RedLine");
        Assert.True(report.InstallDirScheduled);
        var (args, _) = Assert.Single(_msi);
        Assert.Equal("/x {859B3A4C-3298-49F8-BE26-2845C71F3693} /qn /norestart", args);
        Assert.Empty(_deleted);
    }

    [Fact]
    public void AZipInstallDeletesItsOwnFolder()
    {
        var dir = Path.Combine(_home, "Programs", "RedLine");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Updates.ExeName), "");
        Make(null).Run(purge: false, installDir: dir);
        Assert.Empty(_msi);
        Assert.Equal(Path.GetFullPath(dir), Assert.Single(_deleted));
    }
}
