import XCTest
@testable import RedlineCore

final class VersionCompareTests: XCTestCase {
    func testStableOrdering() {
        XCTAssertTrue(VersionCompare.isNewer("0.4.0", than: "0.3.3"))
        XCTAssertFalse(VersionCompare.isNewer("0.3.3", than: "0.3.3"))
        XCTAssertFalse(VersionCompare.isNewer("0.3.3", than: "0.10.0"))
    }

    func testPrereleaseOrdersBelowItsRelease() {
        XCTAssertTrue(VersionCompare.isNewer("0.4.0", than: "0.4.0-beta.1"))
        XCTAssertFalse(VersionCompare.isNewer("0.4.0-beta.1", than: "0.4.0"))
        XCTAssertTrue(VersionCompare.isNewer("0.4.0-beta.1", than: "0.3.3"))
        XCTAssertFalse(VersionCompare.isNewer("0.3.3", than: "0.4.0-beta.1"))
    }

    func testNumericPrereleaseIdentifiers() {
        XCTAssertTrue(VersionCompare.isNewer("0.4.0-beta.10", than: "0.4.0-beta.2"))
        XCTAssertFalse(VersionCompare.isNewer("0.4.0-beta.2", than: "0.4.0-beta.10"))
    }

    func testMixedIdentifiers() {
        XCTAssertTrue(VersionCompare.isNewer("0.4.0-beta", than: "0.4.0-1"))
        XCTAssertTrue(VersionCompare.isNewer("0.4.0-beta.1.hotfix", than: "0.4.0-beta.1"))
    }

    func testVPrefixTolerated() {
        XCTAssertTrue(VersionCompare.isNewer("v0.4.0", than: "0.3.3"))
    }

    func testNumericComparisonNotStringComparison() {
        XCTAssertTrue(VersionCompare.isNewer("0.10.0", than: "0.9.0"),
                      "string comparison would call 0.10.0 older than 0.9.0")
        XCTAssertTrue(VersionCompare.isNewer("1.0.0", than: "0.99.9"))
        XCTAssertFalse(VersionCompare.isNewer("0.1.9", than: "0.2.0"))
    }

    func testShorterVersionsPadWithZero() {
        XCTAssertTrue(VersionCompare.isNewer("0.3", than: "0.2.9"))
        XCTAssertFalse(VersionCompare.isNewer("0.2", than: "0.2.0"))
    }
}
