// One RedLine per login session, whatever route started it. LaunchServices only refuses a
// second copy of the same bundle path, so a build in dist/ and the installed app in
// ~/Applications happily ran side by side and put two icons in the menu bar.
import Foundation
#if os(Windows)
import WinSDK
#endif

public final class SingleInstance {
    #if os(Windows)
    // HANDLE itself is not optional; CreateMutexW is what returns one
    private let handle: HANDLE?
    private init(handle: HANDLE?) { self.handle = handle }
    #else
    private let fd: Int32
    private init(fd: Int32) { self.fd = fd }
    #endif

    public static var lockURL: URL {
        AppPaths.data("instance.lock")
    }

    /// The name a second copy collides with on Windows, derived from the lock file's own name
    /// so one argument picks both. Session-local rather than global, so two people signed into
    /// the same machine each get their own RedLine.
    static func mutexName(for url: URL) -> String {
        "Local\\com.goriparthi.redline." + url.deletingPathExtension().lastPathComponent
    }

    /// nil when another process already holds the lock. The lock lives exactly as long as the
    /// returned object, so the caller must hold it for the life of the process; the kernel
    /// releases it on exit or crash, so a killed app never leaves the next one locked out.
    public static func claim(at url: URL = lockURL) -> SingleInstance? {
        #if os(Windows)
        // A named mutex rather than a lock file: Windows has no flock, and an abandoned mutex
        // is handed to the next waiter automatically, which is the behaviour we want after a
        // crash.
        let handle = mutexName(for: url).withCString(encodedAs: UTF16.self) {
            CreateMutexW(nil, true, $0)
        }
        // A mutex that will not open must not stop the app from starting, same as the
        // unopenable lock file below
        guard let handle else { return SingleInstance(handle: nil) }
        if GetLastError() == DWORD(ERROR_ALREADY_EXISTS) {
            CloseHandle(handle)
            return nil
        }
        return SingleInstance(handle: handle)
        #else
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        let fd = open(url.path, O_CREAT | O_RDWR, 0o600)
        // A lock file that will not even open must not stop the app from starting. This guard
        // exists to prevent a second menu bar icon, not to gate launching at all.
        guard fd >= 0 else { return SingleInstance(fd: -1) }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            close(fd)
            return nil
        }
        return SingleInstance(fd: fd)
        #endif
    }

    deinit {
        #if os(Windows)
        guard let handle else { return }
        ReleaseMutex(handle)
        CloseHandle(handle)
        #else
        guard fd >= 0 else { return }
        flock(fd, LOCK_UN)
        close(fd)
        #endif
    }
}
