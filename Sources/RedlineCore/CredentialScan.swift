// Pure JSON scanning for a borrowed CLI credential. Kept separate from Keychain access so the
// undocumented credential shape can be unit tested without touching real secrets.
import Foundation

/// What Claude Code stores: the access token, the refresh token that outlives it, and when the
/// access token dies. Expiry is carried rather than applied here, because a stale token still
/// tells the caller "signed in, needs refresh", which is not the same as "signed out".
public struct BorrowedCredential: Equatable {
    public let accessToken: String
    public let refreshToken: String?
    public let expiresAt: Date?

    public init(accessToken: String, refreshToken: String?, expiresAt: Date?) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
    }

    /// A missing expiry is treated as non-expiring; the margin covers clock skew and the
    /// round trip that follows.
    public func isFresh(now: Date = Date(), margin: TimeInterval = 60) -> Bool {
        guard let expiresAt else { return true }
        return expiresAt > now.addingTimeInterval(margin)
    }

    public var canRefresh: Bool {
        !(refreshToken?.trimmingCharacters(in: .whitespaces).isEmpty ?? true)
    }
}

public enum CredentialScan {
    // Scans for an accessToken/expiresAt pair instead of hardcoding a key path that a CLI
    // update could move. Returns nil when the token is absent, empty, or already expired.
    public static func accessToken(in json: [String: Any],
                                   now: Date = Date(),
                                   margin: TimeInterval = 60) -> String? {
        guard let c = credential(in: json), c.isFresh(now: now, margin: margin) else { return nil }
        return c.accessToken
    }

    /// The whole credential, expired or not. The refresh path needs the stale one.
    public static func credential(in json: [String: Any]) -> BorrowedCredential? {
        find(in: json, depth: 0)
    }

    private static func find(in dict: [String: Any], depth: Int) -> BorrowedCredential? {
        if let token = value(dict, "accesstoken") as? String, !token.isEmpty {
            return BorrowedCredential(accessToken: token,
                                      refreshToken: value(dict, "refreshtoken") as? String,
                                      expiresAt: expiry(dict))
        }
        guard depth < 3 else { return nil }
        // Sort keys so a nested match is deterministic rather than dictionary-order dependent
        for key in dict.keys.sorted() {
            if let d = dict[key] as? [String: Any],
               let c = find(in: d, depth: depth + 1) {
                return c
            }
        }
        return nil
    }

    private static func value(_ dict: [String: Any], _ name: String) -> Any? {
        dict.first(where: { $0.key.lowercased() == name })?.value
    }

    // expiresAt is epoch milliseconds
    private static func expiry(_ dict: [String: Any]) -> Date? {
        guard let raw = value(dict, "expiresat"),
              let ms = LimitParser.number(raw), ms > 0 else { return nil }
        return Date(timeIntervalSince1970: ms / 1000)
    }
}

/// `security find-generic-password -w` hex-encodes any payload it cannot hand back as a clean
/// C-string, and Claude Code's blob line-wraps, which triggers exactly that. Real credential
/// JSON always contains '{' and '"', so an all-hex even-length payload is unambiguously a dump.
public enum SecurityCLIOutput {
    public static func decode(_ raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2, trimmed.count % 2 == 0,
              trimmed.allSatisfy({ $0.isHexDigit }) else { return raw }
        var bytes = [UInt8]()
        bytes.reserveCapacity(trimmed.count / 2)
        var index = trimmed.startIndex
        while index < trimmed.endIndex {
            let next = trimmed.index(index, offsetBy: 2)
            guard let b = UInt8(trimmed[index..<next], radix: 16) else { return raw }
            bytes.append(b)
            index = next
        }
        return String(decoding: bytes, as: UTF8.self)
    }
}
