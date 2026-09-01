// Where everything RedLine reads and writes hangs off.
//
// One helper rather than thirty calls to homeDirectoryForCurrentUser, for two reasons. The
// end to end tests need to drive the real binary against a directory that is not the real
// home, and macOS resolves the home directory from the account rather than from $HOME, so
// exporting HOME does not do it. And a second profile on one machine becomes possible
// without a second account.
//
// REDLINE_HOME is honoured only when it names an absolute path that already exists, because
// a typo that silently creates a fresh empty history somewhere is worse than being ignored.
import Foundation

public enum RedlineHome {
    public static let variable = "REDLINE_HOME"

    public static var url: URL {
        if let raw = ProcessInfo.processInfo.environment[variable],
           raw.hasPrefix("/"),
           FileManager.default.fileExists(atPath: raw) {
            return URL(fileURLWithPath: raw, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser
    }

    /// True when the paths in use are not the account's own. Surfaced so a diagnostic can
    /// say so rather than leaving someone puzzled about missing history.
    public static var isOverridden: Bool {
        url.standardizedFileURL != FileManager.default.homeDirectoryForCurrentUser
            .standardizedFileURL
    }

    public static func path(_ components: String) -> URL {
        url.appendingPathComponent(components)
    }
}
