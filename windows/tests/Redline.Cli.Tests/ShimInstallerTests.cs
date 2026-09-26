// Installing the shim. It must never claim a file it did not write, and the PATH edit must keep
// every other entry and the value's registry type.
using Redline.Cli;

namespace Redline.Cli.Tests;

public sealed class ShimInstallerTests : IDisposable
{
    private readonly string _home;
    private readonly string _bin;
    private readonly FakeStore _store = new();
    private const string Target = @"C:\Program Files\RedLine\redlinectl.exe";

    public ShimInstallerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "redline-install-" + Guid.NewGuid());
        Directory.CreateDirectory(_home);
        _bin = ShimInstaller.BinDir(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
    }

    private ShimInstallResult Install(string target = Target) => ShimInstaller.Install(target, _home, _store);

    [Fact]
    public void AFreshInstallWritesTheForwarderAndFrontsThePath()
    {
        _store.Value = @"C:\Users\me\AppData\Local\Programs\Ollama;C:\tools";
        var r = Install();
        Assert.Equal(ShimInstallStatus.Installed, r.Status);
        Assert.True(r.Succeeded);
        Assert.True(r.PathChanged);
        Assert.Equal(Path.Combine(_bin, "ollama.cmd"), r.ShimPath);

        var text = File.ReadAllText(r.ShimPath);
        Assert.Contains(ShimInstaller.Marker, text[..Math.Min(300, text.Length)]);
        Assert.Contains($"\"{Target}\" ollama-shim %*", text);
        Assert.True(ShimInstaller.IsInstalled(_home));

        Assert.Equal($@"{_bin};C:\Users\me\AppData\Local\Programs\Ollama;C:\tools", _store.Value);
        Assert.True(_store.Expandable);
        Assert.Equal(1, _store.Broadcasts);
    }

    [Fact]
    public void ALaterCopyOfTheEntryMovesToTheFront()
    {
        _store.Value = $@"C:\a;{_bin}\;C:\b";
        Install();
        Assert.Equal($@"{_bin};C:\a;C:\b", _store.Value);
    }

    [Fact]
    public void AnEntryAlreadyInFrontIsLeftAlone()
    {
        _store.Value = $@"{_bin};C:\a";
        var r = Install();
        Assert.False(r.PathChanged);
        Assert.Equal(0, _store.Writes);
        Assert.Equal(0, _store.Broadcasts);
    }

    [Fact]
    public void ThePathKeepsItsRegistryType()
    {
        _store.Value = @"C:\a";
        _store.Expandable = false;
        Install();
        Assert.False(_store.Expandable);
    }

    [Fact]
    public void AMissingPathIsCreatedExpandable()
    {
        _store.Value = null;
        _store.Expandable = false;
        Install();
        Assert.Equal(_bin, _store.Value);
        Assert.True(_store.Expandable);
    }

    [Fact]
    public void AForeignFileIsNeverOverwritten()
    {
        Directory.CreateDirectory(_bin);
        var path = Path.Combine(_bin, "ollama.cmd");
        File.WriteAllText(path, "@echo off\r\nsomeone else's\r\n");
        var r = Install();
        Assert.Equal(ShimInstallStatus.ForeignFile, r.Status);
        Assert.False(r.Succeeded);
        Assert.Equal("@echo off\r\nsomeone else's\r\n", File.ReadAllText(path));
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void ReinstallingUpdatesAnOlderCopy()
    {
        Install(@"C:\old\redlinectl.exe");
        Assert.Equal(ShimInstallStatus.Updated, Install().Status);
        Assert.Contains(Target, File.ReadAllText(ShimInstaller.ShimPath(_home)));
        Assert.Equal(ShimInstallStatus.Unchanged, Install().Status);
    }

    [Fact]
    public void AnOllamaOnTheMachinePathIsWarnedAbout()
    {
        var machine = Path.Combine(_home, "machine");
        Directory.CreateDirectory(machine);
        File.WriteAllText(Path.Combine(machine, "ollama.exe"), "MZ");
        _store.Machine = @"C:\Windows;" + machine;
        Assert.Contains("system PATH", Install().Warning);
        _store.Machine = @"C:\Windows";
        Assert.Null(Install().Warning);
    }

    [Fact]
    public void ThePortableSpellingCountsAsTheSameDirectory()
    {
        var real = ShimInstaller.BinDir();
        Assert.True(ShimInstaller.SameDir(ShimInstaller.PortableEntry, real));
        Assert.Null(ShimInstaller.Fronted(ShimInstaller.PortableEntry + @";C:\a", real, ShimInstaller.PortableEntry));
        Assert.Equal(ShimInstaller.PortableEntry + @";C:\a",
                     ShimInstaller.Fronted(@"C:\a;" + real, real, ShimInstaller.PortableEntry));
    }

    [Fact]
    public void UninstallRemovesOnlyOurFileAndItsPathEntry()
    {
        _store.Value = @"C:\a";
        Install();
        var r = ShimInstaller.Uninstall(_home, _store);
        Assert.Equal(ShimInstallStatus.Removed, r.Status);
        Assert.False(File.Exists(r.ShimPath));
        Assert.True(r.PathChanged);
        Assert.Equal(@"C:\a", _store.Value);

        Assert.Equal(ShimInstallStatus.NotInstalled, ShimInstaller.Uninstall(_home, _store).Status);
    }

    [Fact]
    public void UninstallKeepsThePathWhileOtherToolsLiveThere()
    {
        _store.Value = @"C:\a";
        Install();
        File.WriteAllText(Path.Combine(_bin, "other.exe"), "MZ");
        var r = ShimInstaller.Uninstall(_home, _store);
        Assert.Equal(ShimInstallStatus.Removed, r.Status);
        Assert.False(r.PathChanged);
        Assert.StartsWith(_bin, _store.Value);
    }

    [Fact]
    public void UninstallLeavesAForeignFile()
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "ollama.cmd"), "not ours");
        Assert.Equal(ShimInstallStatus.ForeignFile, ShimInstaller.Uninstall(_home, _store).Status);
        Assert.True(File.Exists(Path.Combine(_bin, "ollama.cmd")));
    }

    private sealed class FakeStore : IUserPathStore
    {
        public string? Value;
        public bool Expandable = true;
        public string? Machine;
        public int Writes;
        public int Broadcasts;

        public (string? Value, bool Expandable) ReadUserPath() => (Value, Expandable);
        public void WriteUserPath(string value, bool expandable) { Value = value; Expandable = expandable; Writes++; }
        public string? ReadMachinePath() => Machine;
        public void Broadcast() => Broadcasts++;
    }
}
