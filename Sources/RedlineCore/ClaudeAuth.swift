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

public enum RefreshDisposition: Equatable {
    /// The grant is dead. Retrying with the same refresh token cannot succeed.
    case terminal
    /// Server-side or rate-limited. Back off and try again.
    case transient
}

public enum ClaudeAuthPolicy {
    /// Exponential backoff capped at 30 minutes, matching the usage endpoint's own behaviour.
    public static func backoff(consecutiveFailures n: Int) -> TimeInterval {
        guard n > 0 else { return 0 }
        return min(300 * pow(2, Double(n - 1)), 1800)
    }

    /// A refused refresh is terminal only when the server says the grant itself is bad.
    /// Everything else is worth another attempt later.
    public static func disposition(status: Int, body: Data? = nil) -> RefreshDisposition {
        if status == 429 || (500..<600).contains(status) { return .transient }
        guard (400..<500).contains(status) else { return .transient }
        let text = body.flatMap { String(data: $0, encoding: .utf8) }?.lowercased() ?? ""
        if text.contains("invalid_grant") || text.contains("invalid_client") { return .terminal }
        // 401/403 without an explicit grant error is usually a stale token rather than a dead
        // one, so it stays retryable; a hard 400 is not.
        return status == 400 ? .terminal : .transient
    }

    /// Retry-After is either delta seconds or an HTTP date. Honouring it beats a blind
    /// exponential, which either hammers too early or sulks far too long.
    public static func retryAfter(_ header: String?, now: Date = Date()) -> Date? {
        guard let header = header?.trimmingCharacters(in: .whitespaces), !header.isEmpty
        else { return nil }
        if let seconds = Double(header) {
            return seconds >= 0 ? now.addingTimeInterval(seconds) : nil
        }
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(secondsFromGMT: 0)
        for pattern in ["EEE, dd MMM yyyy HH:mm:ss zzz",
                        "EEEE, dd-MMM-yy HH:mm:ss zzz",
                        "EEE MMM d HH:mm:ss yyyy"] {
            fmt.dateFormat = pattern
            if let d = fmt.date(from: header) { return d }
        }
        return nil
    }
}
