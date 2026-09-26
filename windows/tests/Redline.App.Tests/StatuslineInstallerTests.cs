using System.Diagnostics;
using System.Text.Json.Nodes;
using Redline.App.Services;

namespace Redline.App.Tests;

public class StatuslineInstallerTests
{
    private static string FakeCtl(TempHome home) => home.Write("app/redlinectl.exe", "not really an exe");

    private static JsonObject Settings(TempHome home) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(StatuslineInstaller.SettingsPath(home.Path)))!;

    private static string? Command(TempHome home) => Settings(home)["statusLine"]?["command"]?.GetValue<string>();

    [Fact]
    public void InstallsIntoAnEmptyHomeWithoutAChain()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        var result = StatuslineInstaller.Install(home.Path, ctl);

        var installed = Assert.IsType<StatuslineInstaller.Result.Installed>(result);
        Assert.Null(installed.Chained);
        Assert.Equal($"\"{ctl}\" statusline", Command(home));
        Assert.Equal("command", Settings(home)["statusLine"]!["type"]!.GetValue<string>());
        Assert.True(StatuslineInstaller.IsInstalled(home.Path));
        Assert.True(StatuslineInstaller.IsWanted(home.Path));
        Assert.False(File.Exists(StatuslineInstaller.ChainPath(home.Path)));
    }

    [Fact]
    public void RerunningIsAlreadyInstalledAndNeverChainsToItself()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        StatuslineInstaller.Install(home.Path, ctl);
        Assert.IsType<StatuslineInstaller.Result.AlreadyInstalled>(StatuslineInstaller.Install(home.Path, ctl));
        Assert.Null(StatuslineInstaller.ChainedCommand(home.Path));
    }

    [Fact]
    public void PreservesAnExistingCommandAndTheRestOfTheFile()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        const string existing = "node \"C:\\tools\\line.js\" --pct 100% | findstr /v \"x\" & echo 'done'";
        home.Write(".claude/settings.json", new JsonObject
        {
            ["model"] = "opus",
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = existing, ["padding"] = 2 },
        }.ToJsonString());

        var installed = Assert.IsType<StatuslineInstaller.Result.Installed>(StatuslineInstaller.Install(home.Path, ctl));
        Assert.Equal(existing, installed.Chained);
        Assert.Equal(existing, StatuslineInstaller.ChainedCommand(home.Path));
        Assert.Equal($"\"{StatuslineInstaller.ScriptPath(home.Path)}\"", Command(home));
        var s = Settings(home);
        Assert.Equal("opus", s["model"]!.GetValue<string>());
        Assert.Equal(2, s["statusLine"]!["padding"]!.GetValue<int>());

        // A second run keeps the chain rather than wrapping the wrapper
        Assert.IsType<StatuslineInstaller.Result.AlreadyInstalled>(StatuslineInstaller.Install(home.Path, ctl));
        Assert.Equal(existing, StatuslineInstaller.ChainedCommand(home.Path));
    }

    [Fact]
    public void UninstallRestoresTheChainedCommandExactly()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        const string existing = "bash -c 'echo \"$(cat)\" | jq -r .model.display_name'";
        home.Write(".claude/settings.json", new JsonObject
        {
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = existing, ["padding"] = 0 },
        }.ToJsonString());
        StatuslineInstaller.Install(home.Path, ctl);

        Assert.True(StatuslineInstaller.Uninstall(home.Path));
        Assert.Equal(existing, Command(home));
        Assert.Equal(0, Settings(home)["statusLine"]!["padding"]!.GetValue<int>());
        Assert.False(StatuslineInstaller.IsInstalled(home.Path));
    }

    [Fact]
    public void UninstallWithNothingChainedRemovesTheEntry()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        home.Write(".claude/settings.json", "{\"theme\":\"dark\"}");
        StatuslineInstaller.Install(home.Path, ctl);

        Assert.True(StatuslineInstaller.Uninstall(home.Path));
        var s = Settings(home);
        Assert.Null(s["statusLine"]);
        Assert.Equal("dark", s["theme"]!.GetValue<string>());
    }

    [Fact]
    public void UninstallLeavesSomeoneElsesStatuslineAlone()
    {
        using var home = new TempHome();
        const string original = "{\"statusLine\":{\"type\":\"command\",\"command\":\"ccline\"}}";
        var path = home.Write(".claude/settings.json", original);
        Assert.False(StatuslineInstaller.Uninstall(home.Path));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void RepairRewiresWhenSomethingElseUnwired()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        StatuslineInstaller.Install(home.Path, ctl);
        // A Claude Code session writes back its in-memory settings without the entry
        home.Write(".claude/settings.json", "{\"model\":\"sonnet\"}");

        Assert.True(StatuslineInstaller.RepairIfNeeded(home.Path, ctl));
        Assert.True(StatuslineInstaller.IsInstalled(home.Path));
        Assert.Equal("sonnet", Settings(home)["model"]!.GetValue<string>());
        Assert.False(StatuslineInstaller.RepairIfNeeded(home.Path, ctl));
    }

    [Fact]
    public void RepairChainsAStatuslineTheUserChangedInTheMeantime()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        StatuslineInstaller.Install(home.Path, ctl);
        home.Write(".claude/settings.json", "{\"statusLine\":{\"type\":\"command\",\"command\":\"starship statusline\"}}");

        Assert.True(StatuslineInstaller.RepairIfNeeded(home.Path, ctl));
        Assert.Equal("starship statusline", StatuslineInstaller.ChainedCommand(home.Path));
        Assert.True(StatuslineInstaller.Uninstall(home.Path));
        Assert.Equal("starship statusline", Command(home));
    }

    [Fact]
    public void RepairDoesNothingWithoutTheMarker()
    {
        using var home = new TempHome();
        var ctl = FakeCtl(home);
        const string original = "{\"model\":\"opus\"}";
        var path = home.Write(".claude/settings.json", original);
        Assert.False(StatuslineInstaller.RepairIfNeeded(home.Path, ctl));
        Assert.Equal(original, File.ReadAllText(path));

        StatuslineInstaller.Install(home.Path, ctl);
        StatuslineInstaller.Uninstall(home.Path);
        StatuslineInstaller.RemoveScript(home.Path);
        Assert.False(StatuslineInstaller.IsWanted(home.Path));
        Assert.False(StatuslineInstaller.RepairIfNeeded(home.Path, ctl));
    }

    [Fact]
    public void RefusesToOverwriteSettingsThatDoNotParse()
    {
        using var home = new TempHome();
        const string broken = "{ \"model\": \"opus\", // a comment\n";
        var path = home.Write(".claude/settings.json", broken);
        Assert.IsType<StatuslineInstaller.Result.Failed>(StatuslineInstaller.Install(home.Path, FakeCtl(home)));
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public void FailsWithoutRedlinectl()
    {
        using var home = new TempHome();
        Assert.IsType<StatuslineInstaller.Result.Failed>(
            StatuslineInstaller.Install(home.Path, Path.Combine(home.Path, "missing.exe")));
        Assert.False(File.Exists(StatuslineInstaller.SettingsPath(home.Path)));
    }

    [Fact]
    public void RefusesAMultilineChain()
    {
        using var home = new TempHome();
        home.Write(".claude/settings.json", "{\"statusLine\":{\"type\":\"command\",\"command\":\"a\\nb\"}}");
        Assert.IsType<StatuslineInstaller.Result.Failed>(StatuslineInstaller.Install(home.Path, FakeCtl(home)));
    }

    [Fact]
    public void WrapperEscapesPercentInTheExePath()
    {
        var script = StatuslineInstaller.WrapperScript(@"C:\100%\redlinectl.exe");
        Assert.Contains(StatuslineInstaller.Marker, script);
        Assert.Contains("\"C:\\100%%\\redlinectl.exe\" statusline", script);
    }

    [Fact]
    public void WrapperHandsTheChainToRedlinectlLiterally()
    {
        using var home = new TempHome();
        // Stands in for redlinectl: prints the variable exactly as the wrapper set it
        var ctl = home.Write("app/fakectl.cmd", "@echo off\r\nset REDLINE_STATUSLINE_CHAIN\r\n");
        const string existing = "node \"C:\\a b\\x.js\" | more & echo 100% ^ !bang! <in >out";
        home.Write(".claude/settings.json", new JsonObject
        {
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = existing },
        }.ToJsonString());
        StatuslineInstaller.Install(home.Path, ctl);

        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /c \"\"" + StatuslineInstaller.ScriptPath(home.Path) + "\"\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal("REDLINE_STATUSLINE_CHAIN=" + existing, output.TrimEnd('\r', '\n'));
    }
}
