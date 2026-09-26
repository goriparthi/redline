using Redline.App.Services;

namespace Redline.App.Tests;

public class LaunchAtLoginTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\RedLine\RedLine.exe";

    [Fact]
    public void EnableWritesTheQuotedExeUnderTheRedLineValue()
    {
        var key = new MemoryRunKey();
        var login = new LaunchAtLogin(key, Exe);
        Assert.False(login.IsEnabled);
        Assert.True(login.Enable());
        Assert.Equal($"\"{Exe}\"", key.Values["RedLine"]);
        Assert.True(login.IsEnabled);
        Assert.True(login.PointsHere);
    }

    [Fact]
    public void DisableRemovesOnlyOurValue()
    {
        var key = new MemoryRunKey();
        key.Set("OneDrive", "onedrive.exe");
        var login = new LaunchAtLogin(key, Exe);
        login.Enable();
        Assert.True(login.Disable());
        Assert.False(login.IsEnabled);
        Assert.Equal("onedrive.exe", key.Get("OneDrive"));
        // Disabling twice is not an error
        Assert.True(login.Disable());
    }

    [Fact]
    public void AValueForAnotherCopyIsEnabledButDoesNotPointHere()
    {
        var key = new MemoryRunKey();
        key.Set("RedLine", "\"D:\\old\\RedLine.exe\" --minimized");
        var login = new LaunchAtLogin(key, Exe);
        Assert.True(login.IsEnabled);
        Assert.False(login.PointsHere);
        key.Set("RedLine", Exe);
        Assert.True(login.PointsHere);
    }

    private sealed class ThrowingKey : IRunKey
    {
        public string? Get(string name) => throw new UnauthorizedAccessException();
        public void Set(string name, string value) => throw new UnauthorizedAccessException();
        public void Delete(string name) => throw new UnauthorizedAccessException();
    }

    [Fact]
    public void RegistryFailuresReportFalseRatherThanThrow()
    {
        var login = new LaunchAtLogin(new ThrowingKey(), Exe);
        Assert.False(login.IsEnabled);
        Assert.False(login.Enable());
        Assert.False(login.Disable());
    }
}
