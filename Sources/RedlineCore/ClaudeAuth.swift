// Decisions about borrowed Claude credentials that do not need the Keychain or the network,
// so they can be tested directly. The app layer performs the reads; this says what they mean.
import Foundation

/// Why a credential read produced nothing. The original bug was treating all three the same:
/// a locked Keychain latched exactly like a signed-out CLI and never retried.
public enum CredentialOutcome: Equatable {
    case found(BorrowedCredential)
    /// The item is genuinely absent. Claude Code is signed out; only the user can fix it.
    case notFound
    /// The item exists but macOS would not hand it over: consent needed, keychain locked, or
    /// the prompt was dismissed. Transient by nature, so it must be retried, not latched.
    case accessDenied
    /// Read succeeded but the payload was not a credential we understand.
    case unreadable

    public var credential: BorrowedCredential? {
        if case let .found(c) = self { return c }
        return nil
    }

    /// Only a signed-out CLI is worth telling the user about as a durable state. The rest
    /// resolve themselves or resolve on the next pass.
    public var isTerminal: Bool { self == .notFound }
}

public enum ClaudeAuthPolicy {
    /// Exponential backoff capped at 30 minutes, matching the usage endpoint's own behaviour.
    /// The grant-refresh disposition logic that used to live beside this left with mint():
    /// RedLine no longer spends the CLI's refresh token, so there is nothing to classify.
    public static func backoff(consecutiveFailures n: Int) -> TimeInterval {
        guard n > 0 else { return 0 }
        return min(300 * pow(2, Double(n - 1)), 1800)
    }
}
