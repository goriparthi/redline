// One RedLine per login session, whatever route started it. LaunchServices only refuses a
// second copy of the same bundle path, so a build in dist/ and the installed app in
// ~/Applications happily ran side by side and put two icons in the menu bar.
import Foundation

public final class SingleInstance {
    private let fd: Int32

    private init(fd: Int32) { self.fd = fd }

    public static var lockURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/redline/instance.lock")
    }

    /// nil when another process already holds the lock. The lock lives exactly as long as the
    /// returned object, so the caller must hold it for the life of the process; the kernel
    /// releases it on exit or crash, so a killed app never leaves the next one locked out.
    public static func claim(at url: URL = lockURL) -> SingleInstance? {
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
    }

    deinit {
        guard fd >= 0 else { return }
        flock(fd, LOCK_UN)
        close(fd)
    }
}
