// The command line tool as a standalone executable.
//
// On macOS the same commands ship inside Redline.app and this exists only so the entry point
// cannot rot. Off macOS it is the whole product until the native shells are written.
import Foundation
import RedlineCore
import RedlinePlatform

let arguments = Array(CommandLine.arguments.dropFirst())

/// The value after a named flag, for the couple of options this entry point owns.
func stringOption(_ args: [String], _ name: String) -> String? {
    guard let index = args.firstIndex(of: name), index + 1 < args.count else { return nil }
    return args[index + 1]
}
// Held for the life of the process; a released signal source stops delivering
var signalSources: [DispatchSourceSignal] = []

if arguments.first == "--version" {
    print("redline \(RedlineVersion.current)")
    exit(0)
}

// Watching is the standalone tool's own command. On macOS the app is the watcher, so this
// has no counterpart inside the bundle and is deliberately not in RedlineCLI.commands.
if arguments.first == "watch" {
    // One watcher per machine. The Windows app starts one of its own, and someone may also
    // have a startup entry pointing at this, so a second copy has to bow out rather than have
    // two processes ingesting into one database.
    guard let watchLock = SingleInstance.claim(at: AppPaths.data("watch.lock")) else {
        print("another watcher is already running")
        // Zero on purpose: nothing is wrong, there is simply nothing to do. Whoever started
        // this needs to be able to tell that apart from a failure.
        exit(0)
    }

    // A UI has to read the numbers from somewhere, and off macOS nothing else writes them
    let options = WatchLoop.Options(publishSnapshot: true)
    let loop = WatchLoop(options: options) { event in
        switch event {
        case let .started(watching, sweep):
            print("watching \(watching.count) director\(watching.count == 1 ? "y" : "ies"), "
                  + "sweeping every \(Int(sweep))s")
            for url in watching { print("  \(url.path)") }
        case let .ingested(outcome, reason):
            // Silent on the common case, or an idle machine writes a line a minute forever
            guard outcome.added > 0 else { break }
            print("[\(reason)] +\(outcome.added) records, \(outcome.total) held")
        case let .published(snapshot):
            guard !snapshot.limits.isEmpty else { break }
            print("  published \(snapshot.limits.count) limit "
                  + "window\(snapshot.limits.count == 1 ? "" : "s")")
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
    withExtendedLifetime(watchLock) { loop.run() }
    exit(0)
}

// Starting at login is the platform's business, not the engine's, so it lives beside watch
// rather than in RedlineCLI. Every shell can then ask for it the same way.
if arguments.first == "autostart" {
    let service = PlatformAutostart.service()
    let action = arguments.dropFirst().first ?? "status"
    // What it should start: this binary, watching. A GUI shell that wants itself started
    // instead passes --program.
    let program = stringOption(arguments, "--program")
        .map { URL(fileURLWithPath: $0) }
        ?? URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
    let programArguments = stringOption(arguments, "--args").map { [$0] } ?? ["watch"]

    switch action {
    case "status":
        print("\(service.name): \(service.isEnabled ? "on" : "off")")
    case "on":
        do {
            try service.enable(program: program, arguments: programArguments)
            print("\(service.name): on, starting \(program.path) "
                  + programArguments.joined(separator: " "))
        } catch {
            FileHandle.standardError.write(Data("could not enable: \(error)\n".utf8))
            exit(1)
        }
    case "off":
        do {
            try service.disable()
            print("\(service.name): off")
        } catch {
            FileHandle.standardError.write(Data("could not disable: \(error)\n".utf8))
            exit(1)
        }
    default:
        FileHandle.standardError.write(Data(
            "unknown autostart action: \(action)\n\nredline autostart [status|on|off]\n".utf8))
        exit(2)
    }
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
      autostart [on|off]  start RedLine when you sign in; no argument reports
                          whether it is on. --program and --args say what to
                          start, and default to this binary watching.
    """, code: result.code)
}
print(result.text)
exit(result.code)
