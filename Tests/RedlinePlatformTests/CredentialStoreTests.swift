// The contract every credential store owes, checked against the file store on every platform
// and against whatever this machine actually offers when it will have us.
import XCTest
@testable import RedlinePlatform
import RedlineCore

/// The shared contract. Written once because three implementations getting subtly different
/// answers for "nothing is stored" is exactly the bug worth preventing.
private func assertStoreRoundTrips(_ store: CredentialStore,
                                   account: String,
                                   file: StaticString = #filePath,
                                   line: UInt = #line) throws {
    try store.removeSecret(for: account)
    XCTAssertNil(try store.secret(for: account),
                 "\(store.name): an empty store must answer nil, not throw", file: file, line: line)

    try store.setSecret("first-secret", for: account)
    XCTAssertEqual(try store.secret(for: account), "first-secret",
                   "\(store.name): did not read back what was written", file: file, line: line)

    // Overwriting matters: a token refresh does this on every renewal
    try store.setSecret("second-secret", for: account)
    XCTAssertEqual(try store.secret(for: account), "second-secret",
                   "\(store.name): overwrite left the old secret behind", file: file, line: line)

    try store.removeSecret(for: account)
    XCTAssertNil(try store.secret(for: account),
                 "\(store.name): removal did not remove", file: file, line: line)

    // Removing something absent is a no-op, because sign-out runs whether or not it needs to
    XCTAssertNoThrow(try store.removeSecret(for: account), file: file, line: line)
}

final class FileCredentialStoreTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-creds-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func store() -> FileCredentialStore {
        FileCredentialStore(service: "redline-tests", directory: dir)
    }

    func testItHonoursTheContract() throws {
        try assertStoreRoundTrips(store(), account: "oauth")
    }

    func testTwoAccountsDoNotCollide() throws {
        let s = store()
        try s.setSecret("a", for: "oauth")
        try s.setSecret("b", for: "other")
        XCTAssertEqual(try s.secret(for: "oauth"), "a")
        XCTAssertEqual(try s.secret(for: "other"), "b")
    }

    /// An account name is ours rather than user input, but a separator in one would still
    /// escape the directory, so it must not be spelled straight into a path.
    func testAnAccountNameCannotEscapeTheDirectory() throws {
        let s = store()
        try s.setSecret("secret", for: "../../escaped")
        let escaped = dir.deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("escaped.secret")
        XCTAssertFalse(FileManager.default.fileExists(atPath: escaped.path),
                       "an account name wrote outside its own directory")
        XCTAssertEqual(try s.secret(for: "../../escaped"), "secret")
    }

    #if !os(Windows)
    /// A secret readable by every account on the machine is not stored, it is published.
    func testTheSecretIsReadableOnlyByItsOwner() throws {
        let s = store()
        try s.setSecret("secret", for: "oauth")
        let path = dir.appendingPathComponent("credentials/redline-tests/oauth.secret").path
        let mode = try FileManager.default.attributesOfItem(atPath: path)[.posixPermissions]
        XCTAssertEqual(mode as? NSNumber, 0o600)
    }
    #endif
}

final class PlatformCredentialStoreTests: XCTestCase {
    /// The real store this machine offers. Skipped rather than failed when it will not have
    /// us: a CI runner has a locked Keychain and a container has no Secret Service, and
    /// neither says anything about the code.
    func testThePlatformStoreHonoursTheSameContract() throws {
        let store = PlatformCredentials.store(service: "redline-tests")
        let account = "contract-\(UUID().uuidString.prefix(8))"
        do {
            try assertStoreRoundTrips(store, account: account)
        } catch {
            try? store.removeSecret(for: account)
            throw XCTSkip("\(store.name) is not usable here: \(error)")
        }
    }

    func testItNamesItself() {
        XCTAssertFalse(PlatformCredentials.store().name.isEmpty,
                       "a diagnostic has to be able to say where the token went")
    }
}
