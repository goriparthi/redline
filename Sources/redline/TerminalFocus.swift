// Finds the app a Claude Code session is running inside, by walking the process tree up from
// its PID until an ancestor turns out to be a running application, and brings it forward.
import AppKit

enum TerminalFocus {
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

    @discardableResult
    static func focus(pid: Int32) -> Bool {
        guard let app = owner(of: pid) else { return false }
        return app.activate()
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
