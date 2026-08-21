// Installing the Claude usage feed: RedLine's statusline wrapper, plus the entry in
// ~/.claude/settings.json that points at it.
//
// This writes into a file Claude Code owns, so it never discards what is already there: an
// existing statusline command is carried forward and still draws the line.
//
// Lives in the core rather than in the macOS app because without it a Windows machine has no
// live Claude limits at all, and there is no menu there to offer the same thing.
import Foundation

public enum StatuslineSetup {
    public enum Outcome: Equatable {
        case installed(script: URL, chained: String?)
        case alreadyInstalled(script: URL)
        case removed
        case notInstalled
        case failed(String)
    }

    public static func settingsURL(home: URL? = nil) -> URL {
        (home ?? RedlineHome.url).appendingPathComponent(".claude/settings.json")
    }

    /// The feeder for this platform. Two languages, one contract; see scripts/.
    public static var scriptName: String {
        #if os(Windows)
        return "claude-statusline.ps1"
        #else
        return "claude-statusline.sh"
        #endif
    }

    static var scriptBody: String {
        #if os(Windows)
        return StatuslineScripts.windows
        #else
        return StatuslineScripts.posix
        #endif
    }

    public static func scriptURL(home: URL? = nil) -> URL {
        AppPaths.data(scriptName, in: home)
    }

    /// The feed as a standing choice: the script exists only while it is wanted, so a
    /// settings.json that another tool rewrote cannot quietly unset it.
    public static func isWanted(home: URL? = nil) -> Bool {
        FileManager.default.fileExists(atPath: scriptURL(home: home).path)
    }

    /// True when settings.json already points at our wrapper, whatever else changed around it.
    public static func isInstalled(home: URL? = nil) -> Bool {
        guard let command = currentCommand(home: home) else { return false }
        return command.contains(scriptName)
    }

    /// How Claude Code should invoke the feeder. PowerShell needs an interpreter and a -File;
    /// a shell script is executable on its own.
    static func invocation(for script: URL) -> String {
        #if os(Windows)
        return "powershell -NoProfile -ExecutionPolicy Bypass -File \(quote(script.path))"
        #else
        return quote(script.path)
        #endif
    }

    public static func install(home: URL? = nil) -> Outcome {
        let fm = FileManager.default
        let dest = scriptURL(home: home)
        do {
            try fm.createDirectory(at: dest.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)
            // Rewritten every time, which doubles as the update path
            try Data(scriptBody.utf8).write(to: dest, options: .atomic)
            #if !os(Windows)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dest.path)
            #endif
        } catch {
            return .failed("Could not write \(dest.path): \(error.localizedDescription)")
        }

        var settings = readSettings(home: home) ?? [:]
        let existing = currentCommand(home: home)
        // Re-running must not chain the wrapper to itself, which would recurse on every draw
        let chained = (existing?.contains(scriptName) ?? true) ? nil : existing

        if isInstalled(home: home), chained == nil {
            return .alreadyInstalled(script: dest)
        }

        var command = invocation(for: dest)
        if let chained {
            // Passed by environment rather than as an argument, so a command containing
            // quotes or pipes survives intact
            command = environmentPrefix(chained) + command
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

    public static func uninstall(home: URL? = nil) -> Outcome {
        try? FileManager.default.removeItem(at: scriptURL(home: home))
        guard var settings = readSettings(home: home),
              let command = currentCommand(home: home),
              command.contains(scriptName) else { return .notInstalled }

        // Whatever was chained behind us goes back to being the statusline
        if let restored = chainedCommand(in: command) {
            settings["statusLine"] = ["type": "command", "command": restored]
        } else {
            settings.removeValue(forKey: "statusLine")
        }
        return writeSettings(settings, home: home) ? .removed
            : .failed("Could not write \(settingsURL(home: home).path)")
    }

    /// The command we wrapped, recovered from our own environment prefix.
    public static func chainedCommand(in command: String) -> String? {
        let key = "REDLINE_STATUSLINE_CHAIN="
        guard let start = command.range(of: key) else { return nil }
        let rest = command[start.upperBound...]
        guard rest.first == "'" || rest.first == "\"" else { return nil }
        let quoteMark = rest.first!
        guard let end = rest.dropFirst().firstIndex(of: quoteMark) else { return nil }
        return String(rest[rest.index(after: rest.startIndex)..<end])
            .replacingOccurrences(of: "'\\''", with: "'")
    }

    private static func environmentPrefix(_ chained: String) -> String {
        #if os(Windows)
        // cmd sets a variable before the command rather than prefixing it
        return "set \"REDLINE_STATUSLINE_CHAIN=\(chained)\" && "
        #else
        return "REDLINE_STATUSLINE_CHAIN=\(quote(chained)) "
        #endif
    }

    static func currentCommand(home: URL? = nil) -> String? {
        (readSettings(home: home)?["statusLine"] as? [String: Any])?["command"] as? String
    }

    static func quote(_ s: String) -> String {
        #if os(Windows)
        return "\"\(s)\""
        #else
        return "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
        #endif
    }

    private static func readSettings(home: URL? = nil) -> [String: Any]? {
        guard let data = try? Data(contentsOf: settingsURL(home: home)),
              let object = try? JSONSerialization.jsonObject(with: data) else { return nil }
        return object as? [String: Any]
    }

    private static func writeSettings(_ settings: [String: Any], home: URL? = nil) -> Bool {
        let url = settingsURL(home: home)
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: settings,
                                                  options: [.prettyPrinted, .sortedKeys])
            try data.write(to: url, options: .atomic)
            return true
        } catch {
            return false
        }
    }
}
