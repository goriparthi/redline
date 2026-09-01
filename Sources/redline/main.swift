// Entry point: run as a menu bar accessory app (no Dock icon).
// Two flags exist so the Homebrew cask can register and remove the LaunchAgent.
import AppKit
import RedlineCore

let redlineVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String
    ?? "dev"

switch CommandLine.arguments.dropFirst().first {
case "--install-launch-agent":
    LaunchAgent.install()
    print("Installed LaunchAgent \(LaunchAgent.label)")
case "--uninstall-launch-agent":
    LaunchAgent.remove()
    print("Removed LaunchAgent \(LaunchAgent.label)")
case "--version":
    print("redline \(redlineVersion)")
#if DEBUG
// Renders the UI's states to PNGs for review. Debug builds only: it draws sample data, and a
// release binary must have no path that puts invented figures on screen.
case "--render-previews":
    let dir = CommandLine.arguments.dropFirst(2).first ?? "dist/previews"
    exit(MainActor.assumeIsolated { PreviewRenderer.run(directory: dir) })
#endif
// The bundled CLI. Anything a status bar, a script or an agent might ask is answered from
// the same files the app publishes, so nothing here starts a second copy of the app.
case .some(let arg) where RedlineCLI.commands.contains(arg):
    let result = RedlineCLI.run(Array(CommandLine.arguments.dropFirst()),
                                version: redlineVersion)
    print(result.text)
    exit(result.code)
case .some(let arg) where arg != "--dashboard":
    FileHandle.standardError.write(Data("unknown argument: \(arg)\n".utf8))
    exit(2)
// nil is the normal launch; --dashboard is the same launch with the window already open
default:
    // A copy already owns the menu bar. Show its dashboard rather than exiting silently,
    // since a launch that appears to do nothing reads as a broken app.
    guard let instance = SingleInstance.claim() else {
        DistributedNotificationCenter.default().postNotificationName(
            AppDelegate.showDashboardNotification, object: nil, userInfo: nil,
            deliverImmediately: true)
        FileHandle.standardError.write(Data("redline is already running\n".utf8))
        // Zero on purpose: the LaunchAgent restarts only after an unsuccessful exit, so a
        // duplicate that exits cleanly is not respawned into a loop.
        exit(0)
    }
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let delegate = AppDelegate()
    app.delegate = delegate
    withExtendedLifetime(instance) { app.run() }
}
