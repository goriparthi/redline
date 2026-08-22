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
/// One JSON object on stdout. Machine-readable outcomes go to stdout even when they report a
/// refusal, so a shell reading this has one stream to read and never has to parse prose.
func emitJSON(_ object: [String: Any]) {
    let data = try? JSONSerialization.data(withJSONObject: object,
                                           options: [.prettyPrinted, .sortedKeys])
    print(String(data: data ?? Data(), encoding: .utf8) ?? "{}")
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

// Settings, so a shell never has to learn this schema for itself. The engine's own validation
// decides what is legal; whatever it would refuse to load is refused here too.
if arguments.first == "config" {
    let rest = Array(arguments.dropFirst()).filter { $0 != "--json" }
    let asJSON = arguments.contains("--json")

    // No key: everything, with what a control needs to render itself
    if rest.isEmpty {
        if asJSON {
            emitJSON(["settings": ConfigEditor.catalog()])
        } else {
            let width = ConfigEditor.settings.map(\.key.count).max() ?? 0
            for (setting, value) in ConfigEditor.current() {
                print("\(setting.key.padding(toLength: width, withPad: " ", startingAt: 0))  "
                      + "\(value)")
            }
            print("\nredline config <key> <value> to change one; "
                  + "redline config <key> to read one")
        }
        exit(0)
    }

    // One key: read it
    if rest.count == 1 {
        guard let setting = ConfigEditor.setting(rest[0]),
              let value = ConfigEditor.value(of: rest[0]) else {
            if asJSON {
                emitJSON(ConfigEditor.json(for: .unknownKey(rest[0])))
            } else {
                FileHandle.standardError.write(Data("no such setting: \(rest[0])\n".utf8))
            }
            exit(2)
        }
        if asJSON {
            emitJSON(ConfigEditor.readJSON(key: setting.key, value: value))
        } else {
            print(value)
        }
        exit(0)
    }

    // A change reports what happened rather than whether it exited zero: "already that" and
    // "refused" are different answers, and a shell has to be able to tell them apart. The
    // shape of that answer is ConfigEditor's, so it is asserted in both languages.
    let outcome = ConfigEditor.set(rest[0], to: rest[1...].joined(separator: " "))
    if asJSON {
        emitJSON(ConfigEditor.json(for: outcome))
    }
    switch outcome {
    case let .changed(key, from, to):
        if !asJSON { print("\(key): \(from) -> \(to)") }
    case let .unchanged(key, value):
        if !asJSON { print("\(key): already \(value)") }
    case let .rejected(key, reason):
        if !asJSON {
            FileHandle.standardError.write(Data("\(key) needs \(reason)\n".utf8))
        }
        exit(2)
    case let .unknownKey(key):
        if !asJSON {
            FileHandle.standardError.write(Data("no such setting: \(key)\n".utf8))
        }
        exit(2)
    case let .failed(why):
        if !asJSON {
            FileHandle.standardError.write(Data("\(why)\n".utf8))
        }
        exit(1)
    }
    exit(0)
}

// Wiring the Claude usage feed. Without it there are no live limits at all off macOS, and
// there is no menu there to offer the same thing.
if arguments.first == "setup" {
    let asJSON = arguments.contains("--json")
    let action = Array(arguments.dropFirst()).prefix { !$0.hasPrefix("--") }.first ?? "status"
    let key = ShellToggle.usageFeedKey

    switch action {
    case "status":
        let wanted = StatuslineSetup.isWanted()
        let wired = StatuslineSetup.isInstalled()
        if asJSON {
            emitJSON(ShellToggle.status(key: key, on: wired, extras: [
                "script": StatuslineSetup.scriptURL().path,
                "settings": StatuslineSetup.settingsURL().path,
                // The script can be there without the settings pointing at it, which is
                // neither on nor a clean off, and a shell has to be able to say so
                "detail": wired ? "" : wanted ? "script present but not wired" : "",
            ]))
        } else {
            print("claude usage feed: \(wired ? "on" : wanted ? "script present but not wired" : "off")")
            print("  script:   \(StatuslineSetup.scriptURL().path)")
            print("  settings: \(StatuslineSetup.settingsURL().path)")
        }
        exit(wired ? 0 : Int32(RedlineCLI.Code.indeterminate))
    case "claude", "on":
        switch StatuslineSetup.install() {
        case let .installed(script, chained):
            if asJSON {
                emitJSON(ShellToggle.changed(key: key, on: true, extras: [
                    "script": script.path,
                    "chained": chained ?? "",
                    "detail": "Start a new Claude Code session for it to take effect.",
                ]))
            } else {
                print("claude usage feed: on")
                print("  script: \(script.path)")
                if let chained { print("  your existing statusline still runs: \(chained)") }
                print("\nStart a new Claude Code session for it to take effect.")
            }
        case let .alreadyInstalled(script):
            if asJSON {
                emitJSON(ShellToggle.unchanged(key: key, on: true,
                                               extras: ["script": script.path]))
            } else {
                print("claude usage feed: already on (\(script.path))")
            }
        case let .failed(why):
            if asJSON {
                emitJSON(ShellToggle.failed(key: key, message: why))
            } else {
                FileHandle.standardError.write(Data("\(why)\n".utf8))
            }
            exit(1)
        default: break
        }
    case "off":
        switch StatuslineSetup.uninstall() {
        case .removed:
            if asJSON {
                emitJSON(ShellToggle.changed(key: key, on: false))
            } else {
                print("claude usage feed: off")
            }
        case .notInstalled:
            if asJSON {
                emitJSON(ShellToggle.unchanged(key: key, on: false))
            } else {
                print("claude usage feed: was not on")
            }
        case let .failed(why):
            if asJSON {
                emitJSON(ShellToggle.failed(key: key, message: why))
            } else {
                FileHandle.standardError.write(Data("\(why)\n".utf8))
            }
            exit(1)
        default: break
        }
    default:
        FileHandle.standardError.write(Data(
            "unknown setup action: \(action)\n\nredline setup [status|claude|off]\n".utf8))
        exit(2)
    }
    exit(0)
}

// Starting at login is the platform's business, not the engine's, so it lives beside watch
// rather than in RedlineCLI. Every shell can then ask for it the same way.
if arguments.first == "autostart" {
    let service = PlatformAutostart.service()
    let asJSON = arguments.contains("--json")
    // The action comes first; everything after it may be a flag
    let action = Array(arguments.dropFirst()).prefix { !$0.hasPrefix("--") }.first ?? "status"
    // What it should start: this binary, watching. A GUI shell that wants itself started
    // instead passes --program, and --args "" for a program that takes none.
    let program = stringOption(arguments, "--program")
        .map { URL(fileURLWithPath: $0) }
        ?? URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
    let programArguments = stringOption(arguments, "--args")
        .map { $0.isEmpty ? [] : [$0] } ?? ["watch"]
    let key = ShellToggle.autostartKey

    switch action {
    case "status":
        let on = service.isEnabled
        if asJSON {
            emitJSON(ShellToggle.status(key: key, on: on, extras: ["name": service.name]))
        } else {
            print("\(service.name): \(on ? "on" : "off")")
        }
    case "on":
        do {
            let was = service.isEnabled
            try service.enable(program: program, arguments: programArguments)
            let detail = ([program.path] + programArguments).joined(separator: " ")
            if asJSON {
                let extras = ["name": service.name, "detail": detail]
                emitJSON(was ? ShellToggle.unchanged(key: key, on: true, extras: extras)
                             : ShellToggle.changed(key: key, on: true, extras: extras))
            } else {
                print("\(service.name): on, starting \(detail)")
            }
        } catch {
            if asJSON {
                emitJSON(ShellToggle.failed(key: key, message: "could not enable: \(error)"))
            } else {
                FileHandle.standardError.write(Data("could not enable: \(error)\n".utf8))
            }
            exit(1)
        }
    case "off":
        do {
            let was = service.isEnabled
            try service.disable()
            if asJSON {
                let extras = ["name": service.name]
                emitJSON(was ? ShellToggle.changed(key: key, on: false, extras: extras)
                             : ShellToggle.unchanged(key: key, on: false, extras: extras))
            } else {
                print("\(service.name): off")
            }
        } catch {
            if asJSON {
                emitJSON(ShellToggle.failed(key: key, message: "could not disable: \(error)"))
            } else {
                FileHandle.standardError.write(Data("could not disable: \(error)\n".utf8))
            }
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
                          start, and default to this binary watching. --json
                          reports the state rather than a sentence.
      config [key value]  read or change a setting. No argument lists them all;
                          --json adds what each one accepts, and reports what a
                          change did rather than only exiting non-zero.
      setup claude        wire Claude Code's statusline to report your limits,
                          carrying forward any statusline you already have.
                          "setup" alone reports, "setup off" undoes it, and
                          --json reports the state rather than a sentence.
    """, code: result.code)
}
print(result.text)
exit(result.code)
