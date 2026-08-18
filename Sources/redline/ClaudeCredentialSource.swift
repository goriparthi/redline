// Reads the credential Claude Code already stored, in increasing order of cost and intrusion:
// a plain file first, then the Keychain API, then the Apple-signed `security` tool. Every read
// reports why it failed, because "signed out" and "not allowed yet" need opposite responses.
import Foundation
import RedlineCore
import Security

enum ClaudeCredentialSource {
    static let keychainService = "Claude Code-credentials"

    /// Tried in order. The file costs nothing and prompts for nothing, so it goes first even
    /// though most installs keep the credential in the Keychain instead.
    /// Blocks on the Keychain consent prompt, so never call from the main thread.
    static func load(home: URL? = nil,
                     allowSecurityCLI: Bool = true) -> CredentialOutcome {
        let fileOutcome = fromFile(home: home)
        if case .found = fileOutcome { return fileOutcome }

        let keychainOutcome = fromKeychain()
        if case .found = keychainOutcome { return keychainOutcome }

        // The `security` binary sits in the item's apple-tool: partition, so on an item that
        // has already been consented to once it can read where a direct API call would ask
        // again. Worth the process spawn only after the cheap paths have missed.
        if allowSecurityCLI, keychainOutcome != .notFound {
            let cliOutcome = fromSecurityCLI()
            if case .found = cliOutcome { return cliOutcome }
            if cliOutcome == .accessDenied { return .accessDenied }
        }

        // Prefer the more specific answer: a present-but-locked item is not an absent one
        return keychainOutcome == .notFound && fileOutcome == .notFound
            ? .notFound : keychainOutcome
    }

    // MARK: - File

    static func fromFile(home: URL? = nil) -> CredentialOutcome {
        let root = home ?? FileManager.default.homeDirectoryForCurrentUser
        let path = root.appendingPathComponent(".claude/.credentials.json")
        guard let data = try? Data(contentsOf: path) else { return .notFound }
        return parse(data)
    }

    // MARK: - Keychain API

    static func fromKeychain() -> CredentialOutcome {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: keychainService,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data else { return .unreadable }
            return parse(data)
        case errSecItemNotFound:
            return .notFound
        default:
            // errSecInteractionNotAllowed, errSecAuthFailed, errSecUserCanceled and friends all
            // mean "exists, not handed over". Discarding this status is what latched the old
            // probe at nil until the user clicked Reconnect.
            return .accessDenied
        }
    }

    /// Attribute-only read: no kSecReturnData, so the secret is never decrypted and the ACL is
    /// never consulted. Free way to notice that Claude Code rotated the item.
    static func keychainModifiedAt() -> Date? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: keychainService,
            kSecReturnAttributes: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let attrs = item as? [CFString: Any] else { return nil }
        return attrs[kSecAttrModificationDate] as? Date
    }

    // MARK: - security(1)

    static func fromSecurityCLI(account: String? = nil) -> CredentialOutcome {
        var args = ["find-generic-password", "-s", keychainService]
        if let account { args += ["-a", account] }
        args.append("-w")

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        proc.arguments = args
        let out = Pipe()
        let err = Pipe()
        proc.standardOutput = out
        proc.standardError = err
        guard (try? proc.run()) != nil else { return .accessDenied }

        // The consent dialog blocks until answered. A short kill here reads a waiting user as
        // a denial and reports "disconnected" while the prompt is still on screen.
        let deadline = Date().addingTimeInterval(90)
        while proc.isRunning, Date() < deadline { usleep(50_000) }
        if proc.isRunning {
            proc.terminate()
            return .accessDenied
        }

        let stdout = out.fileHandleForReading.readDataToEndOfFile()
        let stderr = String(data: err.fileHandleForReading.readDataToEndOfFile(),
                            encoding: .utf8)?.lowercased() ?? ""
        guard proc.terminationStatus == 0 else {
            // 44 is security(1)'s "item not found"; anything else means it is there but shut
            let missing = proc.terminationStatus == 44
                || stderr.contains("could not be found")
            return missing ? .notFound : .accessDenied
        }
        let text = SecurityCLIOutput.decode(String(data: stdout, encoding: .utf8) ?? "")
        guard let data = text.data(using: .utf8) else { return .unreadable }
        return parse(data)
    }

    // MARK: -

    private static func parse(_ data: Data) -> CredentialOutcome {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return .unreadable }
        guard let credential = CredentialScan.credential(in: json) else { return .unreadable }
        return .found(credential)
    }
}
