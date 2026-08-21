namespace RedLine.Core;

/// <summary>
/// The status vocabulary, matching RLStatus in the engine. Shape and word, never colour
/// alone: the same rule the macOS app follows, and the reason each level carries a phrase.
/// </summary>
public enum TrayLevel
{
    Healthy,
    Approaching,
    AtLimit,
    /// <summary>Nothing has been read yet. Not the same as a reading of zero.</summary>
    Unknown,
    /// <summary>The last known reading, past the point where it can be called current.</summary>
    Stale,
}

public sealed record TrayView(string Title, string Phrase, TrayLevel Level, string Detail);

/// <summary>
/// Turns a snapshot into the handful of strings a tray icon shows. Deliberately here rather
/// than in the app: this is where a wrong figure would reach a person, so it is where the
/// tests are.
/// </summary>
public static class TrayPresenter
{
    /// <summary>Defaults matching the engine's own.</summary>
    public const double ApproachingPercent = 60;
    public const double AtLimitPercent = 85;

    /// <summary>
    /// How old a reading may be before it is drawn as the last known one rather than as fact.
    /// Matches the engine's StatuslineFeed.freshFor, so the two agree about "current".
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);

    public static TrayView From(Snapshot? snapshot, DateTimeOffset now,
                                double approaching = ApproachingPercent,
                                double atLimit = AtLimitPercent)
    {
        if (snapshot is null)
        {
            return new TrayView("RedLine", "Nothing read yet", TrayLevel.Unknown,
                                "Run redline watch to start recording");
        }

        var stale = snapshot.AgeAt(now) > FreshFor;
        var worst = snapshot.Worst;
        var today = snapshot.Today;

        // Cost first only when there is no limit to report: a percentage is the thing someone
        // is actually watching for, and a cost is what they review later.
        var title = worst is not null
            ? $"{Math.Round(worst.Utilization)}%"
            : today is not null ? Formatting.Tokens(today.Io) : "RedLine";

        var level = worst is null
            ? (stale ? TrayLevel.Stale : TrayLevel.Unknown)
            : stale ? TrayLevel.Stale : Classify(worst.Utilization, approaching, atLimit);

        var phrase = level switch
        {
            TrayLevel.Healthy => "Healthy",
            // Report the fact, never scold
            TrayLevel.Approaching => "Approaching your limit",
            TrayLevel.AtLimit => "Limit reached",
            TrayLevel.Stale => "Last known reading",
            _ => "Not checked",
        };

        return new TrayView(title, phrase, level, Detail(snapshot, worst));
    }

    public static TrayLevel Classify(double utilization, double approaching, double atLimit)
    {
        if (utilization >= atLimit) return TrayLevel.AtLimit;
        if (utilization >= approaching) return TrayLevel.Approaching;
        return TrayLevel.Healthy;
    }

    private static string Detail(Snapshot snapshot, LimitWindow? worst)
    {
        var parts = new List<string>();
        if (worst is not null) parts.Add($"{worst.Provider} {worst.DisplayName}");
        if (snapshot.Today is { } today)
        {
            // An unpriced model makes the cost a floor, and saying so is the difference
            // between a number and a guess
            var cost = Formatting.Cost(today.Cost);
            parts.Add(today.HasUnpriced
                ? $"{Formatting.Tokens(today.Io)} today, at least {cost}"
                : $"{Formatting.Tokens(today.Io)} today, {cost}");
        }
        return parts.Count == 0 ? "No usage recorded" : string.Join(" · ", parts);
    }
}
