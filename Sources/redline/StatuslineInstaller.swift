// Installs the Claude usage feed: RedLine's statusline wrapper plus the ~/.claude/settings.json
// entry that points at it. This writes into a file Claude Code owns, so it never discards what
// is already there; an existing statusline command is carried forward and still draws the line.
import Foundation
import RedlineCore

enum StatuslineInstaller {
    static let marker = "RedLine Claude usage feed"

    enum Result {
        case installed(script: URL, chained: String?)
        case alreadyInstalled(script: URL)
        case failed(String)
    }

    static func settingsURL(home: URL? = nil) -> URL {
        (home ?? FileManager.default.homeDirectoryForCurrentUser)
            .appendingPathComponent(".claude/settings.json")
    }

    static func scriptURL(home: URL? = nil) -> URL {
        (home ?? FileManager.default.homeDirectoryForCurrentUser)
            .appendingPathComponent(".local/share/redline/claude-statusline.sh")
    }

    /// True when settings.json already points at our wrapper, whatever else has changed
    /// around it.
    static func isInstalled(home: URL? = nil) -> Bool {
        guard let settings = readSettings(home: home),
              let line = settings["statusLine"] as? [String: Any],
              let command = line["command"] as? String else { return false }
        return command.contains(scriptURL(home: home).lastPathComponent)
    }

    static func install(home: URL? = nil) -> Result {
        let fm = FileManager.default
        guard let src = Bundle.main.url(forResource: "claude-statusline", withExtension: "sh")
            ?? repoFallback() else {
            return .failed("claude-statusline.sh is not in this build. Install it from "
                + "scripts/claude-statusline.sh in a clone of the repository instead.")
        }

        let dest = scriptURL(home: home)
        do {
            try fm.createDirectory(at: dest.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)
            // Replace our own file freely; this doubles as the update path
            if fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
            try fm.copyItem(at: src, to: dest)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dest.path)
        } catch {
            return .failed("Could not write \(dest.path)\n\n\(error.localizedDescription)")
        }

        var settings = readSettings(home: home) ?? [:]
        let existing = (settings["statusLine"] as? [String: Any])?["command"] as? String
        // Re-running must not chain the wrapper to itself, which would recurse on every draw
        let chained = (existing?.contains(dest.lastPathComponent) ?? true) ? nil : existing

        if isInstalled(home: home), chained == nil {
            return .alreadyInstalled(script: dest)
        }

        var command = shellQuote(dest.path)
        if let chained {
            // Passed by environment rather than argument so a command containing quotes or
            // pipes survives intact
            command = "REDLINE_STATUSLINE_CHAIN=\(shellQuote(chained)) \(command)"
        }
        var line = (settings["statusLine"] as? [String: Any]) ?? [:]
        line["type"] = "command"
        line["command"] = command
        settings["statusLine"] = line

        guard writeSettings(settings, home: home) else {
            return .failed("Could not write \(settingsURL(home: home).path)")
        }
        return .installed(script: dest, chained: chained)
    }

    // MARK: -

    private static func readSettings(home: URL? = nil) -> [String: Any]? {
        guard let data = try? Data(contentsOf: settingsURL(home: home)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return json
    }

    /// Writes through a temporary file: a half-written settings.json would break every Claude
    /// Code session on this Mac, which is a far worse failure than no usage feed.
    private static func writeSettings(_ settings: [String: Any], home: URL? = nil) -> Bool {
        let url = settingsURL(home: home)
        guard let data = try? JSONSerialization.data(
            withJSONObject: settings,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return false }
        let tmp = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).redline.tmp")
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try data.write(to: tmp, options: .atomic)
            _ = try FileManager.default.replaceItemAt(url, withItemAt: tmp)
            return true
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            return false
        }
    }

    /// A dev build run from the repo has no bundle resources, so fall back to the clone
    private static func repoFallback() -> URL? {
        let candidate = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            .appendingPathComponent("scripts/claude-statusline.sh")
        return FileManager.default.fileExists(atPath: candidate.path) ? candidate : nil
    }

    static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
