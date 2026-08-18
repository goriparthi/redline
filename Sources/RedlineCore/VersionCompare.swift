// Semver-style ordering for the updater. A prerelease sorts below its release
// (0.4.0-beta.1 < 0.4.0) and numerically among its own kind (beta.2 < beta.10).
import Foundation

public enum VersionCompare {
    public static func isNewer(_ candidate: String, than current: String) -> Bool {
        compare(candidate, current) == .orderedDescending
    }

    private static func compare(_ lhs: String, _ rhs: String) -> ComparisonResult {
        let (lCore, lPre) = split(lhs)
        let (rCore, rPre) = split(rhs)
        for i in 0..<max(lCore.count, rCore.count) {
            let x = i < lCore.count ? lCore[i] : 0
            let y = i < rCore.count ? rCore[i] : 0
            if x != y { return x > y ? .orderedDescending : .orderedAscending }
        }
        switch (lPre.isEmpty, rPre.isEmpty) {
        case (true, true): return .orderedSame
        case (true, false): return .orderedDescending
        case (false, true): return .orderedAscending
        case (false, false): return comparePrerelease(lPre, rPre)
        }
    }

    private static func split(_ version: String) -> (core: [Int], pre: [Substring]) {
        let trimmed = version.hasPrefix("v") ? String(version.dropFirst()) : version
        let parts = trimmed.split(separator: "-", maxSplits: 1)
        let core = (parts.first ?? "").split(separator: ".").map { Int($0) ?? 0 }
        let pre = parts.count > 1 ? parts[1].split(separator: ".") : []
        return (core, pre)
    }

    private static func comparePrerelease(_ a: [Substring],
                                          _ b: [Substring]) -> ComparisonResult {
        for i in 0..<max(a.count, b.count) {
            // Semver: with all shared identifiers equal, the shorter list orders first
            guard i < a.count else { return .orderedAscending }
            guard i < b.count else { return .orderedDescending }
            let x = a[i], y = b[i]
            switch (Int(x), Int(y)) {
            case let (nx?, ny?):
                if nx != ny { return nx > ny ? .orderedDescending : .orderedAscending }
            case (_?, nil): return .orderedAscending
            case (nil, _?): return .orderedDescending
            case (nil, nil):
                if x != y { return x > y ? .orderedDescending : .orderedAscending }
            }
        }
        return .orderedSame
    }
}
