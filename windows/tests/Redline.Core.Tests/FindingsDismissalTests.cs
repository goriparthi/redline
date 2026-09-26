// A dismissal has to hide a finding everywhere at once, and has to expire. Both are the point.
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class FindingsDismissalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_787_000_000);

    private static Finding Finding(string id, FindingKind kind = FindingKind.FixNow) =>
        new(id, kind, FindingBasis.Measured, id, "d", Array.Empty<FindingEvidence>());

    private static FindingsReport Report(params string[] ids) =>
        new(Now, 14, 3, ids.Select(id => Finding(id)).ToList());

    [Fact]
    public void DismissedFindingIsHiddenUntilTheSnoozeExpires()
    {
        var d = new FindingsDismissals();
        d.Dismiss("mcp-unused", Now);
        Assert.True(d.IsHidden("mcp-unused", 14, Now));
        // A day short of the window is still hidden; a day past it is not
        Assert.True(d.IsHidden("mcp-unused", 14, Now.AddSeconds(13 * 86400)));
        Assert.False(d.IsHidden("mcp-unused", 14, Now.AddSeconds(15 * 86400)));
        Assert.False(d.IsHidden("reread-files", 14, Now));
    }

    /// The menu line and the panel both read `Visible`, so this is what keeps them agreeing
    [Fact]
    public void VisibleFiltersAndCountsWhatItHid()
    {
        var d = new FindingsDismissals();
        d.Dismiss("a", Now);
        var v = Report("a", "b", "c").Visible(d, 14, Now);
        Assert.Equal(new[] { "b", "c" }, v.Findings.Select(f => f.Id));
        Assert.Equal(1, v.Hidden);
        Assert.Equal("2 findings · 2 to fix", v.Summary);
        // Everything else about the report survives the filter
        Assert.Equal(3, v.SessionsScanned);
        Assert.Equal(14, v.WindowDays);
    }

    /// A finding that is still true after the snooze must come back, not vanish quietly
    [Fact]
    public void AStillTrueFindingReturnsAfterTheSnooze()
    {
        var d = new FindingsDismissals();
        d.Dismiss("a", Now);
        var v = Report("a", "b").Visible(d, 14, Now.AddSeconds(20 * 86400));
        Assert.Equal(new[] { "a", "b" }, v.Findings.Select(f => f.Id));
        Assert.Equal(0, v.Hidden);
    }

    [Fact]
    public void EmptyAfterDismissingEverythingStillReportsWhatItHid()
    {
        var d = new FindingsDismissals();
        d.Dismiss("a", Now);
        d.Dismiss("b", Now);
        var v = Report("a", "b").Visible(d, 14, Now);
        Assert.True(v.IsEmpty);
        Assert.Equal(2, v.Hidden);
        Assert.Equal("no findings", v.Summary);
    }

    [Fact]
    public void RestoreAllBringsThemBackNow()
    {
        var d = new FindingsDismissals();
        d.Dismiss("a", Now);
        d.RestoreAll();
        Assert.True(d.IsEmpty);
        Assert.Single(Report("a").Visible(d, 14, Now).Findings);
    }

    /// The file must not accumulate a row for every finding the checks ever produced
    [Fact]
    public void PruneDropsExpiredSnoozesOnly()
    {
        var d = new FindingsDismissals();
        d.Dismiss("old", Now.AddSeconds(-30 * 86400));
        d.Dismiss("fresh", Now);
        d.Prune(14, Now);
        Assert.False(d.IsHidden("old", 14, Now));
        Assert.True(d.IsHidden("fresh", 14, Now));
        Assert.Single(d.Dismissed);
    }

    /// A clock that moved backwards must not read as a snooze that has already run out
    [Fact]
    public void AFutureDismissalIsTreatedAsHidden()
    {
        var d = new FindingsDismissals();
        d.Dismiss("a", Now.AddSeconds(86400));
        Assert.True(d.IsHidden("a", 14, Now));
    }

    [Fact]
    public void RoundTripsThroughDiskAndSurvivesAMissingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redline-dismiss-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "findings-dismissed.json");
            // Absent is empty, not an error
            Assert.True(FindingsDismissalStore.Load(path).IsEmpty);

            var d = new FindingsDismissals();
            d.Dismiss("mcp-unused", Now);
            Assert.True(FindingsDismissalStore.Save(d, path));
            Assert.Equal(d, FindingsDismissalStore.Load(path));

            // Garbage on disk degrades to empty rather than taking the panel out
            File.WriteAllText(path, "not json");
            Assert.True(FindingsDismissalStore.Load(path).IsEmpty);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SnoozeDaysIsValidatedAndOutOfRangeFallsBack()
    {
        Assert.Equal(14, new Config().FindingsSnoozeDays);
        var ok = JsonNode.Parse("{\"findingsSnoozeDays\": 30}")!.AsObject();
        Assert.Equal(30, Config.Apply(ok, new Config()).FindingsSnoozeDays);
        foreach (var bad in new[] { "0", "-1", "400", "\"sometimes\"" })
        {
            var json = JsonNode.Parse("{\"findingsSnoozeDays\": " + bad + "}")!.AsObject();
            Assert.True(Config.Apply(json, new Config()).FindingsSnoozeDays == 14,
                $"{bad} should have been rejected");
        }
    }
}
