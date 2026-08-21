// Raises the terminal a Claude Code session is running in, and the session's own tab where
// the app can say which tab that is. Falls back to raising the app when it cannot.
import AppKit
import RedlineCore
import RedlineUI

enum TerminalFocus {
    /// How precisely a focus request landed, so the caller can say so rather than claiming
    /// more than happened.
    enum Result {
        case tab      // the session's own tab or split pane is now frontmost
        case app      // the right app came forward, but not to the right tab
        case failed
    }

    /// The GUI app hosting this PID, or nil when nothing in the chain is one. A session
    /// started by launchd or a bare login shell genuinely has no window to raise, which is
    /// why the caller needs a fallback rather than a retry.
    static func owner(of pid: Int32) -> NSRunningApplication? {
        var current = pid
        // The chain to a terminal is short (claude, shell, login, app); the bound is only
        // there so a cycle in a malformed process table cannot spin forever.
        for _ in 0..<12 {
            if let app = NSRunningApplication(processIdentifier: current),
               app.activationPolicy != .prohibited,
               app.bundleIdentifier != Bundle.main.bundleIdentifier {
                return app
            }
            guard let parent = parent(of: current), parent > 1, parent != current
            else { return nil }
            current = parent
        }
        return nil
    }

    /// True when this PID's terminal can be focused down to its own tab. Used to word the
    /// menu item, so it never promises a tab it will only deliver an app for.
    static func canFocusTab(pid: Int32) -> Bool {
        guard let app = owner(of: pid), script(for: app.bundleIdentifier) != nil else {
            return false
        }
        return ProcessProbe.ttyPath(pid: pid) != nil
    }

    /// Raises the session. Runs off the main thread: the first Apple event to a terminal
    /// puts up the macOS automation consent dialog, and waiting for that on the main thread
    /// beachballs the whole menu bar.
    static func focus(pid: Int32, completion: @escaping (Result) -> Void = { _ in }) {
        guard let app = owner(of: pid) else {
            completion(.failed)
            return
        }
        // Bring the app forward first, always. If the tab hop is refused or the app is not
        // scriptable, the user still lands somewhere useful instead of nowhere.
        let raised = app.activate()
        guard let build = script(for: app.bundleIdentifier),
              let tty = ProcessProbe.ttyPath(pid: pid), isPlainDevice(tty) else {
            completion(raised ? .app : .failed)
            return
        }
        queue.async {
            let ok = run(build(tty))
            DispatchQueue.main.async { completion(ok ? .tab : (raised ? .app : .failed)) }
        }
    }

    // Serial and off the main thread, because an Apple event can block on a consent dialog
    private static let queue = DispatchQueue(label: "terminal-focus", qos: .userInitiated)

    /// Only iTerm2 and Terminal publish the tty of each tab, which is the one field that
    /// joins a session to the window it is drawn in. Ghostty, WezTerm and Alacritty expose
    /// no such API, so for those the honest answer stays "the app came forward".
    private static func script(for bundleID: String?) -> ((String) -> String)? {
        switch bundleID {
        case "com.googlecode.iterm2": return iTermScript
        case "com.apple.Terminal":    return terminalScript
        default:                      return nil
        }
    }

    private static func iTermScript(tty: String) -> String {
        """
        tell application "iTerm2"
          repeat with w in windows
            repeat with t in tabs of w
              repeat with s in sessions of t
                if tty of s is "\(tty)" then
                  select w
                  select t
                  select s
                  return "ok"
                end if
              end repeat
            end repeat
          end repeat
        end tell
        return "no"
        """
    }

    private static func terminalScript(tty: String) -> String {
        """
        tell application "Terminal"
          repeat with w in windows
            repeat with t in tabs of w
              if tty of t is "\(tty)" then
                set selected of t to true
                set index of w to 1
                return "ok"
              end if
            end repeat
          end repeat
        end tell
        return "no"
        """
    }

    /// The tty is interpolated into a script, so it is checked against the only shape a
    /// device name can take rather than trusted because it came from the kernel.
    private static func isPlainDevice(_ path: String) -> Bool {
        guard path.hasPrefix("/dev/"), path.count <= 64 else { return false }
        return path.dropFirst(5).allSatisfy {
            $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" || $0 == "_"
        }
    }

    /// False on a refused consent dialog, a terminal that has closed since the menu was
    /// built, or a tty that matches no open tab. All three mean the same thing here: the
    /// app is forward, the tab is not, and nothing should claim otherwise.
    private static func run(_ source: String) -> Bool {
        var error: NSDictionary?
        let result = NSAppleScript(source: source)?.executeAndReturnError(&error)
        guard error == nil else { return false }
        return result?.stringValue == "ok"
    }

    private static func parent(of pid: Int32) -> Int32? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        let ok = mib.withUnsafeMutableBufferPointer { buf -> Bool in
            sysctl(buf.baseAddress, UInt32(buf.count), &info, &size, nil, 0) == 0
        }
        guard ok, size > 0, info.kp_proc.p_pid == pid else { return nil }
        return info.kp_eproc.e_ppid
    }
}
