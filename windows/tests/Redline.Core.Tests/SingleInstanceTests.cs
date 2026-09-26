using Redline.Core;

namespace Redline.Core.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _lock;

    public SingleInstanceTests()
    {
        _lock = Path.Combine(Path.GetTempPath(), "redline-tests-" + Guid.NewGuid(), "instance.lock");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_lock)!, true); } catch { }
    }

    [Fact]
    public void FirstClaimSucceeds()
    {
        using var first = SingleInstance.Claim(_lock);
        Assert.NotNull(first);
    }

    [Fact]
    public void SecondClaimIsRefusedWhileTheFirstIsHeld()
    {
        using var first = SingleInstance.Claim(_lock);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.Claim(_lock));
        GC.KeepAlive(first);
    }

    // Windows closes the handle when the holder goes away, so a crash must not lock out the
    // next launch. Disposing the token is the in-process equivalent.
    [Fact]
    public void LockIsReleasedWhenTheHolderGoesAway()
    {
        var first = SingleInstance.Claim(_lock);
        Assert.NotNull(first);
        first!.Dispose();
        using var second = SingleInstance.Claim(_lock);
        Assert.NotNull(second);
    }

    [Fact]
    public void MissingDirectoryIsCreated()
    {
        using var first = SingleInstance.Claim(_lock);
        Assert.NotNull(first);
        Assert.True(File.Exists(_lock));
    }

    /// Windows addition: each profile gets its own guard, so a test home never blocks the app.
    [Fact]
    public void DistinctLockPathsDoNotContend()
    {
        using var first = SingleInstance.Claim(_lock);
        using var other = SingleInstance.Claim(_lock + ".other");
        Assert.NotNull(first);
        Assert.NotNull(other);
        Assert.NotEqual(SingleInstance.MutexName(_lock), SingleInstance.MutexName(_lock + ".other"));
    }
}
