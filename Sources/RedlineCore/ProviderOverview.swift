// What one provider's overview card says, derived once here rather than assembled inside a
// view. Which state wins, and what a card says when a figure is missing, are decisions worth
// testing; a SwiftUI body is not a place they can be.
import Foundation

/// A provider's connection state, worst-news-first. The order of the cases is the order the
/// builder resolves them in, and the reason it never reports "no usage" for a tool it cannot
/// even reach.
public enum ProviderConnection: Equatable, Sendable {
    /// The tool's own directory is not on this Mac.
    case notInstalled
    /// Installed, but switched off in the provider selection, so nothing is being read.
    case notRead
    /// Installed and read, but the thing that serves it is not answering. Local only: a
    /// hosted provider's transcripts are on disk whether the endpoint is up or not.
    case unreachable
    /// Read, reachable, and nothing has happened in the window being shown.
    case idle
    /// Read and reporting.
    case active

    public var phrase: String {
        switch self {
        case .notInstalled: return "Not found on this Mac"
        case .notRead:      return "Not being read"
        case .unreachable:  return "Not reachable"
        case .idle:         return "No usage in this range"
        case .active:       return "Reading"
        }
    }

    /// The status vocabulary this state draws with. Nothing here is a fault except a tool
    /// that is being read and will not answer.
    public var tone: ServiceGlyph.Tone {
        switch self {
        case .notInstalled, .notRead: return .unknown
        case .unreachable:            return .warning
        case .idle, .active:          return .healthy
        }
    }

    /// Whether the card has figures to draw at all.
    public var hasFigures: Bool { self == .active }
}

/// Everything one provider overview card draws.
public struct ProviderCard: Equatable, Identifiable {
    public var id: String { provider }

    public let provider: String
    public let connection: ProviderConnection
    /// In+out tokens over the window the card names.
    public let tokens: Int
    public let cost: Double
    /// True when a model in the window had no pricing entry, so cost is marked partial
    /// rather than reported as if it were complete.
    public let hasUnpriced: Bool
    /// The window nearest its cap, which is the one that will stop you first.
    public let worstWindow: LimitWindow?
    public let pace: Pace?
    /// Set when the provider's percentages are older than the staleness threshold.
    public let asOf: Date?
    public let isStale: Bool
    /// The provider's own status page, where one is read.
    public let serviceTone: ServiceGlyph.Tone?
    public let servicePhrase: String?
    /// Daily in+out tokens, oldest first, for the card's trend. Empty when there is no series.
    public let trend: [Int]
    /// Why there is no limit figure, when there is none. Never a fabricated percentage.
    public let limitNote: String?

    public var identity: ProviderIdentity? { ProviderIdentity.of(provider) }

    /// The card's headline percentage, or nil when this provider reports no limit at all.
    public var utilization: Double? { worstWindow?.utilization }

    /// Capacity left in the binding window. Nil when there is no window to have capacity in.
    public var remainingPercent: Double? {
        worstWindow.map { max(0, 100 - $0.utilization) }
    }

    /// The status the card draws: the limit reading when there is one, otherwise the
    /// connection state, so a provider with no limits is never drawn as though it had one.
    public func status(approaching: Double, atLimit: Double) -> RLStatus {
        if let worstWindow {
            return RLStatus.forUtilization(worstWindow.utilization, approaching: approaching,
                                           atLimit: atLimit, stale: isStale)
        }
        return RLStatus.forTone(connection.tone, phrase: connection.phrase)
    }
}

public enum ProviderOverview {
    /// How old a percentage may be before it is drawn as a last-known reading rather than a
    /// current one. Matches the dashboard rail and the menu bar.
    public static let stalenessThreshold: TimeInterval = 900

    /// Builds one card. Every input is passed in, so this is exercised against fixtures
    /// rather than against whatever happens to be installed on the machine running the tests.
    public static func card(provider: String,
                            installed: Bool,
                            read: Bool,
                            reachable: Bool? = nil,
                            usage: ProviderUsage?,
                            hasUnpriced: Bool = false,
                            windows: [LimitWindow] = [],
                            paces: [Pace] = [],
                            asOf: Date? = nil,
                            serviceTone: ServiceGlyph.Tone? = nil,
                            servicePhrase: String? = nil,
                            trend: [Int] = [],
                            limitsNote: String? = nil,
                            now: Date = Date()) -> ProviderCard {
        let tokens = usage?.io ?? 0
        let stale = asOf.map { now.timeIntervalSince($0) > stalenessThreshold } ?? false
        let mine = LimitParser.sorted(windows.filter {
            $0.provider.caseInsensitiveCompare(provider) == .orderedSame && !$0.isUninformative
        })
        let worst = mine.max(by: { $0.utilization < $1.utilization })
        let pace = worst.flatMap { w in
            paces.first { $0.provider == w.provider && $0.key == w.key }
        }

        let connection: ProviderConnection
        if !installed {
            connection = .notInstalled
        } else if !read {
            connection = .notRead
        } else if reachable == false {
            connection = .unreachable
        } else if tokens == 0 && worst == nil {
            connection = .idle
        } else {
            connection = .active
        }

        return ProviderCard(
            provider: provider,
            connection: connection,
            tokens: tokens,
            cost: usage?.cost ?? 0,
            hasUnpriced: hasUnpriced,
            worstWindow: worst,
            // A rate needs a current number, so a stale reading carries no pace
            pace: stale ? nil : pace,
            asOf: asOf,
            isStale: stale,
            serviceTone: serviceTone,
            servicePhrase: servicePhrase,
            trend: trend,
            limitNote: worst == nil
                ? limitNote(for: provider, connection: connection, note: limitsNote)
                : nil)
    }

    /// Why a provider shows no percentage. Each answer names the reason rather than leaving a
    /// blank that reads as broken tracking.
    static func limitNote(for provider: String, connection: ProviderConnection,
                          note: String?) -> String? {
        switch connection {
        case .notInstalled, .notRead:
            return nil   // the connection state already says why the whole card is quiet
        case .unreachable, .idle, .active:
            if let note, !note.isEmpty { return note }
            if ProviderIdentity.of(provider)?.isLocal == true {
                return "Runs on this Mac, so there is no rate limit to report"
            }
            return "No limit window reported yet"
        }
    }

    /// The warnings worth putting at the top of the overview: a window at or past the
    /// configured threshold, and a window that runs out before it resets.
    ///
    /// Deliberately not "everything that could be said". A banner that is always lit says
    /// nothing, so a healthy window and a stale reading both produce no warning: a stale
    /// number is not evidence about now, and the surfaces that draw it already say so.
    public static func warnings(windows: [LimitWindow], paces: [Pace],
                                approaching: Double, atLimit: Double,
                                staleProviders: Set<String> = []) -> [Warning] {
        var out: [Warning] = []
        for window in LimitParser.sorted(windows) where !window.isUninformative {
            guard !staleProviders.contains(window.provider) else { continue }
            let pace = paces.first { $0.provider == window.provider && $0.key == window.key }
            if window.utilization >= atLimit {
                out.append(Warning(provider: window.provider, window: window,
                                   kind: .atLimit,
                                   text: "\(window.provider) \(window.displayName) is "
                                       + "\(Int(window.utilization.rounded()))% used"))
            } else if window.utilization >= approaching {
                out.append(Warning(provider: window.provider, window: window,
                                   kind: .approaching,
                                   text: "\(window.provider) \(window.displayName) is "
                                       + "\(Int(window.utilization.rounded()))% used"))
            } else if let pace, pace.hitsLimitBeforeReset, let toLimit = pace.timeToLimit() {
                out.append(Warning(provider: window.provider, window: window,
                                   kind: .runsOutEarly,
                                   text: "\(window.provider) \(window.displayName) runs out "
                                       + "in about \(Pace.short(toLimit)), before it resets"))
            }
        }
        // Worst first, so the one that will stop you soonest is read first
        return out.sorted { a, b in
            a.kind == b.kind ? a.window.utilization > b.window.utilization
                             : a.kind.severity > b.kind.severity
        }
    }

    public struct Warning: Equatable, Identifiable {
        public enum Kind: Equatable, Sendable {
            case approaching, atLimit, runsOutEarly

            var severity: Int {
                switch self {
                case .atLimit:      return 3
                case .runsOutEarly: return 2
                case .approaching:  return 1
                }
            }

            public var status: RLStatus {
                switch self {
                case .atLimit:                   return RLStatus(.atLimit)
                case .approaching, .runsOutEarly: return RLStatus(.approaching)
                }
            }
        }

        public var id: String { "\(window.id)|\(kind)" }
        public let provider: String
        public let window: LimitWindow
        public let kind: Kind
        public let text: String
    }
}
