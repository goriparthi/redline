// What one provider's overview card says, derived once here rather than assembled inside a
// view, so which state wins and what a missing figure reads as can be tested.
namespace Redline.Core;

/// A provider's connection state, worst-news-first. The order is the order the builder
/// resolves them in, so it never reports "no usage" for a tool it cannot even reach.
public enum ProviderConnection
{
    /// The tool's own directory is not on this machine.
    NotInstalled,
    /// Installed, but switched off in the provider selection, so nothing is being read.
    NotRead,
    /// Installed and read, but the thing that serves it is not answering. Local only.
    Unreachable,
    /// Read, reachable, and nothing has happened in the window being shown.
    Idle,
    /// Read and reporting.
    Active,
}

public static class ProviderConnectionExt
{
    public static string Phrase(this ProviderConnection c) => c switch
    {
        ProviderConnection.NotInstalled => "Not found on this PC",
        ProviderConnection.NotRead => "Not being read",
        ProviderConnection.Unreachable => "Not reachable",
        ProviderConnection.Idle => "No usage in this range",
        _ => "Reading",
    };

    /// Nothing here is a fault except a tool that is being read and will not answer.
    public static ServiceGlyph.Tone Tone(this ProviderConnection c) => c switch
    {
        ProviderConnection.NotInstalled or ProviderConnection.NotRead => ServiceGlyph.Tone.Unknown,
        ProviderConnection.Unreachable => ServiceGlyph.Tone.Warning,
        _ => ServiceGlyph.Tone.Healthy,
    };

    /// Whether the card has figures to draw at all.
    public static bool HasFigures(this ProviderConnection c) => c == ProviderConnection.Active;
}

/// Everything one provider overview card draws.
public sealed record ProviderCard(
    string Provider,
    ProviderConnection Connection,
    // In+out tokens over the window the card names
    int Tokens,
    double Cost,
    // True when a model had no pricing entry, so cost is marked partial
    bool HasUnpriced,
    // The window nearest its cap, which is the one that will stop you first
    LimitWindow? WorstWindow,
    Pace? Pace,
    DateTimeOffset? AsOf,
    bool IsStale,
    ServiceGlyph.Tone? ServiceTone,
    string? ServicePhrase,
    // Daily in+out tokens, oldest first. Empty when there is no series
    IReadOnlyList<int> Trend,
    // Why there is no limit figure, when there is none. Never a fabricated percentage
    string? LimitNote)
{
    public string Id => Provider;

    public ProviderIdentity? Identity => ProviderIdentity.Of(Provider);

    /// The headline percentage, or null when this provider reports no limit at all.
    public double? Utilization => WorstWindow?.Utilization;

    /// Capacity left in the binding window. Null when there is no window.
    public double? RemainingPercent => WorstWindow is { } w ? Math.Max(0, 100 - w.Utilization) : null;

    /// The limit reading when there is one, otherwise the connection state, so a provider
    /// with no limits is never drawn as though it had one.
    public RLStatus Status(double approaching, double atLimit)
    {
        if (WorstWindow is { } w)
            return RLStatus.ForUtilization(w.Utilization, approaching, atLimit, IsStale);
        return RLStatus.ForTone(Connection.Tone(), Connection.Phrase());
    }
}

public enum OverviewWarningKind { Approaching, AtLimit, RunsOutEarly }

public static class OverviewWarningKindExt
{
    internal static int Severity(this OverviewWarningKind k) => k switch
    {
        OverviewWarningKind.AtLimit => 3,
        OverviewWarningKind.RunsOutEarly => 2,
        _ => 1,
    };

    public static RLStatus Status(this OverviewWarningKind k) =>
        k == OverviewWarningKind.AtLimit ? new RLStatus(RLStatusKind.AtLimit) : new RLStatus(RLStatusKind.Approaching);

    /// The Swift case name, which is what the warning's identity is spelled with.
    internal static string CaseName(this OverviewWarningKind k) => k switch
    {
        OverviewWarningKind.AtLimit => "atLimit",
        OverviewWarningKind.RunsOutEarly => "runsOutEarly",
        _ => "approaching",
    };
}

public static class ProviderOverview
{
    /// How old a percentage may be before it is drawn as a last-known reading.
    public const double StalenessThreshold = 900;

    /// Builds one card. Every input is passed in, so this runs against fixtures rather
    /// than against whatever is installed on the machine running the tests.
    public static ProviderCard Card(string provider, bool installed, bool read,
                                    ProviderUsage? usage, bool? reachable = null,
                                    bool hasUnpriced = false,
                                    IEnumerable<LimitWindow>? windows = null,
                                    IEnumerable<Pace>? paces = null,
                                    DateTimeOffset? asOf = null,
                                    ServiceGlyph.Tone? serviceTone = null,
                                    string? servicePhrase = null,
                                    IReadOnlyList<int>? trend = null,
                                    string? limitsNote = null,
                                    DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var tokens = usage?.Io ?? 0;
        var stale = asOf is { } a && (t - a).TotalSeconds > StalenessThreshold;
        var mine = LimitParser.Sorted((windows ?? Enumerable.Empty<LimitWindow>()).Where(w =>
            string.Equals(w.Provider, provider, StringComparison.OrdinalIgnoreCase) && !w.IsUninformative));
        var worst = mine.Count == 0 ? null : mine.MaxBy(w => w.Utilization);
        var pace = worst is null ? null
            : (paces ?? Enumerable.Empty<Pace>()).FirstOrDefault(p => p.Provider == worst.Provider && p.Key == worst.Key);

        ProviderConnection connection;
        if (!installed) connection = ProviderConnection.NotInstalled;
        else if (!read) connection = ProviderConnection.NotRead;
        else if (reachable == false) connection = ProviderConnection.Unreachable;
        else if (tokens == 0 && worst is null) connection = ProviderConnection.Idle;
        else connection = ProviderConnection.Active;

        return new ProviderCard(
            provider, connection, tokens, usage?.Cost ?? 0, hasUnpriced, worst,
            // A rate needs a current number, so a stale reading carries no pace
            stale ? null : pace,
            asOf, stale, serviceTone, servicePhrase, trend ?? Array.Empty<int>(),
            worst is null ? LimitNote(provider, connection, limitsNote) : null);
    }

    /// Why a provider shows no percentage, named rather than left as a blank that reads as
    /// broken tracking.
    internal static string? LimitNote(string provider, ProviderConnection connection, string? note)
    {
        switch (connection)
        {
            case ProviderConnection.NotInstalled:
            case ProviderConnection.NotRead:
                return null; // the connection state already says why the whole card is quiet
            default:
                if (!string.IsNullOrEmpty(note)) return note;
                if (ProviderIdentity.Of(provider)?.IsLocal == true)
                    return "Runs on this PC, so there is no rate limit to report";
                return "No limit window reported yet";
        }
    }

    /// A window at or past the configured threshold, or one that runs out before it resets.
    /// Healthy and stale windows raise nothing: a banner that is always lit says nothing.
    public static List<Warning> Warnings(IEnumerable<LimitWindow> windows, IEnumerable<Pace> paces,
                                         double approaching, double atLimit,
                                         ISet<string>? staleProviders = null)
    {
        var output = new List<Warning>();
        var paceList = paces.ToList();
        foreach (var window in LimitParser.Sorted(windows))
        {
            if (window.IsUninformative) continue;
            if (staleProviders is not null && staleProviders.Contains(window.Provider)) continue;
            var pace = paceList.FirstOrDefault(p => p.Provider == window.Provider && p.Key == window.Key);
            var pct = (int)Math.Round(window.Utilization, MidpointRounding.AwayFromZero);
            if (window.Utilization >= atLimit)
                output.Add(new Warning(window.Provider, window, OverviewWarningKind.AtLimit,
                    $"{window.Provider} {window.DisplayName} is {pct}% used"));
            else if (window.Utilization >= approaching)
                output.Add(new Warning(window.Provider, window, OverviewWarningKind.Approaching,
                    $"{window.Provider} {window.DisplayName} is {pct}% used"));
            else if (pace is not null && pace.HitsLimitBeforeReset && pace.TimeToLimit() is { } toLimit)
                output.Add(new Warning(window.Provider, window, OverviewWarningKind.RunsOutEarly,
                    $"{window.Provider} {window.DisplayName} runs out in about {Pace.Short(toLimit)}, before it resets"));
        }
        // Worst first, so the one that will stop you soonest is read first
        return output.OrderByDescending(w => w.Kind.Severity())
                     .ThenByDescending(w => w.Window.Utilization).ToList();
    }

    public sealed record Warning(string Provider, LimitWindow Window, OverviewWarningKind Kind, string Text)
    {
        public string Id => $"{Window.Id}|{Kind.CaseName()}";
    }
}
