// Reads Claude Code's live session registry (~/.claude/sessions/<PID>.json), one file per
// running session, so RedLine can say which sessions are working and which are blocked.
import Foundation
#if os(Windows)
import WinSDK
#endif

/// Where a session is, as far as its own record admits. Kept as a closed set only for
/// ordering; the raw string survives on the session so an upstream addition still displays.
public enum FleetState: Int, Comparable {
    /// Blocked on the user. The one state this whole feature exists to surface.
    case waiting = 0
    case busy = 1
    case idle = 2
    case unknown = 3

    public static func < (a: FleetState, b: FleetState) -> Bool { a.rawValue < b.rawValue }

    static func of(_ raw: String?) -> FleetState {
        switch raw {
        case "waiting": return .waiting
        case "busy":    return .busy
        case "idle":    return .idle
        default:        return .unknown
        }
    }
}

/// One live Claude Code session.
///
/// Only `pid` and `cwd` are required. Everything else is undocumented internals that will
/// churn, so a missing or renamed field degrades one row rather than dropping the session.
public struct FleetSession: Equatable {
    public var pid: Int32
    public var cwd: String
    public var sessionId: String?
    public var name: String?
    /// Verbatim from the record. `state` is the interpreted form; this is what it said.
    public var status: String?
    /// Present only while waiting; observed as "input needed"
    public var waitingFor: String?
    public var statusUpdatedAt: Date?
    public var startedAt: Date?
    public var version: String?
    /// Open ended by design. Observed "cli"; the bundle also carries other strings, and
    /// treating this as an enum would drop sessions rather than label them.
    public var entrypoint: String?
    public var kind: String?
    public var bridgeSessionId: String?
    /// The file this was read from. Carried so a caller can watch it: a status change
    /// rewrites the record in place, which a watcher on the directory never sees.
    public var recordPath: String?

    public init(pid: Int32, cwd: String) {
        self.pid = pid
        self.cwd = cwd
    }

    public var state: FleetState { FleetState.of(status) }

    /// What the row calls the session: its own name when it has one, else the folder.
    public var label: String {
        if let name, !name.isEmpty { return name }
        return folder
    }

    public var folder: String {
        URL(fileURLWithPath: cwd).lastPathComponent
    }

    /// How long it has been in the current status, which for a waiting session is how long
    /// it has been sitting there unattended.
    public func timeInStatus(now: Date = Date()) -> TimeInterval? {
        guard let statusUpdatedAt else { return nil }
        return max(0, now.timeIntervalSince(statusUpdatedAt))
    }

    /// The one free bridge between the local session and its cloud view.
    public var claudeURL: URL? {
        guard let bridgeSessionId, !bridgeSessionId.isEmpty else { return nil }
        return URL(string: "https://claude.ai/code/\(bridgeSessionId)")
    }
}

public struct FleetSnapshot: Equatable {
    /// Sorted waiting first, then busy, then idle, each by longest in that status.
    public var sessions: [FleetSession] = []

    public init(sessions: [FleetSession] = []) { self.sessions = sessions }

    public var isEmpty: Bool { sessions.isEmpty }
    public var waiting: [FleetSession] { sessions.filter { $0.state == .waiting } }
    public var busy: [FleetSession] { sessions.filter { $0.state == .busy } }
}

/// Whether a PID is alive, and when it started. Injected so the tests can present a dead
/// process without killing anything.
public struct ProcessProbe {
    public var startTime: (Int32) -> Date?

    public init(startTime: @escaping (Int32) -> Date?) { self.startTime = startTime }

    public static let live = ProcessProbe(startTime: ProcessProbe.kernelStartTime)

    // Three kernels, one pair of questions: when did this process start, and what terminal
    // is it attached to. Absent start time means the process is gone, which is also the
    // liveness answer, so one call covers both questions.

#if canImport(Darwin)

    public static func kernelStartTime(pid: Int32) -> Date? {
        guard let info = procInfo(pid: pid) else { return nil }
        let tv = info.kp_proc.p_un.__p_starttime
        return Date(timeIntervalSince1970: Double(tv.tv_sec) + Double(tv.tv_usec) / 1_000_000)
    }

    /// The process's controlling terminal, as `/dev/ttysNNN`, or nil when it has none.
    ///
    /// This is what identifies a session inside its terminal app. A record says which app a
    /// session runs in but not which tab, and both iTerm2 and Terminal publish the tty of
    /// every tab, so the tty is the join between the two.
    public static func ttyPath(pid: Int32) -> String? {
        guard let info = procInfo(pid: pid) else { return nil }
        let dev = info.kp_eproc.e_tdev
        guard dev != -1, let name = devname(dev, S_IFCHR) else { return nil }
        let base = String(cString: name)
        return base.isEmpty ? nil : "/dev/" + base
    }

    private static func procInfo(pid: Int32) -> kinfo_proc? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        let ok = mib.withUnsafeMutableBufferPointer { buf -> Bool in
            sysctl(buf.baseAddress, UInt32(buf.count), &info, &size, nil, 0) == 0
        }
        guard ok, size > 0, info.kp_proc.p_pid == pid else { return nil }
        return info
    }

#elseif os(Linux)

    /// /proc counts a start in clock ticks since boot rather than in seconds since the epoch,
    /// so the boot instant has to be added back before it means anything.
    public static func kernelStartTime(pid: Int32) -> Date? {
        guard let fields = statFields(pid: pid), fields.count > 19,
              let ticks = Double(fields[19]), let boot = bootTime else { return nil }
        return Date(timeIntervalSince1970: boot + ticks / Double(clockTicks))
    }

    public static func ttyPath(pid: Int32) -> String? {
        guard let fields = statFields(pid: pid), fields.count > 4,
              let raw = Int32(fields[4]), raw != 0 else { return nil }
        // The kernel packs the device the new way, with the minor split either side of the
        // major, so it cannot simply be masked off in one go.
        let major = (Int(raw) >> 8) & 0xfff
        let minor = (Int(raw) & 0xff) | ((Int(raw) >> 12) & 0xfff00)
        let path: String
        switch major {
        case 136...143: path = "/dev/pts/\((major - 136) * 256 + minor)"   // UNIX98 pseudo-tty
        case 4:         path = "/dev/tty\(minor)"                          // virtual console
        default:        return nil
        }
        return FileManager.default.fileExists(atPath: path) ? path : nil
    }

    private static let clockTicks: Int = {
        let hz = sysconf(Int32(_SC_CLK_TCK))
        return hz > 0 ? hz : 100
    }()

    /// Seconds since the epoch at boot, from /proc/stat. Read once: it does not move.
    private static let bootTime: Double? = {
        guard let text = try? String(contentsOfFile: "/proc/stat", encoding: .utf8) else {
            return nil
        }
        for line in text.split(separator: "\n") where line.hasPrefix("btime ") {
            return Double(line.dropFirst("btime ".count).trimmingCharacters(in: .whitespaces))
        }
        return nil
    }()

    /// The fields of /proc/<pid>/stat after the command name, so index 0 is the state.
    ///
    /// Split from the last close paren rather than the first, because the command name is
    /// parenthesised and is allowed to contain both spaces and parens of its own.
    private static func statFields(pid: Int32) -> [String]? {
        guard let text = try? String(contentsOfFile: "/proc/\(pid)/stat", encoding: .utf8),
              let close = text.lastIndex(of: ")") else { return nil }
        return text[text.index(after: close)...]
            .split(separator: " ", omittingEmptySubsequences: true).map(String.init)
    }

#elseif os(Windows)

    // UNVERIFIED: written from the documentation, never compiled. See notes/cross-platform.md.
    public static func kernelStartTime(pid: Int32) -> Date? {
        // BOOL is Int32 here, not Bool: 0 for bInheritHandle, and the result compared to 0
        guard pid > 0,
              let handle = OpenProcess(DWORD(PROCESS_QUERY_LIMITED_INFORMATION), 0,
                                       DWORD(pid)) else { return nil }
        defer { CloseHandle(handle) }
        var created = FILETIME(), exited = FILETIME()
        var kernel = FILETIME(), user = FILETIME()
        guard GetProcessTimes(handle, &created, &exited, &kernel, &user) != 0 else { return nil }
        // FILETIME counts 100ns intervals from 1601, which is 11644473600 seconds before 1970
        let ticks = (UInt64(created.dwHighDateTime) << 32) | UInt64(created.dwLowDateTime)
        guard ticks > 0 else { return nil }
        return Date(timeIntervalSince1970: Double(ticks) / 10_000_000 - 11_644_473_600)
    }

    /// Windows has no controlling terminal to name, so a session is never joined to a tab
    /// this way. The shell falls back to focusing the window.
    public static func ttyPath(pid: Int32) -> String? { nil }

#endif
}

/// Scans the live session registry.
///
/// Scope is deliberately local-only. Cloud sessions and sessions on other Macs have no public
/// API, so reaching them would mean a headless bridge session or the private claude.ai API,
/// and neither is worth the coupling. Codex has no equivalent registry at all: CodexStore
/// reads finished transcripts, not live state, so the two must not be unified.
public final class ClaudeFleetStore {
    private let root: URL
    private let probe: ProcessProbe
    /// ctime format, as Claude Code writes it. Observed in UTC on 2026-08-18, but the field
    /// carries no zone, so both readings are kept and either may match; see isLive.
    private static func ctimeFormat(utc: Bool) -> DateFormatter {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "EEE MMM d HH:mm:ss yyyy"
        if utc { f.timeZone = TimeZone(secondsFromGMT: 0) }
        return f
    }
    private let procStartUTC = ClaudeFleetStore.ctimeFormat(utc: true)
    private let procStartLocal = ClaudeFleetStore.ctimeFormat(utc: false)

    /// One record per running session, deleted on exit. Watched, never written to.
    public static var defaultRoot: URL { RedlineHome.path(".claude/sessions") }

    public init(root: URL? = nil, probe: ProcessProbe = .live) {
        self.root = root ?? ClaudeFleetStore.defaultRoot
        self.probe = probe
    }

    public func scan(now: Date = Date()) -> FleetSnapshot {
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: root.path) else {
            // No directory is the normal state on a Mac without Claude Code, not an error
            return FleetSnapshot()
        }
        var out: [FleetSession] = []
        for name in names {
            // Siblings named <PID>.<hash>.key hold secrets. Only the plain record is read.
            guard name.hasSuffix(".json"), !name.contains(".key") else { continue }
            let url = root.appendingPathComponent(name)
            guard let data = try? Data(contentsOf: url),
                  var record = decode(data) else { continue }
            guard isLive(record) else { continue }
            record.session.recordPath = url.path
            out.append(record.session)
        }
        return FleetSnapshot(sessions: sorted(out, now: now))
    }

    /// A decoded record plus the start time it claims, which is not part of the session the
    /// UI sees but is what proves the record is not a leftover.
    struct Record {
        var session: FleetSession
        /// The claimed start read both ways, because the field states no zone
        var procStart: [Date]
    }

    /// Decodes one record by hand rather than through Codable so an unknown field is ignored
    /// and a missing one costs a property instead of the whole session.
    func decode(_ data: Data) -> Record? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pid = int32(obj["pid"]), pid > 0,
              let cwd = obj["cwd"] as? String, !cwd.isEmpty else { return nil }
        var s = FleetSession(pid: pid, cwd: cwd)
        s.sessionId = obj["sessionId"] as? String
        s.name = obj["name"] as? String
        s.status = obj["status"] as? String
        s.waitingFor = obj["waitingFor"] as? String
        s.version = obj["version"] as? String
        s.entrypoint = obj["entrypoint"] as? String
        s.kind = obj["kind"] as? String
        s.bridgeSessionId = obj["bridgeSessionId"] as? String
        s.statusUpdatedAt = epochMillis(obj["statusUpdatedAt"]) ?? epochMillis(obj["updatedAt"])
        s.startedAt = epochMillis(obj["startedAt"])
        // ctime space pads a single digit day, which the formatter will not match
        let raw = (obj["procStart"] as? String)?
            .replacingOccurrences(of: "  ", with: " ")
        let claimed = [procStartUTC, procStartLocal].compactMap { f in
            raw.flatMap { f.date(from: $0) }
        }
        return Record(session: s, procStart: claimed)
    }

    /// A record outlives its process when a session is killed, and PIDs are reused, so the
    /// claimed start is checked against the kernel's. A stale record reads as absent; nothing
    /// here ever deletes from ~/.claude.
    ///
    /// Either zone reading may match, because `procStart` names none and reading it wrong
    /// would empty the whole pane rather than drop the one recycled PID this guards against.
    /// A reuse still has to land on the same second to slip through.
    private func isLive(_ r: Record) -> Bool {
        guard let actual = probe.startTime(r.session.pid) else { return false }
        guard !r.procStart.isEmpty else { return true }
        return r.procStart.contains { abs(actual.timeIntervalSince($0)) < 2 }
    }

    /// Waiting first, because that is the only state that needs a person. Within a state the
    /// oldest floats up, so the session that has been stuck longest is the one on top.
    func sorted(_ sessions: [FleetSession], now: Date) -> [FleetSession] {
        sessions.sorted { a, b in
            if a.state != b.state { return a.state < b.state }
            let ta = a.statusUpdatedAt ?? .distantPast
            let tb = b.statusUpdatedAt ?? .distantPast
            if ta != tb { return ta < tb }
            return a.pid < b.pid
        }
    }

    private func int32(_ v: Any?) -> Int32? {
        if let i = v as? Int { return Int32(truncatingIfNeeded: i) }
        if let d = v as? Double { return Int32(d) }
        return nil
    }

    private func epochMillis(_ v: Any?) -> Date? {
        let ms: Double
        if let i = v as? Int { ms = Double(i) }
        else if let d = v as? Double { ms = d }
        else { return nil }
        guard ms > 0 else { return nil }
        return Date(timeIntervalSince1970: ms / 1000)
    }
}
