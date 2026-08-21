// What RedLine knows about a provider as data: its mark, its name, what it reads.
// The accent colours live in RedlineUI, because a colour is a shell's business.
import Foundation

/// Everything RedLine knows about how to present one provider, in one place: which mark
/// identifies it, and what it is called.
public struct ProviderIdentity: Equatable, Sendable {
    public let name: String
    public let mark: ProviderMark
    /// What the provider reads on this machine, in one line, for a card subtitle or tooltip.
    public let blurb: String
    /// Whether usage runs on this machine or against a hosted endpoint.
    public let isLocal: Bool

    /// Provider identity for a provider name, or nil when no provider is named. The mapping
    /// is deliberate: "Claude" is a provider track in RedLine's data, and the provider-level
    /// mark is Anthropic's, so the Claude sparkle is reserved for naming the product itself.
    public static func of(_ provider: String?) -> ProviderIdentity? {
        guard let provider else { return nil }
        switch provider.lowercased() {
        case "claude", "anthropic":
            return ProviderIdentity(
                name: provider.lowercased() == "anthropic" ? "Anthropic" : "Claude",
                mark: .anthropic,
                blurb: "Tokens and cost from transcripts on disk, plus rate-limit windows",
                isLocal: false)
        case "codex", "openai":
            return ProviderIdentity(name: "Codex", mark: .codex,
                                    blurb: "Limits and tokens, read entirely from disk",
                                    isLocal: false)
        case "ollama":
            return ProviderIdentity(name: "Ollama", mark: .ollama,
                                    blurb: "Local models, counted once tracking is set up",
                                    isLocal: true)
        default:
            return nil
        }
    }
}
