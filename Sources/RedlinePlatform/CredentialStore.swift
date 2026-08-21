// Somewhere to keep a secret, on three systems that each have their own idea of where.
//
// This is the reason RedlinePlatform exists rather than everything living in RedlineCore:
// the core is meant to be answerable from files alone, and a credential store is the exact
// opposite of that.
import Foundation
import RedlineCore
#if canImport(Security)
import Security
#endif
#if os(Windows)
import WinSDK
#endif

public enum CredentialError: Error, CustomStringConvertible {
    case backend(String, code: Int32)
    case notWritable(String)

    public var description: String {
        switch self {
        case let .backend(name, code): return "\(name) refused the request (code \(code))"
        case let .notWritable(path):   return "could not write \(path)"
        }
    }
}

/// Read, write and forget one secret per account name.
public protocol CredentialStore {
    /// nil means nothing is stored, which is an answer rather than a failure.
    func secret(for account: String) throws -> String?
    func setSecret(_ secret: String, for account: String) throws
    func removeSecret(for account: String) throws
    /// What a diagnostic should call this, so someone can tell where their token went.
    var name: String { get }
}

public enum PlatformCredentials {
    public static let defaultService = "redline"

    /// The best store this machine offers.
    ///
    /// Linux has no single answer: a desktop has the Secret Service, a server has nothing at
    /// all, and a token that cannot be saved is worse than one saved in a file only its owner
    /// can read. So it degrades rather than refuses, and says which it chose.
    public static func store(service: String = defaultService) -> CredentialStore {
        #if canImport(Security)
        return KeychainCredentialStore(service: service)
        #elseif os(Windows)
        return CredentialManagerStore(service: service)
        #else
        if SecretToolStore.isAvailable { return SecretToolStore(service: service) }
        return FileCredentialStore(service: service)
        #endif
    }
}

// MARK: - A file only its owner can read

/// The fallback, and the one implementation that exists on every platform so the contract
/// itself can be tested without a desktop session.
public struct FileCredentialStore: CredentialStore {
    public let name = "file"
    private let directory: URL

    public init(service: String, directory: URL? = nil) {
        self.directory = (directory ?? AppPaths.data).appendingPathComponent("credentials")
            .appendingPathComponent(service)
    }

    private func url(_ account: String) -> URL {
        // Account names are ours, not user input, but a separator would still escape the
        // directory, so they are encoded rather than trusted.
        let safe = account.addingPercentEncoding(
            withAllowedCharacters: .alphanumerics) ?? account
        return directory.appendingPathComponent("\(safe).secret")
    }

    public func secret(for account: String) throws -> String? {
        guard let data = FileManager.default.contents(atPath: url(account).path) else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    public func setSecret(_ secret: String, for account: String) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let target = url(account)
        guard FileManager.default.createFile(
            atPath: target.path, contents: Data(secret.utf8),
            attributes: [.posixPermissions: 0o600]) else {
            throw CredentialError.notWritable(target.path)
        }
    }

    public func removeSecret(for account: String) throws {
        try? FileManager.default.removeItem(at: url(account))
    }
}

// MARK: - macOS

#if canImport(Security)
/// The login Keychain, matching the item shape TokenStore has always used so an existing
/// sign-in is still found.
public struct KeychainCredentialStore: CredentialStore {
    public let name = "Keychain"
    private let service: String

    public init(service: String) { self.service = service }

    private func query(_ account: String) -> [CFString: Any] {
        [kSecClass: kSecClassGenericPassword,
         kSecAttrService: service,
         kSecAttrAccount: account]
    }

    public func secret(for account: String) throws -> String? {
        var q = query(account)
        q[kSecReturnData] = true
        q[kSecMatchLimit] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data else {
            throw CredentialError.backend(name, code: status)
        }
        return String(data: data, encoding: .utf8)
    }

    public func setSecret(_ secret: String, for account: String) throws {
        let data = Data(secret.utf8)
        var attrs = query(account)
        attrs[kSecAttrAccessible] = kSecAttrAccessibleWhenUnlocked
        attrs[kSecValueData] = data
        let status = SecItemAdd(attrs as CFDictionary, nil)
        if status == errSecSuccess { return }
        // A rebuilt binary loses access to the old item's ACL, so a delete can fail and leave
        // a duplicate behind; update it in place rather than dropping the secret.
        guard status == errSecDuplicateItem else {
            throw CredentialError.backend(name, code: status)
        }
        let updated = SecItemUpdate(query(account) as CFDictionary,
                                    [kSecValueData: data] as CFDictionary)
        guard updated == errSecSuccess else {
            throw CredentialError.backend(name, code: updated)
        }
    }

    public func removeSecret(for account: String) throws {
        let status = SecItemDelete(query(account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw CredentialError.backend(name, code: status)
        }
    }
}
#endif

// MARK: - Windows

#if os(Windows)
/// Windows Credential Manager. Stored as a generic credential under "redline/<account>",
/// which is where a person would go looking for it in Control Panel.
public struct CredentialManagerStore: CredentialStore {
    public let name = "Credential Manager"
    private let service: String

    public init(service: String) { self.service = service }

    private func target(_ account: String) -> String { "\(service)/\(account)" }

    public func secret(for account: String) throws -> String? {
        var pointer: PCREDENTIALW?
        let ok = target(account).withCString(encodedAs: UTF16.self) {
            CredReadW($0, DWORD(CRED_TYPE_GENERIC), 0, &pointer)
        }
        guard ok, let credential = pointer else {
            let code = Int32(bitPattern: GetLastError())
            if code == Int32(ERROR_NOT_FOUND) { return nil }
            throw CredentialError.backend(name, code: code)
        }
        defer { CredFree(credential) }
        let size = Int(credential.pointee.CredentialBlobSize)
        guard size > 0, let blob = credential.pointee.CredentialBlob else { return nil }
        return String(data: Data(bytes: blob, count: size), encoding: .utf8)
    }

    public func setSecret(_ secret: String, for account: String) throws {
        var bytes = Array(secret.utf8)
        let ok: Bool = target(account).withCString(encodedAs: UTF16.self) { targetName in
            account.withCString(encodedAs: UTF16.self) { userName in
                bytes.withUnsafeMutableBufferPointer { buffer in
                    var credential = CREDENTIALW()
                    credential.Type = DWORD(CRED_TYPE_GENERIC)
                    credential.TargetName = UnsafeMutablePointer(mutating: targetName)
                    credential.UserName = UnsafeMutablePointer(mutating: userName)
                    credential.CredentialBlobSize = DWORD(buffer.count)
                    credential.CredentialBlob = buffer.baseAddress
                    // LOCAL_MACHINE rather than SESSION, so a sign-in survives a logout
                    credential.Persist = DWORD(CRED_PERSIST_LOCAL_MACHINE)
                    return CredWriteW(&credential, 0)
                }
            }
        }
        guard ok else {
            throw CredentialError.backend(name, code: Int32(bitPattern: GetLastError()))
        }
    }

    public func removeSecret(for account: String) throws {
        let ok = target(account).withCString(encodedAs: UTF16.self) {
            CredDeleteW($0, DWORD(CRED_TYPE_GENERIC), 0)
        }
        guard ok else {
            let code = Int32(bitPattern: GetLastError())
            if code == Int32(ERROR_NOT_FOUND) { return }
            throw CredentialError.backend(name, code: code)
        }
    }
}
#endif

// MARK: - Linux

#if os(Linux)
/// The Secret Service, driven through secret-tool rather than by linking libsecret, so the
/// package keeps its no-dependency build and a machine without a desktop simply has no
/// secret-tool and falls back.
public struct SecretToolStore: CredentialStore {
    public let name = "Secret Service"
    private let service: String

    public init(service: String) { self.service = service }

    static var isAvailable: Bool { executable != nil }

    private static var executable: URL? {
        for dir in (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":") {
            let candidate = URL(fileURLWithPath: String(dir))
                .appendingPathComponent("secret-tool")
            if FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    @discardableResult
    private func run(_ arguments: [String], input: String? = nil) throws -> (String, Int32) {
        guard let executable = Self.executable else {
            throw CredentialError.backend(name, code: -1)
        }
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        let output = Pipe()
        let stdin = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = stdin
        try process.run()
        if let input { stdin.fileHandleForWriting.write(Data(input.utf8)) }
        try? stdin.fileHandleForWriting.close()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (String(data: data, encoding: .utf8) ?? "", process.terminationStatus)
    }

    public func secret(for account: String) throws -> String? {
        let (text, code) = try run(["lookup", "service", service, "account", account])
        // A miss exits non-zero with nothing on stdout, which is not an error
        guard code == 0, !text.isEmpty else { return nil }
        return text.hasSuffix("\n") ? String(text.dropLast()) : text
    }

    public func setSecret(_ secret: String, for account: String) throws {
        let (_, code) = try run(
            ["store", "--label=RedLine", "service", service, "account", account], input: secret)
        guard code == 0 else { throw CredentialError.backend(name, code: code) }
    }

    public func removeSecret(for account: String) throws {
        try run(["clear", "service", service, "account", account])
    }
}
#endif
