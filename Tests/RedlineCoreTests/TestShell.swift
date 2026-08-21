// Finding the interpreter that runs the shipped scripts. Shared by the two suites that drive
// them, because both hit the same two Windows traps: isExecutableFile answers false there, and
// an XCTSkip thrown from inside a helper is reported as a failure rather than a skip.
import XCTest

enum TestShell {
    /// Looks up an executable on PATH. Uses plain existence rather than isExecutableFile,
    /// which reports false for a perfectly runnable .exe on Windows.
    static func onPath(_ name: String) -> URL? {
        let raw = ProcessInfo.processInfo.environment["PATH"] ?? ""
        #if os(Windows)
        let separator: Character = ";"
        #else
        let separator: Character = ":"
        #endif
        for dir in raw.split(separator: separator) where !dir.isEmpty {
            let candidate = URL(fileURLWithPath: String(dir)).appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    /// How to run one of the scripts in scripts/, or nil when this machine cannot.
    /// Resolve it in setUp and skip there, never mid-test.
    static func interpreter(forScript script: URL) -> (executable: URL, leadingArguments: [String])? {
        #if os(Windows)
        let known = [
            #"C:\Program Files\PowerShell\7\pwsh.exe"#,
            #"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"#,
        ].map { URL(fileURLWithPath: $0) }
        let candidates = [onPath("pwsh.exe"), onPath("powershell.exe")].compactMap { $0 } + known
        guard let exe = candidates.first(where: {
            FileManager.default.fileExists(atPath: $0.path)
        }) else { return nil }
        return (exe, ["-NoProfile", "-File", script.path])
        #else
        return (URL(fileURLWithPath: "/bin/bash"), [script.path])
        #endif
    }
}
