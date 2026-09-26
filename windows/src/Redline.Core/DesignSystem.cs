// The data half of the design system: provider identity and its accent tokens. Brushes and
// styles built from these live in the app.
namespace Redline.Core;

/// Everything RedLine knows about how to present one provider: which mark identifies it,
/// which accent surrounds it, and what it is called.
public sealed record ProviderIdentity(string Name, ProviderMark Mark, string Blurb, bool IsLocal)
{
    /// Kept clear of the signal red so a provider colour can never be read as a warning.
    public static readonly (BrandColor Dark, BrandColor Light) CodexAccent = (new(0x45C4D4), new(0x0C6D7C));
    public static readonly (BrandColor Dark, BrandColor Light) AnthropicAccent = (new(0xD9A05B), new(0x8A6118));
    public static readonly (BrandColor Dark, BrandColor Light) OllamaAccent = (new(0x9888D4), new(0x5F44A6));
    /// Every provider at once, or none named: the product's own neutral.
    public static readonly (BrandColor Dark, BrandColor Light) NeutralAccent = (new(0xA8AEBA), new(0x4E5462));

    public (BrandColor Dark, BrandColor Light) Accent => Mark switch
    {
        ProviderMark.Codex => CodexAccent,
        ProviderMark.Anthropic or ProviderMark.Claude => AnthropicAccent,
        _ => OllamaAccent,
    };

    /// Nil when no provider is named. "Claude" maps to Anthropic's mark on purpose: the
    /// Claude sparkle is reserved for naming the product itself.
    public static ProviderIdentity? Of(string? provider)
    {
        if (provider is null) return null;
        switch (provider.ToLowerInvariant())
        {
            case "claude":
            case "anthropic":
                return new(provider.ToLowerInvariant() == "anthropic" ? "Anthropic" : "Claude",
                    ProviderMark.Anthropic,
                    "Tokens and cost from transcripts on disk, plus rate-limit windows", false);
            case "codex":
            case "openai":
                return new("Codex", ProviderMark.Codex, "Limits and tokens, read entirely from disk", false);
            case "ollama":
                return new("Ollama", ProviderMark.Ollama, "Local models, counted once tracking is set up", true);
            default:
                return null;
        }
    }

    /// The accent for a provider name, falling back to the product's neutral.
    public static (BrandColor Dark, BrandColor Light) AccentFor(string? provider) =>
        Of(provider)?.Accent ?? NeutralAccent;
}
