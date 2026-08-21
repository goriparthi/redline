// The ollama shim stands between a person and their own tool, so the property that matters
// most is that it gets out of the way: anything it does not count must reach the real binary
// byte for byte. Both the bash and PowerShell shims are driven by these tests.
import XCTest
@testable import RedlineCore

final class OllamaShimTests: XCTestCase {
    private var dir: URL!
    private var stub: URL!
    private var seen: URL!

    private static let shim = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        #if os(Windows)
        .appendingPathComponent("scripts/ollama-shim.ps1")
        #else
        .appendingPathComponent("scripts/ollama-shim.sh")
        #endif

    private var shell: (executable: URL, leadingArguments: [String])!

    override func setUpWithError() throws {
        shell = try XCTUnwrap(TestShell.interpreter(forScript: Self.shim),
                              "no interpreter for \(Self.shim.lastPathComponent)")
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("redline-shim-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        seen = dir.appendingPathComponent("seen.txt")

        // A stand-in for the real ollama that records the argv and stdin it was handed
        #if os(Windows)
        stub = dir.appendingPathComponent("fake-ollama.ps1")
        try """
        $argv = $args -join ' '
        $stdin = ''
        if ([Console]::IsInputRedirected) { $stdin = [Console]::In.ReadToEnd() }
        Add-Content -LiteralPath '\(seen.path)' -Value "argv=$argv"
        Add-Content -LiteralPath '\(seen.path)' -Value "stdin=$stdin"
        """.write(to: stub, atomically: true, encoding: .utf8)
        #else
        stub = dir.appendingPathComponent("fake-ollama")
        try """
        #!/bin/bash
        echo "argv=$*" >> '\(seen.path)'
        if [ ! -t 0 ]; then echo "stdin=$(cat)" >> '\(seen.path)'; fi
        """.write(to: stub, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: stub.path)
        #endif
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    /// Runs the shim with the given arguments, optionally piping stdin, and returns what the
    /// stub recorded. `host` points the counted path somewhere, so a test can choose whether
    /// the API answers at all.
    @discardableResult
    private func run(_ arguments: [String], stdin: String? = nil,
                     host: String = "http://127.0.0.1:9") throws -> String {
        let process = Process()
        var env = ProcessInfo.processInfo.environment
        env["REDLINE_OLLAMA_BIN"] = stub.path
        env["REDLINE_DATA_DIR"] = dir.appendingPathComponent("data").path
        env["OLLAMA_HOST"] = host
        env["OLLAMA_TIMEOUT"] = "5"
        process.environment = env

        process.executableURL = shell.executable
        process.arguments = shell.leadingArguments + arguments

        let input = Pipe()
        process.standardInput = input
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        if let stdin { input.fileHandleForWriting.write(Data(stdin.utf8)) }
        try input.fileHandleForWriting.close()
        process.waitUntilExit()
        return (try? String(contentsOf: seen, encoding: .utf8)) ?? ""
    }

    // MARK: - Getting out of the way

    func testAnUnrelatedSubcommandIsPassedThroughUntouched() throws {
        let recorded = try run(["list"])
        XCTAssertTrue(recorded.contains("argv=list"),
                      "the real binary must see the original argv, got: \(recorded)")
    }

    func testPullIsNotCounted() throws {
        let recorded = try run(["pull", "llama3"])
        XCTAssertTrue(recorded.contains("argv=pull llama3"), recorded)
    }

    /// A run carrying flags is not one of the two shapes the shim understands, so it must go
    /// straight through rather than be half-interpreted.
    func testRunWithFlagsIsPassedThrough() throws {
        let recorded = try run(["run", "llama3", "--verbose"])
        XCTAssertTrue(recorded.contains("--verbose"), recorded)
    }

    func testRunWithSeveralPromptArgumentsIsPassedThrough() throws {
        let recorded = try run(["run", "llama3", "one", "two"])
        XCTAssertTrue(recorded.contains("argv=run llama3 one two"), recorded)
    }

    // MARK: - The counted path, when the API cannot be reached

    /// Port 9 is the discard port, so the request always fails. The prompt must still reach
    /// the real binary: an uncounted call is acceptable, a lost one is not.
    func testAFailedAPICallReplaysThePromptThroughTheRealBinary() throws {
        let recorded = try run(["run", "llama3", "hello there"])
        XCTAssertTrue(recorded.contains("run llama3"), recorded)
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: dir.appendingPathComponent("data/ollama.jsonl").path),
            "nothing was counted, so nothing may be logged")
    }

    func testAFailedStdinCallReplaysThePromptThroughTheRealBinary() throws {
        let recorded = try run(["run", "llama3"], stdin: "from stdin\n")
        XCTAssertTrue(recorded.contains("run llama3"), recorded)
        XCTAssertTrue(recorded.contains("from stdin"),
                      "the piped prompt must survive the replay, got: \(recorded)")
    }
}
