// Starting RedLine when the machine starts, three ways.
//
// Every backend takes an injectable root so a test can prove the behaviour without enlisting
// the developer's own login items, which is the sort of side effect a test suite should
// never have.
import Foundation
import RedlineCore
#if os(Windows)
import WinSDK
#endif

public enum AutostartError: Error, CustomStringConvertible {
    case backend(String, detail: String)

    public var description: String {
        switch self {
        case let .backend(name, detail): return "\(name): \(detail)"
        }
    }
}

public protocol Autostarting {
    /// What a diagnostic should call this, so a person knows where to go and turn it off.
    var name: String { get }
    var isEnabled: Bool { get }
    func enable(program: URL, arguments: [String]) throws
    func disable() throws
}

public enum PlatformAutostart {
    public static let label = "com.goriparthi.redline"

    public static func service(root: URL? = nil) -> Autostarting {
        #if os(macOS)
        return LaunchAgentAutostart(root: root ?? RedlineHome.url)
        #elseif os(Windows)
        return RunKeyAutostart()
        #else
        return SystemdUserAutostart(root: root ?? RedlineHome.url)
        #endif
    }
}

// MARK: - macOS

#if os(macOS)
/// A LaunchAgent plist, in the shape RedLine has always written.
///
/// Writing the file is the whole operation. Bootstrapping the job here would start a second
/// copy while this one already owns the menu bar, and booting it out on disable would kill
/// the very process doing the disabling.
public struct LaunchAgentAutostart: Autostarting {
    public let name = "LaunchAgent"
    private let root: URL

    public init(root: URL) { self.root = root }

    public var plistURL: URL {
        root.appendingPathComponent("Library/LaunchAgents/\(PlatformAutostart.label).plist")
    }

    public var isEnabled: Bool { FileManager.default.fileExists(atPath: plistURL.path) }

    public func enable(program: URL, arguments: [String]) throws {
        let logs = root.appendingPathComponent("Library/Logs")
        // Restart after a crash, but never after a clean exit: KeepAlive=true would
        // resurrect the app every time someone chose Quit.
        let plist: [String: Any] = [
            "Label": PlatformAutostart.label,
            "ProgramArguments": [program.path] + arguments,
            "RunAtLoad": true,
            "KeepAlive": ["SuccessfulExit": false],
            "StandardOutPath": logs.appendingPathComponent("redline.log").path,
            "StandardErrorPath": logs.appendingPathComponent("redline.err").path,
        ]
        try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        do {
            let data = try PropertyListSerialization.data(fromPropertyList: plist,
                                                          format: .xml, options: 0)
            try data.write(to: plistURL)
        } catch {
            throw AutostartError.backend(name, detail: String(describing: error))
        }
    }

    public func disable() throws {
        try? FileManager.default.removeItem(at: plistURL)
    }
}
#endif

// MARK: - Linux

#if os(Linux)
/// A systemd user unit. Written whether or not systemctl is present, because the file is the
/// record of intent; enabling it is best effort on top.
public struct SystemdUserAutostart: Autostarting {
    public let name = "systemd user unit"
    private let root: URL

    public init(root: URL) { self.root = root }

    public var unitURL: URL {
        root.appendingPathComponent(".config/systemd/user/redline.service")
    }

    public var isEnabled: Bool { FileManager.default.fileExists(atPath: unitURL.path) }

    public func enable(program: URL, arguments: [String]) throws {
        let exec = ([program.path] + arguments)
            .map { $0.contains(" ") ? "\"\($0)\"" : $0 }
            .joined(separator: " ")
        // Restart=on-failure rather than always, for the same reason macOS uses
        // SuccessfulExit=false: quitting on purpose must stay quit.
        let unit = """
        [Unit]
        Description=RedLine usage monitor
        After=graphical-session.target

        [Service]
        Type=simple
        ExecStart=\(exec)
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target
        """
        try FileManager.default.createDirectory(at: unitURL.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        do {
            try unit.write(to: unitURL, atomically: true, encoding: .utf8)
        } catch {
            throw AutostartError.backend(name, detail: String(describing: error))
        }
        systemctl(["daemon-reload"])
        systemctl(["enable", "redline.service"])
    }

    public func disable() throws {
        systemctl(["disable", "redline.service"])
        try? FileManager.default.removeItem(at: unitURL)
    }

    /// Best effort on purpose: a container, or a session with no user bus, has no systemctl
    /// to talk to, and the unit file on disk is still the right outcome.
    private func systemctl(_ arguments: [String]) {
        guard root.standardizedFileURL == RedlineHome.url.standardizedFileURL else { return }
        guard let exe = ["/usr/bin/systemctl", "/bin/systemctl"].first(where: {
            FileManager.default.isExecutableFile(atPath: $0)
        }) else { return }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: exe)
        process.arguments = ["--user"] + arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try? process.run()
        process.waitUntilExit()
    }
}
#endif

// MARK: - Windows

#if os(Windows)
/// The per-user Run key, which is where Windows itself lists startup apps and therefore where
/// someone will go to turn this off.
public struct RunKeyAutostart: Autostarting {
    // KEY_READ and KEY_WRITE are macros built from other macros and a bitwise negation, which
    // the Swift importer does not fold, so their documented values are spelled out.
    private static let keyRead: DWORD = 0x2_0019
    private static let keyWrite: DWORD = 0x2_0006

    public let name = "Run key"
    private let subkey: String
    private let valueName: String

    public init(subkey: String = #"Software\Microsoft\Windows\CurrentVersion\Run"#,
                valueName: String = "RedLine") {
        self.subkey = subkey
        self.valueName = valueName
    }

    private func withKey<T>(create: Bool, _ body: (HKEY) throws -> T) throws -> T {
        var key: HKEY?
        let status = subkey.withCString(encodedAs: UTF16.self) { path -> LSTATUS in
            if create {
                return RegCreateKeyExW(HKEY_CURRENT_USER, path, 0, nil,
                                       DWORD(REG_OPTION_NON_VOLATILE),
                                       Self.keyRead | Self.keyWrite, nil, &key, nil)
            }
            return RegOpenKeyExW(HKEY_CURRENT_USER, path, 0, Self.keyRead, &key)
        }
        guard status == ERROR_SUCCESS, let key else {
            throw AutostartError.backend(name, detail: "opening \(subkey) failed (\(status))")
        }
        defer { RegCloseKey(key) }
        return try body(key)
    }

    public var isEnabled: Bool {
        (try? withKey(create: false) { key -> Bool in
            var size: DWORD = 0
            let status = valueName.withCString(encodedAs: UTF16.self) {
                RegQueryValueExW(key, $0, nil, nil, nil, &size)
            }
            return status == ERROR_SUCCESS
        }) ?? false
    }

    public func enable(program: URL, arguments: [String]) throws {
        // Quoted because a path under Program Files has spaces in it, and an unquoted Run
        // value is split on them
        let command = (["\"\(program.path)\""] + arguments).joined(separator: " ")
        var bytes = Array(command.utf16) + [0]
        try withKey(create: true) { key in
            let status = valueName.withCString(encodedAs: UTF16.self) { name -> LSTATUS in
                bytes.withUnsafeBufferPointer { buffer in
                    buffer.baseAddress!.withMemoryRebound(
                        to: UInt8.self, capacity: buffer.count * 2) { raw in
                        RegSetValueExW(key, name, 0, DWORD(REG_SZ), raw,
                                       DWORD(buffer.count * 2))
                    }
                }
            }
            guard status == ERROR_SUCCESS else {
                throw AutostartError.backend(name, detail: "writing the value failed (\(status))")
            }
        }
    }

    public func disable() throws {
        _ = try? withKey(create: false) { key in
            _ = valueName.withCString(encodedAs: UTF16.self) { RegDeleteValueW(key, $0) }
        }
    }
}
#endif
