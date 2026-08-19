// Where a number came from. Every figure RedLine reports can name its own origin, so a
// reading that disagrees with another tool can be argued about rather than guessed at.
import Foundation

public enum Provenance: String, Codable, CaseIterable, Sendable {
    /// Reported by the tool or provider itself: Claude Code's own statusline figures,
    /// Codex's rate-limit block on disk, token counts the provider counted.
    case official
    /// Computed here from local data and a pricing table. Correct arithmetic over an
    /// assumption, which is not the same thing as a bill.
    case localEstimate = "local_estimate"
    /// Read from a source that nobody documents and that can change without notice.
    case experimental
    /// Provenance was not recorded. Older files decode to this rather than claiming more.
    case unknown

    public var label: String {
        switch self {
        case .official:      return "official"
        case .localEstimate: return "local estimate"
        case .experimental:  return "experimental"
        case .unknown:       return "unknown"
        }
    }

    /// One line a person can act on, for tooltips and the `--json` `note` field.
    public var note: String {
        switch self {
        case .official:      return "reported by the provider"
        case .localEstimate: return "computed here from local files and your pricing table"
        case .experimental:  return "read from an undocumented endpoint"
        case .unknown:       return "source not recorded"
        }
    }

    public init(rawValueOrUnknown raw: String?) {
        self = raw.flatMap(Provenance.init(rawValue:)) ?? .unknown
    }
}
