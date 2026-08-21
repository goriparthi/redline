// The status vocabulary as data. A status is a kind plus its wording; how it is drawn is
// the shell's business, so no colour or symbol name appears here.
import Foundation

// MARK: - Status vocabulary

/// One state of a thing RedLine watches, as a kind plus the words for it. Every shell pairs
/// the kind with its own colour and shape, so status is never signalled by colour alone.
public struct RLStatus: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case healthy, approaching, atLimit, offline, unknown, stale
    }

    public let kind: Kind
    /// Overrides the default wording when a caller knows something more specific.
    public let phrase: String

    public init(_ kind: Kind, phrase: String? = nil) {
        self.kind = kind
        self.phrase = phrase ?? RLStatus.defaultPhrase(kind)
    }

    static func defaultPhrase(_ kind: Kind) -> String {
        switch kind {
        case .healthy:     return "Healthy"
        case .approaching: return "Approaching your limit"
        case .atLimit:     return "Limit reached"
        case .offline:     return "Not reachable"
        case .unknown:     return "Not checked"
        case .stale:       return "Last known reading"
        }
    }

    /// From a utilization percentage and the configured thresholds.
    public static func forUtilization(_ utilization: Double, approaching: Double = 60,
                                      atLimit: Double = 85, stale: Bool = false) -> RLStatus {
        if stale { return RLStatus(.stale) }
        switch Brand.status(for: utilization, approachingPct: approaching,
                            atLimitPct: atLimit) {
        case .healthy:     return RLStatus(.healthy)
        case .approaching: return RLStatus(.approaching)
        case .atLimit:     return RLStatus(.atLimit)
        }
    }

    /// From the shared service-health vocabulary, so a status page and a limit window are
    /// drawn by the same component.
    public static func forTone(_ tone: ServiceGlyph.Tone, phrase: String? = nil) -> RLStatus {
        switch tone {
        case .healthy:  return RLStatus(.healthy, phrase: phrase)
        case .warning:  return RLStatus(.approaching, phrase: phrase)
        case .critical: return RLStatus(.atLimit, phrase: phrase)
        case .unknown:  return RLStatus(.unknown, phrase: phrase)
        }
    }
}
