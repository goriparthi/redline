// The status vocabulary shared by every card, chip and rail. Only the data lives in Core;
// colours and glyph drawing belong to the app.
namespace Redline.Core;

public enum RLStatusKind { Healthy, Approaching, AtLimit, Offline, Unknown, Stale }

/// One presentation of state, carrying a shape and a word together, so status is never
/// signalled by colour alone.
public sealed record RLStatus
{
    public RLStatusKind Kind { get; }
    /// Overrides the default wording when a caller knows something more specific.
    public string Phrase { get; }

    public RLStatus(RLStatusKind kind, string? phrase = null)
    {
        Kind = kind;
        Phrase = phrase ?? DefaultPhrase(kind);
    }

    internal static string DefaultPhrase(RLStatusKind kind) => kind switch
    {
        RLStatusKind.Healthy => "Healthy",
        RLStatusKind.Approaching => "Approaching your limit",
        RLStatusKind.AtLimit => "Limit reached",
        RLStatusKind.Offline => "Not reachable",
        RLStatusKind.Unknown => "Not checked",
        _ => "Last known reading",
    };

    /// SF Symbol name on macOS; the app maps it to its own glyph. Distinct per state.
    public string Symbol => Kind switch
    {
        RLStatusKind.Healthy => "checkmark.circle.fill",
        RLStatusKind.Approaching => "exclamationmark.triangle.fill",
        RLStatusKind.AtLimit => "exclamationmark.octagon.fill",
        RLStatusKind.Offline => "bolt.slash.circle.fill",
        RLStatusKind.Unknown => "questionmark.circle.fill",
        _ => "clock.badge.exclamationmark.fill",
    };

    public static RLStatus ForUtilization(double utilization, double approaching = 60,
                                          double atLimit = 85, bool stale = false)
    {
        if (stale) return new RLStatus(RLStatusKind.Stale);
        return Brand.Status(utilization, approaching, atLimit) switch
        {
            BrandStatus.Healthy => new RLStatus(RLStatusKind.Healthy),
            BrandStatus.Approaching => new RLStatus(RLStatusKind.Approaching),
            _ => new RLStatus(RLStatusKind.AtLimit),
        };
    }

    /// From the shared service-health vocabulary, so a status page and a limit window are
    /// drawn by the same component.
    public static RLStatus ForTone(ServiceGlyph.Tone tone, string? phrase = null) => tone switch
    {
        ServiceGlyph.Tone.Healthy => new RLStatus(RLStatusKind.Healthy, phrase),
        ServiceGlyph.Tone.Warning => new RLStatus(RLStatusKind.Approaching, phrase),
        ServiceGlyph.Tone.Critical => new RLStatus(RLStatusKind.AtLimit, phrase),
        _ => new RLStatus(RLStatusKind.Unknown, phrase),
    };
}
