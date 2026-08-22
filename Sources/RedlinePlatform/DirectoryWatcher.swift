// Watching a directory for change, on three kernels that do not agree on how.
//
// The app needs this because Claude Code rewrites its sidecar and its session records while
// RedLine is running, and a poll interval measured in minutes would make the menu bar lie.
import Foundation
import RedlineCore
#if canImport(Darwin)
import Darwin
#elseif os(Linux)
import Glibc
#elseif os(Windows)
import WinSDK
#endif

/// A watch that lives exactly as long as this object.
public protocol DirectoryWatching: AnyObject {
    func cancel()
}

public enum DirectoryWatcher {
    /// Calls `onChange` when anything in `directory` is created, written or removed.
    ///
    /// Coalescing is the caller's business: every backend can report one logical edit more
    /// than once, and a writer that replaces a file atomically always does.
    ///
    /// Returns nil only when the directory cannot be opened at all. A caller that must not
    /// miss a change should keep its periodic sweep regardless; this makes the common case
    /// prompt, it does not make the sweep unnecessary.
    public static func watch(_ directory: URL,
                             queue: DispatchQueue,
                             onChange: @escaping () -> Void) -> DirectoryWatching? {
        #if canImport(Darwin)
        return VnodeWatcher(directory, queue: queue, onChange: onChange)
        #elseif os(Linux)
        return INotifyWatcher(directory, queue: queue, onChange: onChange)
        #elseif os(Windows)
        return ReadDirectoryWatcher(directory, queue: queue, onChange: onChange)
        #else
        return nil
        #endif
    }
}

#if canImport(Darwin)

/// A vnode source on an open descriptor, which is how this has always worked on macOS.
private final class VnodeWatcher: DirectoryWatching {
    private let source: DispatchSourceFileSystemObject

    init?(_ directory: URL, queue: DispatchQueue, onChange: @escaping () -> Void) {
        let fd = open(directory.path, O_EVTONLY)
        guard fd >= 0 else { return nil }
        source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .rename, .delete, .extend], queue: queue)
        source.setEventHandler(handler: onChange)
        source.setCancelHandler { close(fd) }
        source.resume()
    }

    func cancel() { source.cancel() }
    deinit { source.cancel() }
}

#elseif os(Linux)

/// inotify, read through a Dispatch source so nothing blocks a thread.
private final class INotifyWatcher: DirectoryWatching {
    private let fd: Int32
    private let source: DispatchSourceRead
    private let lock = NSLock()
    private var cancelled = false

    init?(_ directory: URL, queue: DispatchQueue, onChange: @escaping () -> Void) {
        fd = inotify_init1(Int32(IN_NONBLOCK | IN_CLOEXEC))
        guard fd >= 0 else { return nil }
        let mask = UInt32(IN_CREATE | IN_CLOSE_WRITE | IN_MOVED_TO | IN_MOVED_FROM
                          | IN_DELETE | IN_MODIFY)
        guard inotify_add_watch(fd, directory.path, mask) >= 0 else {
            close(fd)
            return nil
        }
        source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        source.setEventHandler { [fd] in
            // The events themselves are not inspected, only that something happened. They
            // still have to be drained or the descriptor stays readable forever.
            var buffer = [UInt8](repeating: 0, count: 4096)
            while read(fd, &buffer, buffer.count) > 0 { }
            onChange()
        }
        source.setCancelHandler { [fd] in close(fd) }
        source.resume()
    }

    func cancel() {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { return }
        cancelled = true
        source.cancel()
    }

    deinit { cancel() }
}

#elseif os(Windows)

/// ReadDirectoryChangesW on its own thread, because the call blocks until something happens.
private final class ReadDirectoryWatcher: DirectoryWatching {
    private let handle: HANDLE
    private let lock = NSLock()
    private var cancelled = false

    init?(_ directory: URL, queue: DispatchQueue, onChange: @escaping () -> Void) {
        // FILE_FLAG_BACKUP_SEMANTICS is what makes CreateFileW accept a directory at all
        let opened: HANDLE? = directory.path.withCString(encodedAs: UTF16.self) { path in
            CreateFileW(path,
                        DWORD(FILE_LIST_DIRECTORY),
                        DWORD(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE),
                        nil,
                        DWORD(OPEN_EXISTING),
                        DWORD(FILE_FLAG_BACKUP_SEMANTICS),
                        nil)
        }
        guard let opened, opened != INVALID_HANDLE_VALUE else { return nil }
        handle = opened

        let thread = Thread { [handle] in
            var buffer = [UInt8](repeating: 0, count: 8192)
            while true {
                var written: DWORD = 0
                let ok = buffer.withUnsafeMutableBytes { raw in
                    ReadDirectoryChangesW(
                        handle, raw.baseAddress, DWORD(raw.count), false,
                        DWORD(FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME
                              | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE),
                        &written, nil, nil)
                }
                // A cancelled or closed handle ends the loop, which is the only way out
                guard ok, written > 0 else { return }
                queue.async(execute: onChange)
            }
        }
        thread.stackSize = 512 * 1024
        thread.start()
    }

    func cancel() {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { return }
        cancelled = true
        // Cancels the blocking read, which lets the thread fall out of its loop
        CancelIoEx(handle, nil)
        CloseHandle(handle)
    }

    deinit { cancel() }
}

#endif
