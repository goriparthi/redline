using RedLine.Core;

namespace RedLine.Core.Tests;

/// <summary>
/// The wire format is a contract between two languages, and the fixture is real output from
/// the Swift engine rather than something written by hand here. If the engine renames a field
/// this suite fails, which is the point: the alternative is a Windows app that quietly shows
/// zero because a key moved.
/// </summary>
public class SnapshotContractTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static Snapshot Load(string name)
    {
        var parsed = SnapshotJson.Parse(File.ReadAllText(FixturePath(name)));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void TheEnginesSnapshotParses()
    {
        var snapshot = Load("snapshot-headless.json");
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 14, 39, 4, TimeSpan.Zero),
                     snapshot.UpdatedAt);
        Assert.NotNull(snapshot.ClaudeLimitsAsOf);
    }

    [Fact]
    public void EveryLimitWindowSurvivesIncludingTheOneWithNoReset()
    {
        var snapshot = Load("snapshot-headless.json");
        Assert.Equal(3, snapshot.Limits.Count);

        // Codex reports a window with no reset time, and a reader that assumed one would
        // drop it. This is the regression: the engine used to lose it entirely.
        var codex = Assert.Single(snapshot.Limits, w => w.Provider == "Codex");
        Assert.Equal("five_hour", codex.Key);
        Assert.Equal(31, codex.Utilization);
        Assert.Null(codex.ResetsAt);

        Assert.Equal(2, snapshot.Limits.Count(w => w.Provider == "Claude"));
    }

    [Fact]
    public void TheWorstWindowIsTheOneATrayIconShouldShow()
    {
        var snapshot = Load("snapshot-headless.json");
        var worst = snapshot.Worst;
        Assert.NotNull(worst);
        Assert.Equal(42, worst.Utilization);
        Assert.Equal("Claude", worst.Provider);
    }

    [Fact]
    public void TotalsAndPerProviderBreakdownsAgree()
    {
        var snapshot = Load("snapshot-headless.json");
        Assert.NotNull(snapshot.Today);
        Assert.Equal(13740, snapshot.Today.Io);
        Assert.Equal(12900, snapshot.TodayByProvider["Claude"].Io);
        Assert.Equal(840, snapshot.TodayByProvider["Codex"].Io);
        Assert.Equal(snapshot.Today.Io,
                     snapshot.TodayByProvider.Values.Sum(t => t.Io));
    }

    /// <summary>
    /// An unpriced model makes the cost a floor rather than an answer, and this flag is the
    /// only thing standing between that and a figure presented as fact.
    /// </summary>
    [Fact]
    public void TheUnpricedFlagIsCarriedThrough()
    {
        var snapshot = Load("snapshot-headless.json");
        Assert.NotNull(snapshot.Today);
        Assert.True(snapshot.Today.HasUnpriced);
    }

    [Fact]
    public void WindowNamesMatchWhatTheMacAppCallsThem()
    {
        Assert.Equal("Session · 5h",
                     new LimitWindow { Key = "five_hour", Provider = "Claude" }.DisplayName);
        Assert.Equal("Week · all models",
                     new LimitWindow { Key = "seven_day", Provider = "Claude" }.DisplayName);
        Assert.Equal("Week",
                     new LimitWindow { Key = "seven_day", Provider = "Codex" }.DisplayName);
        Assert.Equal("Week · Opus",
                     new LimitWindow { Key = "seven_day_opus", Provider = "Claude" }.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"limits\": \"wrong type\"}")]
    public void UnreadableInputIsNullRatherThanAnException(string json)
    {
        Assert.Null(SnapshotJson.Parse(json));
    }

    /// <summary>A snapshot with no limits at all is an ordinary state, not a broken file.</summary>
    [Fact]
    public void AnEmptySnapshotIsStillASnapshot()
    {
        var snapshot = SnapshotJson.Parse("{\"updatedAt\":\"2026-08-21T00:00:00Z\"}");
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Limits);
        Assert.Null(snapshot.Worst);
    }

    [Fact]
    public void AgeIsReportedRatherThanJudged()
    {
        var snapshot = Load("snapshot-headless.json");
        var later = snapshot.UpdatedAt.AddMinutes(30);
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.AgeAt(later));
    }
}
