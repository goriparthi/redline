// One test per property, run against whichever backend this platform compiled in. The point
// is not to test inotify or ReadDirectoryChangesW, it is to pin the contract all three owe
// the caller: a write in the directory wakes the callback, and cancelling stops it.
import XCTest
@testable import RedlinePlatform
import RedlineCore

final class DirectoryWatcherTests: XCTestCase {
    private var dir: URL!
    private let queue = DispatchQueue(label: "redline.tests.watcher")

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-watch-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func write(_ name: String, _ text: String = "x") throws {
        try text.write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8)
    }

    func testAFileAppearingWakesTheCallback() throws {
        let fired = expectation(description: "watcher fired")
        fired.assertForOverFulfill = false
        let watcher = try XCTUnwrap(DirectoryWatcher.watch(dir, queue: queue) { fired.fulfill() },
                                    "the directory exists, so a watch must be possible")
        defer { watcher.cancel() }

        // Given to the backend before the write, or the change can land before it is listening
        Thread.sleep(forTimeInterval: 0.2)
        try write("appeared.json")
        wait(for: [fired], timeout: 10)
    }

    func testRewritingAFileWakesTheCallback() throws {
        try write("sidecar.json", "first")
        let fired = expectation(description: "watcher fired")
        fired.assertForOverFulfill = false
        let watcher = try XCTUnwrap(DirectoryWatcher.watch(dir, queue: queue) { fired.fulfill() })
        defer { watcher.cancel() }

        Thread.sleep(forTimeInterval: 0.2)
        // Atomically, the way the feeder replaces its sidecar
        try write("sidecar.json", "second")
        wait(for: [fired], timeout: 10)
    }

    /// A cancelled watch must go quiet. Without this the app would keep refreshing against a
    /// directory it has stopped caring about.
    func testCancellingStopsTheCallbacks() throws {
        let counter = Counter()
        let watcher = try XCTUnwrap(DirectoryWatcher.watch(dir, queue: queue) { counter.bump() })
        Thread.sleep(forTimeInterval: 0.2)
        try write("before.json")

        // Let the first change land, then stop listening
        Thread.sleep(forTimeInterval: 1.0)
        watcher.cancel()
        let afterCancel = counter.value

        try write("after.json")
        Thread.sleep(forTimeInterval: 1.0)
        XCTAssertEqual(counter.value, afterCancel,
                       "a cancelled watch kept reporting changes")
    }

    func testWatchingSomethingThatIsNotThereFailsRatherThanPretending() {
        let missing = dir.appendingPathComponent("no-such-directory")
        XCTAssertNil(DirectoryWatcher.watch(missing, queue: queue) { })
    }
}

/// The callback runs on the watcher's queue, so the count it touches needs a lock
private final class Counter {
    private let lock = NSLock()
    private var count = 0
    func bump() { lock.lock(); count += 1; lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return count }
}
