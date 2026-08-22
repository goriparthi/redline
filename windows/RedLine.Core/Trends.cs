using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedLine.Core;

/// <summary>
/// One bucket of the daily series, as `redline trends --json` publishes it. The label is the
/// engine's, so two shells cannot print one day two ways.
/// </summary>
public sealed record TrendPoint
{
    [JsonPropertyName("day")] public string Day { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("tokens")] public long Tokens { get; init; }
    [JsonPropertyName("cost_usd")] public double Cost { get; init; }
}

public sealed record ProviderTrend
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("tokens")] public long Tokens { get; init; }
    [JsonPropertyName("cost_usd")] public double Cost { get; init; }
    [JsonPropertyName("points")] public IReadOnlyList<TrendPoint> Points { get; init; } = [];
}

public sealed record ModelShare
{
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("tokens")] public long Tokens { get; init; }
    [JsonPropertyName("cost_usd")] public double Cost { get; init; }

    /// <summary>
    /// False when RedLine has no price for this model. Its cost is then not a small number,
    /// it is no number at all, and showing one would be inventing it.
    /// </summary>
    [JsonPropertyName("priced")] public bool Priced { get; init; }
}

/// <summary>
/// The shape of the last N days. Every figure here was worked out by the engine, including
/// how the days are bucketed and how often the axis should carry a label.
/// </summary>
public sealed record TrendReport
{
    [JsonPropertyName("days")] public int Days { get; init; }
    [JsonPropertyName("label_every_days")] public int LabelEveryDays { get; init; } = 1;
    [JsonPropertyName("day_basis")] public string DayBasis { get; init; } = "";
    [JsonPropertyName("tokens")] public long Tokens { get; init; }
    [JsonPropertyName("cost_usd")] public double Cost { get; init; }

    /// <summary>True when something in the window had no price, making every total a floor.</summary>
    [JsonPropertyName("has_unpriced")] public bool HasUnpriced { get; init; }

    /// <summary>Every provider added together, which is what one chart draws.</summary>
    [JsonPropertyName("series")] public IReadOnlyList<TrendPoint> Series { get; init; } = [];

    [JsonPropertyName("providers")]
    public IReadOnlyList<ProviderTrend> Providers { get; init; } = [];

    [JsonPropertyName("models")] public IReadOnlyList<ModelShare> Models { get; init; } = [];

    /// <summary>Why there is nothing to draw, when there is nothing to draw.</summary>
    [JsonIgnore] public string Problem { get; init; } = "";

    [JsonIgnore] public bool Available => Problem.Length == 0;

    /// <summary>
    /// Nothing recorded is not the same as nothing readable, and both happen. A machine with
    /// no history at all has no buckets rather than a row of zeros, because with no provider
    /// there is nothing to bucket.
    /// </summary>
    [JsonIgnore] public bool IsEmpty => Series.All(p => p.Tokens == 0);

    public TrendPoint? Busiest =>
        Series.Count == 0 ? null : Series.MaxBy(p => p.Tokens) is { Tokens: > 0 } best
            ? best : null;

    /// <summary>
    /// The line above the chart. An unpriced model makes the cost a floor, and the words say
    /// so rather than leaving a reader to assume the figure is the whole bill.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!Available) return Problem;
            if (IsEmpty) return $"Nothing recorded in the last {Days} days";
            var cost = Formatting.Cost(Cost);
            return HasUnpriced
                ? $"{Formatting.Tokens(Tokens)} over {Days} days, at least {cost}"
                : $"{Formatting.Tokens(Tokens)} over {Days} days, {cost}";
        }
    }

    public static TrendReport Unavailable(string problem) => new() { Problem = problem };
}

public static class TrendJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Null for anything unreadable, so the caller can say so rather than draw an
    /// empty chart that looks like a quiet fortnight.</summary>
    public static TrendReport? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TrendReport>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One bar of the daily chart. An empty label is a bar the axis does not name.</summary>
public sealed record ChartBar(string Day, string Label, long Tokens, double Cost, double Share);

/// <summary>
/// Turning the series into bars. Here rather than in the window because it is arithmetic with
/// an edge case at either end, and this is where the tests are.
/// </summary>
public static class TrendChart
{
    /// <summary>
    /// Bars measured against the busiest day, so the tallest is full height and the rest are
    /// read against it. A day with nothing gets a zero share rather than being left out.
    /// </summary>
    public static IReadOnlyList<ChartBar> Bars(TrendReport report)
    {
        var series = report.Series;
        if (series.Count == 0) return [];

        var peak = series.Max(p => p.Tokens);
        var every = Math.Max(1, report.LabelEveryDays);
        var last = series.Count - 1;

        // Counted back from the newest day, so today is always named. Counting forward
        // labels the oldest and can leave the right hand end blank, which is the end
        // someone is actually looking at.
        return series.Select((point, index) => new ChartBar(
            point.Day,
            (last - index) % every == 0 ? point.Label : "",
            point.Tokens,
            point.Cost,
            peak > 0 ? (double)point.Tokens / peak : 0)).ToList();
    }

    /// <summary>
    /// The biggest few models, and how many were left out. Everything is kept in the report;
    /// this only decides what fits.
    /// </summary>
    public static (IReadOnlyList<ModelShare> Shown, int Hidden) TopModels(
        TrendReport report, int limit = 6)
    {
        var shown = report.Models.Take(Math.Max(0, limit)).ToList();
        return (shown, Math.Max(0, report.Models.Count - shown.Count));
    }

    /// <summary>What a model's share of the window is, for a bar beside its name.</summary>
    public static double Share(ModelShare model, TrendReport report) =>
        report.Tokens > 0 ? (double)model.Tokens / report.Tokens : 0;

    /// <summary>
    /// What to print for a model's cost. An unpriced model reads as "n/a", the same wording
    /// the macOS mix uses, because a zero there would be a number nobody measured.
    /// </summary>
    public static string CostOf(ModelShare model) =>
        model.Priced ? Formatting.Cost(model.Cost) : "n/a";
}

/// <summary>
/// The recorded history, through the engine. Nothing here buckets a day, adds a column or
/// decides when a cost is a floor: it asks, and draws what comes back.
/// </summary>
public sealed class TrendStore(Engine? engine = null)
{
    private readonly Engine engine = engine ?? new Engine();

    /// <summary>
    /// Reading nothing is an ordinary answer: the engine exits 20 with an empty series when
    /// there is no history yet, which is not a failure and must not be shown as one.
    /// </summary>
    public TrendReport Read(int days = 14)
    {
        var result = engine.Run("trends", "--days",
                                days.ToString(CultureInfo.InvariantCulture), "--json");
        if (!result.Ran) return TrendReport.Unavailable(Problem(result));

        return TrendJson.Parse(result.Output)
            ?? TrendReport.Unavailable("redline did not report its history in a readable shape");
    }

    private static string Problem(EngineResult result)
    {
        var said = result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim();
        return said.Length > 0 ? said : "redline could not be run";
    }
}
