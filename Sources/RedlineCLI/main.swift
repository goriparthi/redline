// The command line tool as a standalone executable.
//
// On macOS the same commands ship inside Redline.app and this exists only so the entry point
// cannot rot. Off macOS it is the whole product until the native shells are written.
import Foundation
import RedlineCore
import RedlinePlatform

let arguments = Array(CommandLine.arguments.dropFirst())
// Held for the life of the process; a released signal source stops delivering
var signalSources: [DispatchSourceSignal] = []

if arguments.first == "--version" {
    print("redline \(RedlineVersion.current)")
    exit(0)
}

// Watching is the standalone tool's own command. On macOS the app is the watcher, so this
// has no counterpart inside the bundle and is deliberately not in RedlineCLI.commands.
if arguments.first == "watch" {
    let loop = WatchLoop { event in
        switch event {
        case let .started(watching, sweep):
            print("watching \(watching.count) director\(watching.count == 1 ? "y" : "ies"), "
                  + "sweeping every \(Int(sweep))s")
            for url in watching { print("  \(url.path)") }
        case let .ingested(outcome, reason):
            // Silent on the common case, or an idle machine writes a line a minute forever
            guard outcome.added > 0 else { break }
            print("[\(reason)] +\(outcome.added) records, \(outcome.total) held")
        case .historyOff:
            FileHandle.standardError.write(Data(
                "Keep Local History is off, so there is nothing to watch into.\n".utf8))
            exit(Int32(RedlineCLI.Code.noData))
        }
        fflush(stdout)
    }
    // Ctrl-C and a service stop both arrive as signals, and the loop has watchers to release
    for signalNumber in [SIGINT, SIGTERM] {
        signal(signalNumber, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: signalNumber, queue: .global())
        source.setEventHandler { loop.stop() }
        source.resume()
        signalSources.append(source)
    }
    loop.run()
    exit(0)
}

// No argument means status, matching what the bundled tool does on macOS
let command = arguments.first ?? "status"
guard command.hasPrefix("--") || RedlineCLI.commands.contains(command) else {
    FileHandle.standardError.write(Data("unknown command: \(command)\n\n\(RedlineCLI.usage)\n".utf8))
    exit(2)
}

var result = RedlineCLI.run(arguments, version: RedlineVersion.current)
// The shared usage text lives in RedlineCore, which has no watch command to describe,
// so the standalone tool adds its own line rather than keeping a second copy of the text.
if command == "help" || command == "--help" || command == "-h" {
    result = RedlineCLI.Result(
        text: result.text + """


      watch               keep the local history current, until stopped
                          point a startup entry at this off macOS, where
                          there is no app doing it
    """, code: result.code)
}
print(result.text)
exit(result.code)
