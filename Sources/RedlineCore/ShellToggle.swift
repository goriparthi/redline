// The shape a shell reads for the commands that are on or off rather than a value.
// Same vocabulary as ConfigEditor's outcomes, so one reader in another language covers both.
import Foundation

public enum ShellToggle {
    /// Where things stand, without changing anything.
    public static func status(key: String, on: Bool,
                              extras: [String: String] = [:]) -> [String: Any] {
        var row: [String: Any] = ["outcome": "status", "key": key, "on": on]
        for (name, value) in extras { row[name] = value }
        return row
    }

    /// It is now on or off, and it was not before.
    public static func changed(key: String, on: Bool,
                               extras: [String: String] = [:]) -> [String: Any] {
        var row: [String: Any] = ["outcome": "changed", "key": key, "on": on]
        for (name, value) in extras { row[name] = value }
        return row
    }

    /// It was already that. Not a failure, and a shell that reported one would be arguing
    /// with someone about a change they did not make.
    public static func unchanged(key: String, on: Bool,
                                 extras: [String: String] = [:]) -> [String: Any] {
        var row: [String: Any] = ["outcome": "unchanged", "key": key, "on": on]
        for (name, value) in extras { row[name] = value }
        return row
    }

    public static func failed(key: String, message: String) -> [String: Any] {
        ["outcome": "failed", "key": key, "message": message]
    }

    /// The keys these commands are known by on the other side of the boundary.
    public static let autostartKey = "autostart"
    public static let usageFeedKey = "usageFeed"
}
