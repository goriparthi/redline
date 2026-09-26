using Redline.Core;

namespace Redline.Core.Tests;

public sealed class VersionCompareTests
{
    [Fact]
    public void StableOrdering()
    {
        Assert.True(VersionCompare.IsNewer("0.4.0", "0.3.3"));
        Assert.False(VersionCompare.IsNewer("0.3.3", "0.3.3"));
        Assert.False(VersionCompare.IsNewer("0.3.3", "0.10.0"));
    }

    [Fact]
    public void PrereleaseOrdersBelowItsRelease()
    {
        Assert.True(VersionCompare.IsNewer("0.4.0", "0.4.0-beta.1"));
        Assert.False(VersionCompare.IsNewer("0.4.0-beta.1", "0.4.0"));
        Assert.True(VersionCompare.IsNewer("0.4.0-beta.1", "0.3.3"));
        Assert.False(VersionCompare.IsNewer("0.3.3", "0.4.0-beta.1"));
    }

    [Fact]
    public void NumericPrereleaseIdentifiers()
    {
        Assert.True(VersionCompare.IsNewer("0.4.0-beta.10", "0.4.0-beta.2"));
        Assert.False(VersionCompare.IsNewer("0.4.0-beta.2", "0.4.0-beta.10"));
    }

    [Fact]
    public void MixedIdentifiers()
    {
        Assert.True(VersionCompare.IsNewer("0.4.0-beta", "0.4.0-1"));
        Assert.True(VersionCompare.IsNewer("0.4.0-beta.1.hotfix", "0.4.0-beta.1"));
    }

    [Fact]
    public void VPrefixTolerated()
    {
        Assert.True(VersionCompare.IsNewer("v0.4.0", "0.3.3"));
    }

    [Fact]
    public void NumericComparisonNotStringComparison()
    {
        // String comparison would call 0.10.0 older than 0.9.0
        Assert.True(VersionCompare.IsNewer("0.10.0", "0.9.0"));
        Assert.True(VersionCompare.IsNewer("1.0.0", "0.99.9"));
        Assert.False(VersionCompare.IsNewer("0.1.9", "0.2.0"));
    }

    [Fact]
    public void ShorterVersionsPadWithZero()
    {
        Assert.True(VersionCompare.IsNewer("0.3", "0.2.9"));
        Assert.False(VersionCompare.IsNewer("0.2", "0.2.0"));
    }
}
