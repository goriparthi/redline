// Ask Claude Code to refresh its own token rather than minting one behind its back. Running
// `claude auth status` makes the CLI touch its credential; if it renews an expired one, the
// next Keychain read finds a fresh token and nothing else was needed.
//
// Whether that renewal happens is not documented, so this measures it instead of assuming it:
// the caller compares the stored expiry before and after, and stops paying for the process
// once delegation proves ineffective on this machine.
import Foundation
import RedlineCore

enum DelegatedRefresh {
    enum Outcome: Equatable {
        case ran
        case cliUnavailable
        case skippedByCooldown
        case failed(String)
    }

    /// Long enough for a cold node start, short enough that a hung CLI cannot wedge the poll.
    private static let timeout: TimeInterval = 25
    private static let cooldown: TimeInterval = 300

    private static let lock = NSLock()
    private static var lastAttempt: Date?
    private static var running = false

    /// `nil` when no `claude` is installed. Checks the documented install locations before
    /// falling back to the login shell's PATH, which a LaunchAgent does not inherit.
    static func binary(home: URL? = nil) -> URL? {
        let root = home ?? FileManager.default.homeDirectoryForCurrentUser
        let candidates = [
            root.appendingPathComponent(".claude/local/claude"),
            URL(fileURLWithPath: "/opt/homebrew/bin/claude"),
            URL(fileURLWithPath: "/usr/local/bin/claude"),
            root.appendingPathComponent(".local/bin/claude"),
        ]
        let fm = FileManager.default
        for url in candidates where fm.isExecutableFile(atPath: url.path) { return url }
        return nil
    }

    /// Single-flight and cooldown-gated: a stale token makes every poll want to refresh, and
    /// without both guards that becomes one CLI process per poll, per window, forever.
    static func attempt(now: Date = Date(), home: URL? = nil) -> Outcome {
        lock.lock()
        if running {
            lock.unlock()
            return .skippedByCooldown
        }
        if let last = lastAttempt, now.timeIntervalSince(last) < cooldown {
            lock.unlock()
            return .skippedByCooldown
        }
        guard let bin = binary(home: home) else {
            lock.unlock()
            return .cliUnavailable
        }
        running = true
        lastAttempt = now
        lock.unlock()
        defer {
            lock.lock()
            running = false
            lock.unlock()
        }

        let proc = Process()
        proc.executableURL = bin
        proc.arguments = ["auth", "status", "--json"]
        // Never inherit this app's stdin, and never let the CLI decide to be interactive
        proc.standardInput = FileHandle.nullDevice
        let out = Pipe()
        proc.standardOutput = out
        proc.standardError = Pipe()
        guard (try? proc.run()) != nil else { return .failed("could not launch \(bin.path)") }

        let deadline = Date().addingTimeInterval(timeout)
        while proc.isRunning, Date() < deadline { usleep(50_000) }
        if proc.isRunning {
            proc.terminate()
            return .failed("timed out")
        }
        guard proc.terminationStatus == 0 else {
            return .failed("exit \(proc.terminationStatus)")
        }
        return .ran
    }

    /// Lets the caller forget a cooldown when the user explicitly asks to reconnect. A manual
    /// action should never be answered with "not yet".
    static func resetCooldown() {
        lock.lock()
        lastAttempt = nil
        lock.unlock()
    }
}
