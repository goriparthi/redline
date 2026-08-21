// The command line tool as a standalone executable.
//
// On macOS the same commands ship inside Redline.app and this exists only so the entry point
// cannot rot. Off macOS it is the whole product until the native shells are written.
import Foundation
import RedlineCore

let arguments = Array(CommandLine.arguments.dropFirst())

if arguments.first == "--version" {
    print("redline \(RedlineVersion.current)")
    exit(0)
}

// No argument means status, matching what the bundled tool does on macOS
let command = arguments.first ?? "status"
guard command.hasPrefix("--") || RedlineCLI.commands.contains(command) else {
    FileHandle.standardError.write(Data("unknown command: \(command)\n\n\(RedlineCLI.usage)\n".utf8))
    exit(2)
}

let result = RedlineCLI.run(arguments, version: RedlineVersion.current)
print(result.text)
exit(result.code)
