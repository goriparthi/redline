// The two directories RedLine owns, resolved per platform. Every path the core reads or
// writes goes through here so a port never has to hunt down a scattered path literal.
import Foundation

public enum AppPaths {
    /// The layout under an explicit home. Every platform agrees here, so the end to end suite
    /// and a REDLINE_HOME profile see one shape rather than three.
    static let dataUnderHome = ".local/share/redline"
    static let configUnderHome = ".config/redline"

    /// Everything RedLine writes: history, the snapshot, the sidecar, logs and the lock.
    ///
    /// macOS keeps the XDG-shaped location it has always used rather than moving to
    /// Application Support, because an existing install's history already lives there. Linux
    /// honours XDG_DATA_HOME. Windows uses LocalAppData, which is the equivalent and, unlike
    /// roaming AppData, does not follow the user onto another machine.
    public static var data: URL { data(in: nil) }

    /// config.json and nothing else.
    public static var config: URL { config(in: nil) }

    public static func data(in home: URL?) -> URL {
        if let home { return home.appendingPathComponent(dataUnderHome) }
        return resolve(xdg: "XDG_DATA_HOME", underHome: dataUnderHome, windows: "LOCALAPPDATA")
    }

    public static func config(in home: URL?) -> URL {
        if let home { return home.appendingPathComponent(configUnderHome) }
        return resolve(xdg: "XDG_CONFIG_HOME", underHome: configUnderHome, windows: "APPDATA")
    }

    public static func data(_ name: String, in home: URL? = nil) -> URL {
        data(in: home).appendingPathComponent(name)
    }

    public static func config(_ name: String, in home: URL? = nil) -> URL {
        config(in: home).appendingPathComponent(name)
    }

    /// True for a path the platform treats as absolute.
    ///
    /// Windows counts a drive-qualified path and a UNC share, neither of which starts with a
    /// slash, so a bare `hasPrefix("/")` rejects every real Windows path.
    public static func isAbsolute(_ path: String) -> Bool {
        #if os(Windows)
        if path.hasPrefix(#"\\"#) || path.hasPrefix("//") { return true }
        let c = Array(path)
        return c.count >= 3 && c[0].isLetter && c[1] == ":" && (c[2] == #"\"# || c[2] == "/")
        #else
        return path.hasPrefix("/")
        #endif
    }

    /// A run pointed at a test home ignores XDG and LocalAppData entirely, so a test can never
    /// reach the real directories however the environment is set.
    private static func resolve(xdg: String, underHome: String, windows: String) -> URL {
        if RedlineHome.isOverridden { return RedlineHome.path(underHome) }
        let env = ProcessInfo.processInfo.environment
        #if os(Windows)
        if let base = env[windows], !base.isEmpty {
            return URL(fileURLWithPath: base, isDirectory: true)
                .appendingPathComponent("RedLine")
        }
        #else
        if let base = env[xdg], base.hasPrefix("/") {
            return URL(fileURLWithPath: base, isDirectory: true)
                .appendingPathComponent("redline")
        }
        #endif
        return RedlineHome.path(underHome)
    }
}
