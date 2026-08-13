// Pure JSON scanning for a borrowed CLI access token. Kept separate from Keychain access
// so the undocumented credential shape can be unit tested without touching real secrets.
import Foundation

public enum CredentialScan {
    // Scans for an accessToken/expiresAt pair instead of hardcoding a key path that a CLI
    // update could move. Returns nil when the token is absent, empty, or already expired.
    public static func accessToken(in json: [String: Any],
                                   now: Date = Date(),
                                   margin: TimeInterval = 60) -> String? {
        find(in: json, depth: 0, now: now, margin: margin)
    }

    private static func find(in dict: [String: Any], depth: Int,
                             now: Date, margin: TimeInterval) -> String? {
        if let token = value(dict, "accesstoken") as? String, !token.isEmpty,
           fresh(dict, now: now, margin: margin) {
            return token
        }
        guard depth < 3 else { return nil }
        // Sort keys so a nested match is deterministic rather than dictionary-order dependent
        for key in dict.keys.sorted() {
            if let d = dict[key] as? [String: Any],
               let t = find(in: d, depth: depth + 1, now: now, margin: margin) {
                return t
            }
        }
        return nil
    }

    private static func value(_ dict: [String: Any], _ name: String) -> Any? {
        dict.first(where: { $0.key.lowercased() == name })?.value
    }

    // expiresAt is epoch milliseconds; a missing value is treated as non-expiring
    private static func fresh(_ dict: [String: Any], now: Date, margin: TimeInterval) -> Bool {
        guard let raw = value(dict, "expiresat"),
              let ms = LimitParser.number(raw), ms > 0 else { return true }
        return Date(timeIntervalSince1970: ms / 1000) > now.addingTimeInterval(margin)
    }
}
