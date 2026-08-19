// A thin wrapper over the SQLite that macOS already ships. No package dependency: `import
// SQLite3` resolves against the SDK's module map, which links libsqlite3 for us.
//
// Deliberately small. This is a statement runner and a migration ledger, not an ORM: every
// query in this app is written as SQL where it is used, so the shape of the question stays
// visible next to the answer.
import Foundation
import SQLite3

/// SQLite copies bound text and blobs only when told to. Without this, a Swift String that
/// goes out of scope before the step leaves the statement pointing at freed memory.
private let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

public struct DatabaseError: Error, CustomStringConvertible {
    public let code: Int32
    public let message: String
    public var description: String { "sqlite error \(code): \(message)" }
}

public final class Database {
    private var handle: OpaquePointer?
    private let lock = NSLock()

    /// Opens, creating the file and its directory. FULLMUTEX because the app scans on one
    /// queue and the dashboard on another, and both hold their own Warehouse; WAL because
    /// the CLI is a second process that must be able to read during a write.
    public init(url: URL) throws {
        try? FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        let rc = sqlite3_open_v2(url.path, &handle, flags, nil)
        guard rc == SQLITE_OK, handle != nil else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "cannot open"
            sqlite3_close_v2(handle)
            handle = nil
            throw DatabaseError(code: rc, message: message)
        }
        // Usage and cost are nobody else's business on a shared machine, same as every other
        // file this app writes.
        try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                               ofItemAtPath: url.path)
        sqlite3_busy_timeout(handle, 3000)
        try execute("PRAGMA journal_mode = WAL")
        try execute("PRAGMA synchronous = NORMAL")
        try execute("PRAGMA foreign_keys = ON")
    }

    deinit { sqlite3_close_v2(handle) }

    public func execute(_ sql: String) throws {
        lock.lock()
        defer { lock.unlock() }
        var error: UnsafeMutablePointer<CChar>?
        let rc = sqlite3_exec(handle, sql, nil, nil, &error)
        guard rc == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? "unknown"
            sqlite3_free(error)
            throw DatabaseError(code: rc, message: message)
        }
    }

    /// Runs one statement, binding positionally, and hands each result row to `row`.
    public func query(_ sql: String, _ bindings: [Value] = [],
                      row: (Row) -> Void = { _ in }) throws {
        lock.lock()
        defer { lock.unlock() }
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(handle, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            throw error(prefix: sql)
        }
        defer { sqlite3_finalize(stmt) }
        for (i, value) in bindings.enumerated() { value.bind(to: stmt, at: Int32(i + 1)) }
        while true {
            let rc = sqlite3_step(stmt)
            if rc == SQLITE_ROW { row(Row(stmt: stmt)); continue }
            if rc == SQLITE_DONE { return }
            throw error(prefix: sql)
        }
    }

    /// Batches writes into one transaction. A poll can ingest thousands of rows, and a
    /// commit per row turns a fast write into a disk-bound one.
    public func transaction(_ body: () throws -> Void) throws {
        try execute("BEGIN IMMEDIATE")
        do {
            try body()
            try execute("COMMIT")
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    public var userVersion: Int {
        var version = 0
        try? query("PRAGMA user_version") { version = $0.int(0) }
        return version
    }

    public func setUserVersion(_ version: Int) throws {
        try execute("PRAGMA user_version = \(version)")
    }

    /// Reclaims space after a retention pass. Cheap to call and a no-op when nothing freed.
    public func compact() { try? execute("PRAGMA incremental_vacuum; VACUUM") }

    private func error(prefix: String) -> DatabaseError {
        let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "unknown"
        return DatabaseError(code: sqlite3_errcode(handle), message: "\(message) [\(prefix)]")
    }

    // MARK: - Values and rows

    public enum Value {
        case int(Int)
        case double(Double)
        case text(String)
        case null

        /// Dates are stored as unix seconds throughout: a REAL sorts, ranges and buckets in
        /// SQL, which an ISO string only does by accident of its format.
        public static func date(_ date: Date?) -> Value {
            guard let date else { return .null }
            return .double(date.timeIntervalSince1970)
        }

        func bind(to stmt: OpaquePointer, at index: Int32) {
            switch self {
            case .int(let v): sqlite3_bind_int64(stmt, index, Int64(v))
            case .double(let v): sqlite3_bind_double(stmt, index, v)
            case .text(let v): sqlite3_bind_text(stmt, index, v, -1, transient)
            case .null: sqlite3_bind_null(stmt, index)
            }
        }
    }

    public struct Row {
        let stmt: OpaquePointer

        public func int(_ column: Int32) -> Int { Int(sqlite3_column_int64(stmt, column)) }
        public func double(_ column: Int32) -> Double { sqlite3_column_double(stmt, column) }
        public func bool(_ column: Int32) -> Bool { int(column) != 0 }

        public func string(_ column: Int32) -> String {
            guard let c = sqlite3_column_text(stmt, column) else { return "" }
            return String(cString: c)
        }

        public func isNull(_ column: Int32) -> Bool {
            sqlite3_column_type(stmt, column) == SQLITE_NULL
        }

        public func date(_ column: Int32) -> Date? {
            isNull(column) ? nil : Date(timeIntervalSince1970: double(column))
        }
    }
}
