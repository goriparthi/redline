import XCTest
@testable import RedlineCore

final class SingleInstanceTests: XCTestCase {
    private var lock: URL!

    override func setUp() {
        super.setUp()
        lock = FileManager.default.temporaryDirectory
            .appendingPathComponent("redline-tests-\(UUID().uuidString)/instance.lock")
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: lock.deletingLastPathComponent())
        super.tearDown()
    }

    func testFirstClaimSucceeds() {
        XCTAssertNotNil(SingleInstance.claim(at: lock))
    }

    func testSecondClaimIsRefusedWhileTheFirstIsHeld() {
        let first = SingleInstance.claim(at: lock)
        XCTAssertNotNil(first)
        XCTAssertNil(SingleInstance.claim(at: lock))
        withExtendedLifetime(first) {}
    }

    // The kernel drops the lock when the holder goes away, so a crash must not lock out the
    // next launch. Releasing the token is the in-process equivalent.
    func testLockIsReleasedWhenTheHolderGoesAway() {
        var first: SingleInstance? = SingleInstance.claim(at: lock)
        XCTAssertNotNil(first)
        first = nil
        XCTAssertNotNil(SingleInstance.claim(at: lock))
    }

    func testMissingDirectoryIsCreated() {
        XCTAssertNotNil(SingleInstance.claim(at: lock))
        XCTAssertTrue(FileManager.default.fileExists(atPath: lock.path))
    }
}
