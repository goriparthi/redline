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

/// What a failed refresh of RedLine's own grant means.
///
/// The distinction matters because the two dispositions are opposites: a retryable failure must
/// keep the stored token and try again, and a terminal one must clear it so the app offers Sign
/// In instead of retrying a grant that will never come back.
public enum GrantRefreshOutcome: Equatable {
    /// The grant is dead. Clear it, and say so once.
    case terminal(String)
    /// Try again on the next poll.
    case retryable(String)

    public var isTerminal: Bool {
        if case .terminal = self { return true }
        return false
    }

    public var message: String {
        switch self {
        case .terminal(let m), .retryable(let m): return m
        }
    }
}

public enum ClaudeAuthPolicy {
    /// Exponential backoff capped at 30 minutes, matching the usage endpoint's own behaviour.
    /// The grant-refresh disposition logic that used to live beside this left with mint():
    /// RedLine no longer spends the CLI's refresh token, so there is nothing to classify.
    public static func backoff(consecutiveFailures n: Int) -> TimeInterval {
        guard n > 0 else { return 0 }
        return min(300 * pow(2, Double(n - 1)), 1800)
    }

    /// Whether a refresh failure is worth repeating.
    ///
    /// The rule is deliberately the other way round from the original one, which treated every
    /// failure as retryable unless the body happened to contain the literal `invalid_grant`.
    /// Anthropic's token endpoint answers in its API envelope rather than in OAuth's, so a
    /// rejected refresh arrives as `invalid_request_error` and was retried on every poll
    /// forever: the app stayed "signed in", the percentages never came back, and nothing
    /// offered Sign In.
    ///
    /// Only three things can make an identical request succeed later: a lifted rate limit, a
    /// recovered server, or a working network. Everything else in the 4xx family is a rejection
    /// of the request or the grant, and repeating it verbatim cannot change the answer.
    public static func classifyRefresh(status: Int, body: String) -> GrantRefreshOutcome {
        let lower = body.lowercased()
        // Transport failure: no status, so nothing was rejected
        if status == 0 {
            return .retryable("Could not reach the sign-in service; retrying")
        }
        if status == 429 {
            return .retryable("Rate limited while renewing the sign-in; retrying")
        }
        if status >= 500 {
            return .retryable("The sign-in service is failing; retrying")
        }
        if (200..<300).contains(status) {
            // A 2xx with no access token is a shape RedLine does not understand. Retrying is
            // pointless, but the grant may well be fine, so this does not clear it.
            return .retryable("The sign-in service returned no token; retrying")
        }
        // Named OAuth causes first, so the message can say which one it was
        if lower.contains("invalid_grant") {
            return .terminal("Sign-in expired; sign in again")
        }
        if lower.contains("invalid_client") || lower.contains("unauthorized_client") {
            return .terminal("This build's OAuth client id was rejected; check oauth.clientId")
        }
        return .terminal("Sign-in could not be renewed (HTTP \(status)); sign in again")
    }

}

/// `application/x-www-form-urlencoded` bodies, which is what RFC 6749 specifies for a token
/// request. Here rather than in the app so the encoding is tested: the default URL character
/// sets leave `+` and `&` alone, and a token containing either would be corrupted in transit
/// with nothing to show for it but a rejected request.
public enum FormBody {
    public static func encoded(_ body: [String: String]) -> String {
        // RFC 3986 unreserved set only. Anything else is percent-encoded, including the
        // characters the URL sets consider legal in a query.
        let unreserved = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        // Sorted so the same body always produces the same bytes, which makes a failing
        // request reproducible.
        return body.keys.sorted().map { key in
            let k = key.addingPercentEncoding(withAllowedCharacters: unreserved) ?? key
            let raw = body[key] ?? ""
            let v = raw.addingPercentEncoding(withAllowedCharacters: unreserved) ?? raw
            return "\(k)=\(v)"
        }.joined(separator: "&")
    }
}
