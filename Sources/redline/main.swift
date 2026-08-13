// Entry point: run as a menu bar accessory app (no Dock icon).
// Two flags exist so the Homebrew cask can register and remove the LaunchAgent.
import AppKit

switch CommandLine.arguments.dropFirst().first {
case "--install-launch-agent":
    LaunchAgent.install()
    print("Installed LaunchAgent \(LaunchAgent.label)")
case "--uninstall-launch-agent":
    LaunchAgent.remove()
    print("Removed LaunchAgent \(LaunchAgent.label)")
case "--version":
    let v = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
    print("redline \(v)")
case .some(let arg):
    FileHandle.standardError.write(Data("unknown argument: \(arg)\n".utf8))
    exit(2)
case nil:
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let delegate = AppDelegate()
    app.delegate = delegate
    app.run()
}
