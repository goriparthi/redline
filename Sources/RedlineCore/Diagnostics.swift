// Structured diagnostics as newline-delimited JSON, so a failure that used to vanish into a
// `try?` leaves a record a human or a tool can read back.
//
// NDJSON rather than the unified log on purpose: the point of this file is that it can be
// grepped, parsed and diffed without `log show`, which is what makes an eval loop over real
// failures possible. One event per line, append only, rotated by size.
import Foundation

public enum DiagLevel: String, Codable, Sendable, CaseIterable, Comparable {
    case debug, info, warn, error

    private var rank: Int {
        switch self {
        case .debug: return 0
        case .info: return 1
        case .warn: return 2
        case .error: return 3
        }
    }

    public static func < (a: DiagLevel, b: DiagLevel) -> Bool { a.rank < b.rank }
}

/// One thing that happened. `code` is a stable dotted slug and is the field to group on;
/// `message` is for a human and may change wording without breaking a query.
public struct DiagEvent: Codable, Equatable, Sendable {
    public let at: String                  // ISO-8601, UTC, always
    public let level: DiagLevel
    public let code: String
    public let message: String
    public let context: [String: String]
    public let version: String

    public init(at: String, level: DiagLevel, code: String, message: String,
                context: [String: String] = [:], version: String) {
        self.at = at
        self.level = level
        self.code = code
        self.message = message
        self.context = context
        self.version = version
    }

    enum CodingKeys: String, CodingKey {
        case at, level, code, message = "msg", context = "ctx", version = "v"
    }
}

/// Appends events to a file. Injectable path and clock so tests run against a temp directory,
/// the same way every scanner in this package does.
public final class DiagnosticsLog: @unchecked Sendable {
    public static let maxBytes = 1_048_576        // rotate at 1 MB
    public static let keptGenerations = 2         // diagnostics.ndjson plus .1

    private let url: URL
    private let version: String
    private let lock = NSLock()
    /// Below this, an event is built and dropped. Debug is off unless asked for, because the
    /// file is meant to stay small enough to read end to end.
    public var minimumLevel: DiagLevel

    public init(url: URL, version: String, minimumLevel: DiagLevel = .info) {
        self.url = url
        self.version = version
        self.minimumLevel = minimumLevel
    }

    public static func defaultURL(home: URL? = nil) -> URL {
        AppPaths.data("diagnostics.ndjson", in: home)
    }

    public func log(_ level: DiagLevel, _ code: String, _ message: String,
                    context: [String: String] = [:], now: Date = Date()) {
        guard level >= minimumLevel else { return }
        let event = DiagEvent(at: Self.stamp(now), level: level, code: code,
                              message: Redaction.scrub(message),
                              context: context.mapValues(Redaction.scrub),
                              version: version)
        append(event)
    }

    public func debug(_ code: String, _ m: String, _ c: [String: String] = [:]) {
        log(.debug, code, m, context: c)
    }
    public func info(_ code: String, _ m: String, _ c: [String: String] = [:]) {
        log(.info, code, m, context: c)
    }
    public func warn(_ code: String, _ m: String, _ c: [String: String] = [:]) {
        log(.warn, code, m, context: c)
    }
    public func error(_ code: String, _ m: String, _ c: [String: String] = [:]) {
        log(.error, code, m, context: c)
    }

    /// Records a thrown error against a code. The returned value is nil, so a call site that
    /// was `try?` becomes `log.attempt("code") { try ... }` without changing its shape.
    @discardableResult
    public func attempt<T>(_ code: String, _ context: [String: String] = [:],
                           _ body: () throws -> T) -> T? {
        do {
            return try body()
        } catch {
            var c = context
            c["error"] = String(describing: error)
            self.error(code, "operation failed", c)
            return nil
        }
    }

    // MARK: - Reading

    /// Events currently on disk, oldest first, across every kept generation. A line that does
    /// not parse is skipped rather than failing the read: a truncated tail must not hide the
    /// events before it.
    public func read(minimumLevel: DiagLevel = .debug, since: Date? = nil,
                     limit: Int? = nil) -> [DiagEvent] {
        lock.lock()
        defer { lock.unlock() }
        let decoder = JSONDecoder()
        var out: [DiagEvent] = []
        for gen in stride(from: Self.keptGenerations - 1, through: 0, by: -1) {
            let file = gen == 0 ? url : url.appendingPathExtension("\(gen)")
            guard let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
            for line in text.split(separator: "\n") {
                guard let data = line.data(using: .utf8),
                      let e = try? decoder.decode(DiagEvent.self, from: data),
                      e.level >= minimumLevel else { continue }
                if let since, let at = Self.parse(e.at), at < since { continue }
                out.append(e)
            }
        }
        if let limit, out.count > limit { out = Array(out.suffix(limit)) }
        return out
    }

    /// How often each code fired, most frequent first. This is the view an eval loop wants:
    /// one recurring failure matters more than a long tail of one-offs.
    public func tally(minimumLevel: DiagLevel = .warn,
                      since: Date? = nil) -> [(code: String, count: Int, latest: String)] {
        var counts: [String: (Int, String)] = [:]
        for e in read(minimumLevel: minimumLevel, since: since) {
            let prior = counts[e.code]
            counts[e.code] = ((prior?.0 ?? 0) + 1, e.at)
        }
        return counts.map { (code: $0.key, count: $0.value.0, latest: $0.value.1) }
            .sorted { $0.count == $1.count ? $0.code < $1.code : $0.count > $1.count }
    }

    // MARK: - Writing

    private func append(_ event: DiagEvent) {
        lock.lock()
        defer { lock.unlock() }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        guard var data = try? encoder.encode(event) else { return }
        data.append(0x0A)

        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        rotateIfNeeded(adding: data.count)
        // A diagnostics write must never take the app down with it, so every failure here is
        // deliberately silent: there is nowhere left to report it to.
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }

    private func rotateIfNeeded(adding bytes: Int) {
        let fm = FileManager.default
        let attrs = try? fm.attributesOfItem(atPath: url.path)
        let size = attrs.flatMap { $0[.size] as? Int } ?? 0
        guard size + bytes > Self.maxBytes else { return }
        let older = url.appendingPathExtension("\(Self.keptGenerations - 1)")
        try? fm.removeItem(at: older)
        try? fm.moveItem(at: url, to: older)
    }

    // MARK: - Time

    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        f.timeZone = TimeZone(identifier: "UTC")
        return f
    }()

    static func stamp(_ d: Date) -> String { formatter.string(from: d) }
    static func parse(_ s: String) -> Date? { formatter.date(from: s) }
}

/// Keeps secrets and personal paths out of a file whose whole purpose is to be shared.
public enum Redaction {
    /// Anything that looks like a bearer token, an OAuth code or a key. Deliberately blunt:
    /// a redacted diagnostic is useful, a leaked token is not recoverable.
    private static let secretish = [
        "sk-", "sk_", "oauth", "bearer", "token", "secret", "password", "authorization",
    ]

    public static func scrub(_ s: String) -> String {
        var out = s
        // Home first, so a path under it does not survive as an absolute one
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        if !home.isEmpty { out = out.replacingOccurrences(of: home, with: "~") }
        let lowered = out.lowercased()
        guard secretish.contains(where: lowered.contains) else { return out }
        // Replace any long opaque run next to a secret-ish word rather than dropping the
        // whole string, which would throw away the part that says what failed.
        return out.split(separator: " ").map { word -> String in
            let w = String(word)
            let bare = w.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
            return bare.count >= 20 && bare.rangeOfCharacter(from: .decimalDigits) != nil
                ? "<redacted>" : w
        }.joined(separator: " ")
    }
}

/// The process-wide log. Configured once at startup so call sites stay a single expression;
/// unconfigured it still writes to the default path, because a diagnostic lost to setup order
/// is exactly the kind of gap this file exists to close.
public enum Diag {
    private static let lock = NSLock()
    private static var instance: DiagnosticsLog?

    public static func configure(version: String, url: URL? = nil,
                                 minimumLevel: DiagLevel = .info) {
        lock.lock()
        defer { lock.unlock() }
        instance = DiagnosticsLog(url: url ?? DiagnosticsLog.defaultURL(),
                                  version: version, minimumLevel: minimumLevel)
    }

    public static var log: DiagnosticsLog {
        lock.lock()
        defer { lock.unlock() }
        if let instance { return instance }
        let made = DiagnosticsLog(url: DiagnosticsLog.defaultURL(), version: "unknown")
        instance = made
        return made
    }
}
