// Menu bar UI: status item title plus a dropdown with per-provider usage and rate limits.
import RedlineCore
import AppKit
import SwiftUI
#if canImport(WidgetKit)
import WidgetKit
#endif

// Login item managed as a plain LaunchAgent plist. Also driven by the Homebrew cask via
// the --install-launch-agent / --uninstall-launch-agent flags.
enum LaunchAgent {
    static let label = "com.goriparthi.redline"
    // Names the log files and the config and data directories, all of which stay lowercase
    static let binName = "redline"
    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func install() {
        let bin = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let logs = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs")
        // Restart after a crash, but never after a clean exit: KeepAlive=true would
        // resurrect the app every time the user chose Quit.
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [bin],
            "RunAtLoad": true,
            "KeepAlive": ["SuccessfulExit": false],
            "StandardOutPath": logs.appendingPathComponent("\(binName).log").path,
            "StandardErrorPath": logs.appendingPathComponent("\(binName).err").path,
        ]
        try? FileManager.default.createDirectory(
            at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        guard let data = try? PropertyListSerialization.data(
            fromPropertyList: plist, format: .xml, options: 0) else { return }
        do { try data.write(to: plistURL) } catch { return }
        // Deliberately not bootstrapped here. This process is already running and owns the
        // status item, so loading the agent now would start a second copy and show two icons.
    }

    // Deleting the plist is enough: at next login nothing loads. Booting the job out here
    // would terminate this very process when launchd is the one running it, so turning the
    // toggle off would look like the app crashing.
    static func remove() {
        try? FileManager.default.removeItem(at: plistURL)
    }

    // True when launchd started this process; launchd stamps the job label into the
    // environment, while a Finder launch gets an "application.…" name instead.
    static var isLaunchdOwned: Bool {
        ProcessInfo.processInfo.environment["XPC_SERVICE_NAME"] == label
    }

    // Unloads the job, terminating its process. Only sane as the very last step of an
    // uninstall, after every other removal has already happened.
    static func bootout() {
        launchctl("bootout")
    }

    private static func launchctl(_ verb: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = [verb, "gui/\(getuid())", plistURL.path]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try? p.run()
        p.waitUntilExit()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var timer: Timer?
    private var config = Config.load()
    private let claudeStore = UsageStore()
    private let codexStore = CodexStore()
    private let ollamaStore = OllamaStore()
    private lazy var oauth = OAuthManager(settings: config.oauth,
                                          useCLIToken: config.useCLIToken)
    private let queue = DispatchQueue(label: "usage-scan", qos: .utility)

    private var lastRefresh: Date?
    private var refreshing = false
    private var today = Agg()
    private var block5h = Agg()
    private var week = Agg()
    private var claudeLimits: [LimitWindow] = []
    private var codexLimits: [LimitWindow] = []
    private var limitsStatus: String?
    private var updateStatus: String?
    private var updateURL: URL?
    private var updateDMG: URL?
    private var updateVersion: String?
    private var updateInFlight = false
    private var claudeLimitsAt: Date?
    private var ollamaSection: Snapshot.Ollama?
    // Recomputed on each refresh so a provider installed later shows up without a restart
    private var availability = ProviderAvailability.detect()
    private var dashboardWindow: NSWindow?
    private var setupWindow: NSWindow?
    private lazy var dashboardModel = DashboardModel()
    private lazy var ollamaService = OllamaService()

    private var allLimits: [LimitWindow] { claudeLimits + codexLimits }

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let mark = NSImage(named: "RedlineTemplate") {
            mark.isTemplate = true
            mark.size = NSSize(width: 18, height: 18)
            statusItem.button?.image = mark
            statusItem.button?.imagePosition = .imageLeading
        }
        statusItem.button?.title = ""
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        let firstRun = Config.isFirstRun()
        scheduleTimer()
        refresh()
        if CommandLine.arguments.contains("--dashboard") { openDashboard(nil) }
        // Ask once what to read, rather than switching every provider on by default
        if firstRun { showSetup() }
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(wokeUp),
            name: NSWorkspace.didWakeNotification, object: nil)
    }

    // An accessory app draws no menu bar, but key equivalents still route through
    // NSApp.mainMenu. Without one, ⌘Q and ⌘W are dead in every window and ⌘V cannot paste
    // into the setup window's client id field.
    private func buildMainMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(
            title: "About RedLine",
            action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)),
            keyEquivalent: ""))
        appMenu.addItem(.separator())
        let q = NSMenuItem(title: "Quit RedLine", action: #selector(quit(_:)), keyEquivalent: "q")
        q.target = self
        appMenu.addItem(q)
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)),
                     keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)),
                       keyEquivalent: "w")
        window.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)),
                       keyEquivalent: "m")
        windowItem.submenu = window
        main.addItem(windowItem)

        NSApp.mainMenu = main
    }

    // Double-clicking the app in Finder previously looked like nothing happened, because a
    // menu bar accessory has no window. Show the dashboard instead.
    func applicationShouldHandleReopen(_ sender: NSApplication,
                                       hasVisibleWindows flag: Bool) -> Bool {
        openDashboard(nil)
        return true
    }

    @objc private func wokeUp() { refresh() }

    private func scheduleTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: config.pollIntervalSeconds,
                                     repeats: true) { [weak self] _ in
            guard let self else { return }
            self.reloadConfig()
            self.refresh()
        }
    }

    private func reloadConfig() {
        let old = config.pollIntervalSeconds
        config = Config.load()
        oauth.update(settings: config.oauth, useCLIToken: config.useCLIToken)
        if config.pollIntervalSeconds != old { scheduleTimer() }
    }

    // MARK: - Actions

    @objc func refreshNow(_ sender: Any?) {
        reloadConfig()
        refresh()
    }

    @objc func editConfig(_ sender: Any?) {
        // Open in TextEdit explicitly; the default .json handler is often Xcode
        let textEdit = URL(fileURLWithPath: "/System/Applications/TextEdit.app")
        NSWorkspace.shared.open([Config.configURL], withApplicationAt: textEdit,
                                configuration: NSWorkspace.OpenConfiguration()) { _, err in
            if err != nil { NSWorkspace.shared.open(Config.configURL) }
        }
    }

    // The shim ships inside the app so every install route has it. Copying it onto the
    // PATH is on request, not at launch, since writing outside the bundle needs a decision.
    @objc func installOllamaShim(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        guard let src = Bundle.main.url(forResource: "ollama-shim", withExtension: "sh") else {
            alert.messageText = "Shim not found in this build"
            alert.informativeText = "Install scripts/ollama-shim.sh from a clone of the "
                + "repository instead."
            alert.runModal()
            return
        }
        let fm = FileManager.default
        let binDir = fm.homeDirectoryForCurrentUser.appendingPathComponent(".local/bin")
        // Installed under the real command's name, so plain `ollama run` is what gets counted
        let dest = binDir.appendingPathComponent("ollama")
        do {
            try fm.createDirectory(at: binDir, withIntermediateDirectories: true)
            // Replace rather than fail, so this doubles as the way to update the shim.
            // Only ever overwrite our own file: a real binary someone placed here is theirs.
            if fm.fileExists(atPath: dest.path) {
                let head = (try? String(contentsOf: dest, encoding: .utf8))?.prefix(300) ?? ""
                guard head.contains("RedLine ollama shim") else {
                    alert.messageText = "A different ollama already lives there"
                    alert.informativeText = "\(dest.path) exists and is not RedLine's shim, "
                        + "so it was left alone. Remove it yourself if you want the shim there."
                    alert.runModal()
                    return
                }
                try fm.removeItem(at: dest)
            }
            try fm.copyItem(at: src, to: dest)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dest.path)
            // The predecessor of the shim; superseded, so clear it out quietly
            try? fm.removeItem(at: binDir.appendingPathComponent("ollama-run.sh"))
        } catch {
            alert.messageText = "Could not install the shim"
            alert.informativeText = "\(dest.path)\n\n\(error.localizedDescription)"
            alert.runModal()
            return
        }
        alert.messageText = "Ollama tracking is set up"
        alert.informativeText = """
            Installed at \(dest.path)

            It passes everything through to the real ollama unchanged and records token \
            counts for plain `ollama run` calls. For it to be found first, ~/.local/bin \
            must come before the real ollama in your PATH; add this to your shell profile \
            if it is not already there:

            export PATH="$HOME/.local/bin:$PATH"
            """
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Show in Finder")
        if alert.runModal() == .alertSecondButtonReturn {
            NSWorkspace.shared.activateFileViewerSelecting([dest])
        }
    }

    @objc func toggleLaunchAtLogin(_ sender: Any?) {
        if LaunchAgent.isInstalled {
            LaunchAgent.remove()
        } else {
            LaunchAgent.install()
        }
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    @objc func signIn(_ sender: Any?) {
        limitsStatus = "Waiting for browser sign-in…"
        oauth.signIn { [weak self] err in
            DispatchQueue.main.async {
                guard let self else { return }
                self.limitsStatus = err
                self.refresh()
            }
        }
    }

    @objc func signOut(_ sender: Any?) {
        oauth.signOut()
        claudeLimits = []
        limitsStatus = nil
        updateTitle()
    }

    @objc func openDashboard(_ sender: Any?) {
        if let w = dashboardWindow {
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            dashboardModel.load(range: dashboardModel.data.range, limits: allLimits)
            ollamaService.refresh()
            return
        }
        let view = DashboardView(model: dashboardModel, ollama: ollamaService,
                                 onReload: { [weak self] range in
            guard let self else { return }
            self.dashboardModel.load(range: range, limits: self.allLimits)
            // Ollama is live state, not derived from transcripts, so refresh it here too or
            // the model list goes stale while the window stays open
            self.ollamaService.refresh()
        }, onFocus: { [weak self] provider in
            guard let self else { return }
            self.dashboardModel.setFocus(provider)
            // Ollama's state is live rather than derived from transcripts, so fetch on demand
            if provider == OllamaStore.provider { self.ollamaService.refresh() }
        })
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 860, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered, defer: false)
        window.title = "RedLine Usage"
        window.contentView = NSHostingView(rootView: view)
        window.isReleasedWhenClosed = false
        window.center()
        window.setFrameAutosaveName("RedlineDashboard")
        // An accessory app keeps no Dock icon, so activate explicitly to take focus
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        dashboardWindow = window
        dashboardModel.load(range: 14, limits: allLimits)
        ollamaService.refresh()
    }

    @objc func showSetup(_ sender: Any? = nil) {
        if let w = setupWindow {
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let detected = ProviderAvailability.detect(
            ollamaReachable: ollamaSection?.reachable ?? false,
            claudeAccount: oauth.isSignedIn || config.oauth.isConfigured)
        let view = FirstRunView(availability: detected,
                                currentProviders: config.providers,
                                useCLIToken: config.useCLIToken,
                                oauthClientId: config.oauth.clientId) {
            [weak self] providers, choice, clientId in
            guard let self else { return }
            if !providers.isEmpty { Config.setProviders(providers) }
            if !Config.write(["useCLIToken": choice == .cliToken]) {
                self.limitsStatus = "Could not write config"
            }
            if choice == .browser { Config.setOAuthClientId(clientId) }
            // Off means off: keeping a signed-in token would leave the percentages showing
            if choice == .off, self.oauth.isSignedIn, !self.oauth.usingCLIToken {
                self.oauth.signOut()
                self.claudeLimits = []
            }
            self.config = Config.load()
            self.oauth.update(settings: self.config.oauth, useCLIToken: self.config.useCLIToken)
            self.setupWindow?.close()
            self.setupWindow = nil
            // The browser flow is the one thing Start cannot finish on its own
            if choice == .browser, !self.oauth.isSignedIn { self.signIn(nil) }
            if choice == .cliToken {
                // Same choice re-asserted still means "try again", not "keep the old failure"
                self.oauth.resetCLIProbe()
                self.verifyCLITokenReadable()
            }
            self.refresh()
        }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 470, height: 460),
                              styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "Set up RedLine"
        window.contentView = NSHostingView(rootView: view)
        window.isReleasedWhenClosed = false
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        setupWindow = window
    }

    // The only way to replace a running menu bar app is for the app to do it itself: quit
    // its own processes, let a helper swap the bundle, and relaunch. Finder cannot, which
    // is the "RedLine.app is in use" error when dragging a new DMG over a live install.
    @objc func installUpdate(_ sender: Any?) {
        guard !updateInFlight, updateDMG != nil, let version = updateVersion else { return }
        let target = Bundle.main.bundleURL
        guard target.pathExtension == "app" else {
            updateStatus = "Development build; update with git pull instead"
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Install RedLine \(version)?"
        alert.informativeText = """
            Downloads the release, verifies it is notarized and signed by this project, \
            then quits, replaces \(target.lastPathComponent) in place, and relaunches.
            """
        alert.addButton(withTitle: "Install and Relaunch")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        beginUpdateInstall()
    }

    // The flow after consent; the update-available dialog enters here directly so the user
    // is not asked twice in a row
    private func beginUpdateInstall() {
        guard !updateInFlight, let dmg = updateDMG, let version = updateVersion else { return }
        let target = Bundle.main.bundleURL
        guard target.pathExtension == "app" else {
            updateStatus = "Development build; update with git pull instead"
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        updateInFlight = true
        Updates.stage(dmg: dmg, replacing: target, status: { [weak self] s in
            DispatchQueue.main.async {
                guard let self else { return }
                self.updateStatus = "\(s) (\(version))"
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }, completion: { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.updateInFlight = false
                switch result {
                case .failed(let message):
                    self.updateStatus = message
                    if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
                case .ready(let swap):
                    self.updateStatus = "Relaunching…"
                    swap()
                    // Same exit path as Quit, so launchd never resurrects the old bundle
                    // out from under the helper
                    if LaunchAgent.isLaunchdOwned { LaunchAgent.bootout() }
                    NSApp.terminate(nil)
                }
            }
        })
    }

    // One-click enable from the menu: persist the choice, probe immediately, and either
    // show data or say exactly what is in the way
    @objc func enableCLIToken(_ sender: Any?) {
        guard Config.write(["useCLIToken": true]) else {
            limitsStatus = "Could not write config"
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        config = Config.load()
        oauth.update(settings: config.oauth, useCLIToken: true)
        oauth.resetCLIProbe()
        verifyCLITokenReadable()
    }

    // The browser route: straight to sign-in when a client id exists, otherwise the setup
    // window, which is where one gets pasted
    @objc func browserSignIn(_ sender: Any?) {
        if oauth.canSignIn { signIn(nil) } else { showSetup(nil) }
    }

    @objc func fixKeychainAccess(_ sender: Any?) {
        oauth.resetCLIProbe()
        verifyCLITokenReadable()
    }

    // Enabling the CLI token either works right now or needs the user to act, and both
    // deserve to be said out loud while they are still looking, not buried in a menu line.
    // The Keychain read can block on the consent prompt, so it stays off the main thread.
    private func verifyCLITokenReadable() {
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            // Warms the same probe the fetch path reuses: one read, one prompt
            let readable = self?.oauth.probeCLIToken() ?? false
            DispatchQueue.main.async {
                guard let self else { return }
                guard !readable else { self.refresh(); return }
                NSApp.activate(ignoringOtherApps: true)
                let alert = NSAlert()
                alert.messageText = "Could not read the Claude CLI's token"
                alert.informativeText = """
                    The percentages stay hidden until RedLine can read the Keychain item \
                    "Claude Code-credentials".

                    When macOS asks, click "Always Allow". Plain Allow grants a single \
                    read, so the prompt returns on every refresh.

                    If you clicked Deny, or macOS never asked: open Keychain Access, \
                    search for "Claude Code-credentials", open Access Control, and add \
                    RedLine.

                    If Claude Code is not signed in, run `claude` once and sign in first.
                    """
                alert.addButton(withTitle: "Open Keychain Access")
                alert.addButton(withTitle: "OK")
                if alert.runModal() == .alertFirstButtonReturn {
                    NSWorkspace.shared.open(URL(fileURLWithPath:
                        "/System/Applications/Utilities/Keychain Access.app"))
                }
            }
        }
    }

    @objc func checkForUpdates(_ sender: Any?) {
        updateStatus = "Checking…"
        if let menu = statusItem.menu { rebuildMenu(menu) }
        Updates.check(currentVersion: Updates.bundleVersion) { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                // The user asked; the answer is a dialog, not a line to hunt for in the menu
                NSApp.activate(ignoringOtherApps: true)
                let alert = NSAlert()
                switch result {
                case .upToDate:
                    self.updateStatus = "Up to date (\(Updates.bundleVersion))"
                    alert.messageText = "You're up to date"
                    alert.informativeText = "RedLine \(Updates.bundleVersion) is the latest release."
                    alert.runModal()
                case .available(let version, let url, let dmg):
                    self.updateStatus = "Update available: \(version)"
                    self.updateURL = url
                    self.updateDMG = dmg
                    self.updateVersion = version
                    alert.messageText = "RedLine \(version) is available"
                    alert.informativeText = "You have \(Updates.bundleVersion)."
                    if dmg != nil {
                        alert.addButton(withTitle: "Install and Relaunch")
                        alert.addButton(withTitle: "Open Release Page")
                        alert.addButton(withTitle: "Later")
                        switch alert.runModal() {
                        case .alertFirstButtonReturn: self.beginUpdateInstall()
                        case .alertSecondButtonReturn: NSWorkspace.shared.open(url)
                        default: break
                        }
                    } else {
                        alert.addButton(withTitle: "Open Release Page")
                        alert.addButton(withTitle: "Later")
                        if alert.runModal() == .alertFirstButtonReturn {
                            NSWorkspace.shared.open(url)
                        }
                    }
                case .failed(let message):
                    self.updateStatus = message
                    alert.messageText = "Update check failed"
                    alert.informativeText = message
                    alert.runModal()
                }
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    @objc func openUpdate(_ sender: Any?) {
        guard let updateURL else { return }
        NSWorkspace.shared.open(updateURL)
    }

    // The same removal scripts/uninstall.sh performs, plus the bundle itself. The app goes to
    // the Trash rather than being deleted outright, so a misclick stays recoverable.
    @objc func uninstall(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        let purge = NSButton(checkboxWithTitle: "Also remove settings, logs and history",
                             target: nil, action: nil)
        let alert = NSAlert()
        alert.messageText = "Uninstall RedLine?"
        alert.informativeText = """
            Moves RedLine to the Trash and removes the login item and its Keychain token. \
            Your Claude, Codex and Ollama files are never touched.
            """
        alert.accessoryView = purge
        alert.addButton(withTitle: "Uninstall")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        LaunchAgent.remove()
        oauth.signOut()
        if purge.state == .on {
            let fm = FileManager.default
            let home = fm.homeDirectoryForCurrentUser
            for url in [Config.configURL.deletingLastPathComponent(),
                        home.appendingPathComponent("Library/Logs/\(LaunchAgent.binName).log"),
                        home.appendingPathComponent("Library/Logs/\(LaunchAgent.binName).err"),
                        home.appendingPathComponent(".local/share/\(LaunchAgent.binName)"),
                        home.appendingPathComponent(".local/bin/ollama-run.sh")] {
                try? fm.removeItem(at: url)
            }
            // The shim only if it is actually ours; a real binary parked there stays
            let shim = home.appendingPathComponent(".local/bin/ollama")
            if let head = try? String(contentsOf: shim, encoding: .utf8).prefix(300),
               head.contains("RedLine ollama shim") {
                try? fm.removeItem(at: shim)
            }
        }

        let bundle = Bundle.main.bundleURL
        // A development build runs straight from .build, where there is no bundle to move
        guard bundle.pathExtension == "app" else { NSApp.terminate(nil); return }
        NSWorkspace.shared.recycle([bundle]) { _, error in
            DispatchQueue.main.async {
                if let error {
                    // macOS can refuse to let an app move its own bundle out of
                    // /Applications. Say so and hand it over, rather than quitting as if
                    // the app were gone.
                    let failed = NSAlert()
                    failed.messageText = "Everything except the app itself was removed"
                    failed.informativeText = """
                        macOS would not let RedLine move its own bundle to the Trash: \
                        \(error.localizedDescription)

                        Drag \(bundle.lastPathComponent) to the Trash to finish.
                        """
                    failed.addButton(withTitle: "Show in Finder")
                    failed.addButton(withTitle: "Quit")
                    if failed.runModal() == .alertFirstButtonReturn {
                        NSWorkspace.shared.activateFileViewerSelecting([bundle])
                    }
                }
                // Last on purpose: when launchd runs this process, bootout terminates it,
                // so everything above must already be finished.
                LaunchAgent.bootout()
                NSApp.terminate(nil)
            }
        }
    }

    @objc func quit(_ sender: Any?) {
        // Booting the job out is the only exit launchd never undoes, whatever KeepAlive
        // policy the plist that started us carried. Plists written before 0.1.0 said
        // KeepAlive=true, which turns a plain terminate into an instant relaunch.
        if LaunchAgent.isLaunchdOwned { LaunchAgent.bootout() }
        NSApp.terminate(nil)
    }

    func menuWillOpen(_ menu: NSMenu) {
        rebuildMenu(menu)
        if let last = lastRefresh, Date().timeIntervalSince(last) < 60 { return }
        refresh()
    }

    // MARK: - Refresh

    private func refresh() {
        refreshLocal()
        refreshLimits()
        refreshOllamaSection()
        refreshAvailability()
    }

    // Polled here rather than in the widget: keeping the extension offline means it needs no
    // network entitlement at all.
    private func refreshAvailability() {
        let reachable = ollamaSection?.reachable ?? false
        let next = ProviderAvailability.detect(
            ollamaReachable: reachable,
            claudeAccount: oauth.isSignedIn || config.oauth.isConfigured)
        guard next != availability else { return }
        availability = next
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    private func refreshOllamaSection() {
        guard config.wants(OllamaStore.provider) else {
            if ollamaSection != nil {
                ollamaSection = nil
                publishSnapshot()
            }
            return
        }
        Task { [weak self] in
            guard let self else { return }
            let section = await self.ollamaService.snapshotSection()
            await MainActor.run {
                self.ollamaSection = section
                self.publishSnapshot()
            }
        }
    }

    private func refreshLocal() {
        guard !refreshing else { return }
        refreshing = true
        let cfg = config
        queue.async { [weak self] in
            guard let self else { return }
            var entries: [Entry] = []
            if cfg.wants(UsageStore.provider) {
                entries += self.claudeStore.scan(lookbackDays: 7)
            }
            var codex: [LimitWindow] = []
            if cfg.wants(CodexStore.provider) {
                let snap = self.codexStore.scan(lookbackDays: 7)
                entries += snap.entries
                codex = snap.limits
            }
            if cfg.wants(OllamaStore.provider) {
                entries += self.ollamaStore.scan(lookbackDays: 7)
            }
            let now = Date()
            let t = aggregate(entries, since: Calendar.current.startOfDay(for: now), config: cfg)
            let b = aggregate(entries, since: now.addingTimeInterval(-5 * 3600), config: cfg)
            let w = aggregate(entries, since: now.addingTimeInterval(-7 * 86400), config: cfg)
            DispatchQueue.main.async {
                self.today = t
                self.block5h = b
                self.week = w
                self.codexLimits = codex
                self.lastRefresh = Date()
                self.refreshing = false
                self.updateTitle()
                self.publishSnapshot()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    // The widget cannot poll or parse transcripts in its time budget, so hand it a snapshot
    private func publishSnapshot() {
        // Same filter the menu applies, so the widget never inherits empty unnamed windows
        let snap = Snapshot(updatedAt: lastRefresh ?? Date(),
                            limits: allLimits.filter { !$0.isUninformative },
                            today: today, week: week, ollama: ollamaSection)
        guard SnapshotStore.writeEverywhere(snap) else { return }
        #if canImport(WidgetKit)
        // Nudge the widget rather than waiting for the system's own reload schedule
        WidgetCenter.shared.reloadTimelines(ofKind: "RedlineWidget")
        #endif
    }

    private func refreshLimits() {
        guard config.wants(UsageStore.provider) else {
            // Switching Claude off has to drop its rows too. Returning early left the last
            // fetched percentages on display, so the menu looked unchanged.
            if !claudeLimits.isEmpty || claudeLimitsAt != nil || limitsStatus != nil {
                claudeLimits = []
                claudeLimitsAt = nil
                limitsStatus = nil
                updateTitle()
                publishSnapshot()
                if let menu = statusItem.menu { rebuildMenu(menu) }
            }
            return
        }
        oauth.fetchLimits { [weak self] limits, err in
            DispatchQueue.main.async {
                guard let self else { return }
                if let limits {
                    self.claudeLimits = limits
                    self.claudeLimitsAt = Date()
                }
                // Losing the token invalidates the cached percentages; keeping them would
                // show a stale "usage left" in the menu bar with no hint it is stale.
                if err != nil && !self.oauth.isSignedIn {
                    self.claudeLimits = []
                    self.claudeLimitsAt = nil
                }
                self.limitsStatus = err
                self.updateTitle()
                self.publishSnapshot()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    // MARK: - UI

    private func updateTitle() {
        guard let button = statusItem.button else { return }
        button.toolTip = nil
        switch config.menuBarDisplay {
        case "limits":
            if let t = limitsTitle() {
                button.attributedTitle = t
                button.toolTip = limitsTooltip()
                return
            }
            // A provider that has no limits by nature reports volume instead. This is not the
            // misleading fallback the rule below guards against: it is only reached when the
            // user has explicitly picked that provider, and it is never a percentage.
            if config.menuBarProvider != Config.autoProvider,
               !config.menuBarProvider.isEmpty,
               menuBarLimits.isEmpty,
               let io = today.providers[canonicalMenuBarProvider]?.io {
                button.title = fmtTokens(io)
                button.toolTip = "\(canonicalMenuBarProvider) · tokens today, no limit reported"
                return
            }
            // Never fall back to tokens+cost here: it reads as a real limit figure and hid
            // an expired sign-in behind a plausible-looking title.
            let signedIn = oauth.isSignedIn
            button.attributedTitle = coloredTitle([
                                (signedIn ? "…" : "Connect", signedIn ? .tertiaryLabelColor
                                                      : contrasted(NSColor(Brand.signal))),
            ])
            button.toolTip = signedIn
                ? "Usage loading…"
                : "Connect Claude to view usage"
        case "cost":
            button.title = fmtCost(today.cost)
        case "tokens":
            button.title = fmtTokens(today.io)
        case "session":
            if let s = worst(in: ["five_hour"]) {
                button.attributedTitle = coloredTitle([                                                       ("\(pct(s))%", limitColor(s.utilization))])
            } else {
                button.title = fmtCost(today.cost)
            }
        default:
            button.title = "\(fmtTokens(today.io)) \(fmtCost(today.cost))"
        }
    }

    // In auto, the binding constraint is whichever provider is closest to its limit. When a
    // single provider is chosen, only that provider's windows drive the readout.
    private var menuBarLimits: [LimitWindow] {
        let choice = config.menuBarProvider
        guard choice != Config.autoProvider else { return allLimits }
        return allLimits.filter { $0.provider.caseInsensitiveCompare(choice) == .orderedSame }
    }

    private func worst(in keys: [String]) -> LimitWindow? {
        menuBarLimits.filter { w in keys.contains(where: { w.key.hasPrefix($0) }) }
            .max(by: { $0.utilization < $1.utilization })
    }

    private func pct(_ w: LimitWindow) -> Int { Int(w.utilization.rounded()) }

    private func limitsTitle() -> NSAttributedString? {
        let session = worst(in: ["five_hour"])
        let week = worst(in: ["seven_day"])
        guard session != nil || week != nil else { return nil }
        var parts: [(String, NSColor?)] = []
        if let s = session {
            parts.append(("\(pct(s))%", limitColor(s.utilization)))
            if let r = fmtResetShort(s.resetsAt) { parts.append((" \(r)", .labelColor)) }
        }
        if session != nil && week != nil { parts.append((" | ", .tertiaryLabelColor)) }
        if let w = week {
            parts.append(("\(pct(w))%", limitColor(w.utilization)))
            if let r = fmtResetShort(w.resetsAt) { parts.append((" \(r)", .labelColor)) }
        }
        return coloredTitle(parts)
    }

    // Config stores whatever case the user typed, so resolve to the canonical spelling
    private var canonicalMenuBarProvider: String {
        Config.knownProviders.first {
            $0.caseInsensitiveCompare(config.menuBarProvider) == .orderedSame
        } ?? config.menuBarProvider
    }

    private func limitsTooltip() -> String {
        if config.menuBarProvider != Config.autoProvider {
            return "\(canonicalMenuBarProvider) · session (5h) | week"
        }
        let names = Set(menuBarLimits.map { $0.provider }).sorted().joined(separator: ", ")
        return names.isEmpty ? "Session (5h) | week"
                             : "Nearest limit across: \(names)"
    }

    @objc func pickMenuBarProvider(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? String,
              Config.setMenuBarProvider(choice) else {
            limitsStatus = "Could not write config"
            return
        }
        config.menuBarProvider = choice
        updateTitle()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    // Compact reset stamp: "3p" or "3:30p" today, "Tue 9a" otherwise
    private func fmtResetShort(_ d: Date?) -> String? {
        guard let d else { return nil }
        let f = DateFormatter()
        f.amSymbol = "a"
        f.pmSymbol = "p"
        let onHour = Calendar.current.component(.minute, from: d) == 0
        if Calendar.current.isDateInToday(d) {
            f.dateFormat = onHour ? "ha" : "h:mma"
        } else {
            f.dateFormat = onHour ? "EEE ha" : "EEE h:mma"
        }
        return f.string(from: d)
    }

    private func coloredTitle(_ parts: [(String, NSColor?)]) -> NSAttributedString {
        let out = NSMutableAttributedString()
        let font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize,
                                                    weight: .semibold)
        for (text, color) in parts {
            var attrs: [NSAttributedString.Key: Any] = [.font: font]
            if let color { attrs[.foregroundColor] = color }
            out.append(NSAttributedString(string: text, attributes: attrs))
        }
        return out
    }

    // Darken in light mode, lighten in dark mode: system tints wash out on the
    // translucent menu bar
    private func contrasted(_ base: NSColor) -> NSColor {
        NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return dark
                ? (base.blended(withFraction: 0.25, of: .white) ?? base)
                : (base.blended(withFraction: 0.35, of: .black) ?? base)
        }
    }

    private func limitColor(_ pct: Double) -> NSColor {
        let status = Brand.status(for: pct, approachingPct: config.limitYellowPct,
                                 atLimitPct: config.limitRedPct)
        return contrasted(NSColor(status.color))
    }

    private func rebuildMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        // Without this, an item with no action is auto-disabled and macOS dims the whole row,
        // which is what made the usage tables hard to read. No action still means inert.
        menu.autoenablesItems = false

        // Drop empty unnamed windows (Claude has emitted internal codenames at 0% with no
        // reset); anything actually being consumed still shows.
        let grouped = Dictionary(grouping: allLimits.filter { !$0.isUninformative },
                                 by: { $0.provider })
        if availability.isEmpty {
            addInfo(menu, "No supported tool found")
            addInfo(menu, "    RedLine reads Claude Code, Codex, or Ollama", secondary: true)
        } else if grouped.isEmpty {
            addInfo(menu, "Rate limits:")
            addInfo(menu, "    none available", secondary: true)
        }
        for provider in grouped.keys.sorted() {
            addInfo(menu, "\(provider) limits:\(staleSuffix(for: provider))")
            for w in LimitParser.sorted(grouped[provider] ?? []) {
                addStatic(menu, coloredTitle([
                    ("    ● ", limitColor(w.utilization)),
                    ("\(w.displayName): \(pct(w))%\(fmtReset(w.resetsAt))",
                     Brandkit.menuPrimary),
                ]))
            }
        }
        if let s = limitsStatus { addInfo(menu, "    \(s)") }
        if oauth.usingCLIToken {
            addInfo(menu, "    Reading limits with the Claude CLI's token", secondary: true)
        }

        // Percentages that are off or broken must say so where the user is looking, with
        // the enable actions one click away. A setting that only lives in another window
        // reads as a silent failure.
        if config.wants(UsageStore.provider), !oauth.isSignedIn {
            if config.useCLIToken {
                addInfo(menu, "    Percentages on, but the CLI token is unreadable")
                let f = NSMenuItem(title: "Fix Keychain Access…",
                                   action: #selector(fixKeychainAccess(_:)), keyEquivalent: "")
                f.target = self
                menu.addItem(f)
            } else {
                addInfo(menu, "    Claude percentages are off", secondary: true)
                let parent = NSMenuItem(title: "Show Claude Percentages",
                                        action: nil, keyEquivalent: "")
                let sub = NSMenu()
                let cli = NSMenuItem(title: "Use the Claude Code CLI's Token",
                                     action: #selector(enableCLIToken(_:)), keyEquivalent: "")
                cli.target = self
                cli.toolTip = "Reads Claude Code's token from your Keychain; macOS asks once"
                sub.addItem(cli)
                let b = NSMenuItem(title: "Sign In with Browser…",
                                   action: #selector(browserSignIn(_:)), keyEquivalent: "")
                b.target = self
                b.toolTip = "For claude.ai users without Claude Code"
                sub.addItem(b)
                parent.submenu = sub
                menu.addItem(parent)
            }
        }
        menu.addItem(.separator())

        addSection(menu, label: "Today", agg: today, detail: true)
        menu.addItem(.separator())
        addSection(menu, label: "Last 5 hours", agg: block5h, detail: false)
        menu.addItem(.separator())
        addSection(menu, label: "Last 7 days", agg: week, detail: true)
        menu.addItem(.separator())

        let updated = lastRefresh.map {
            DateFormatter.localizedString(from: $0, dateStyle: .none, timeStyle: .medium)
        } ?? "never"
        addInfo(menu, "Updated \(updated), polling every \(Int(config.pollIntervalSeconds))s")

        let d = NSMenuItem(title: "Open Usage Dashboard…",
                           action: #selector(openDashboard(_:)), keyEquivalent: "d")
        d.target = self
        menu.addItem(d)

        let r = NSMenuItem(title: "Refresh Now", action: #selector(refreshNow(_:)),
                           keyEquivalent: "r")
        r.target = self
        menu.addItem(r)
        menu.addItem(.separator())

        // Settings, most-reached-for first. Providers are chosen in one place only, the
        // setup window, which also carries the Claude limits decision.
        let setup = NSMenuItem(title: "Providers & Claude Limits…",
                               action: #selector(showSetup(_:)), keyEquivalent: "")
        setup.target = self
        menu.addItem(setup)

        let barItem = NSMenuItem(title: "Menu Bar Shows", action: nil, keyEquivalent: "")
        let barSub = NSMenu()
        for choice in availability.trackChoices {
            let title = choice == Config.autoProvider ? "Nearest Limit (any provider)" : choice
            let item = NSMenuItem(title: title, action: #selector(pickMenuBarProvider(_:)),
                                  keyEquivalent: "")
            item.target = self
            item.representedObject = choice
            item.state = choice.caseInsensitiveCompare(config.menuBarProvider) == .orderedSame
                ? .on : .off
            // A provider that is switched off entirely has nothing to report
            if choice != Config.autoProvider, !config.wants(choice) {
                item.isEnabled = false
                item.toolTip = "Enable \(choice) in Providers & Claude Limits first"
            } else if choice == OllamaStore.provider {
                item.toolTip = "Ollama has no rate limit; shows tokens used today"
            }
            barSub.addItem(item)
        }
        barItem.submenu = barSub
        if availability.hasChoice { menu.addItem(barItem) }

        // Only offered when Ollama is actually installed; a setup step for a missing tool
        // is noise
        if availability.installed.contains(where: {
            $0.caseInsensitiveCompare(OllamaStore.provider) == .orderedSame
        }) {
            let w = NSMenuItem(title: "Set Up Ollama Tracking…",
                               action: #selector(installOllamaShim(_:)), keyEquivalent: "")
            w.target = self
            w.toolTip = "Installs a transparent ollama shim in ~/.local/bin so plain "
                      + "`ollama run` calls are counted"
            menu.addItem(w)
        }

        let e = NSMenuItem(title: "Edit Config…", action: #selector(editConfig(_:)),
                           keyEquivalent: ",")
        e.target = self
        menu.addItem(e)

        let l = NSMenuItem(title: "Launch at Login",
                           action: #selector(toggleLaunchAtLogin(_:)), keyEquivalent: "")
        l.target = self
        l.state = LaunchAgent.isInstalled ? .on : .off
        menu.addItem(l)

        if oauth.isSignedIn {
            let o = NSMenuItem(title: "Sign Out of Claude", action: #selector(signOut(_:)),
                               keyEquivalent: "")
            o.target = self
            menu.addItem(o)
        }
        menu.addItem(.separator())

        if let updateStatus {
            addInfo(menu, "    \(updateStatus)", secondary: true)
        }
        if updateDMG != nil, let updateVersion {
            let i = NSMenuItem(title: "Install Update to \(updateVersion)…",
                               action: #selector(installUpdate(_:)), keyEquivalent: "")
            i.target = self
            i.isEnabled = !updateInFlight
            menu.addItem(i)
        }
        if updateURL != nil {
            let u = NSMenuItem(title: "Open Release Page…", action: #selector(openUpdate(_:)),
                               keyEquivalent: "")
            u.target = self
            menu.addItem(u)
        }
        let c = NSMenuItem(title: "Check for Updates…", action: #selector(checkForUpdates(_:)),
                           keyEquivalent: "")
        c.target = self
        menu.addItem(c)
        menu.addItem(.separator())

        let u = NSMenuItem(title: "Uninstall RedLine…", action: #selector(uninstall(_:)),
                           keyEquivalent: "")
        u.target = self
        menu.addItem(u)
        let q = NSMenuItem(title: "Quit", action: #selector(quit(_:)), keyEquivalent: "q")
        q.target = self
        menu.addItem(q)
    }

    // A fetch that keeps failing must not leave old percentages looking current
    private func staleSuffix(for provider: String) -> String {
        guard provider == UsageStore.provider, let at = claudeLimitsAt else { return "" }
        guard Date().timeIntervalSince(at) > max(config.pollIntervalSeconds * 2, 600) else {
            return ""
        }
        let f = DateFormatter()
        f.dateFormat = "h:mm a"
        return "  (last updated \(f.string(from: at)))"
    }

    private func addSection(_ menu: NSMenu, label: String, agg: Agg, detail: Bool) {
        let partial = agg.hasUnpriced ? "+" : ""
        addInfo(menu, "\(label): \(fmtCost(agg.cost))\(partial) est, \(fmtTokens(agg.io)) in+out")
        addInfo(menu, "    cache read \(fmtTokens(agg.cacheRead)), write \(fmtTokens(agg.cacheWrite))",
                secondary: true)
        guard detail, agg.io > 0 else { return }

        for (provider, usage) in agg.rankedProviders {
            addRow(menu, indent: 1, dot: Brandkit.nsColor(for: provider),
                   name: provider,
                   share: agg.share(ofIO: usage.io),
                   cost: usage.cost, priced: true, io: usage.io,
                   tint: Brandkit.nsColor(for: provider))

            // Models sit under the provider that produced them, never in one flat list
            for (model, m) in usage.rankedModels {
                addRow(menu, indent: 2, dot: nil,
                       name: Sparkline.shortModel(model),
                       share: agg.share(ofIO: m.io),
                       cost: m.cost, priced: m.priced, io: m.io,
                       tint: Brandkit.nsColor(for: provider).withAlphaComponent(0.75))
            }
        }
        if agg.hasUnpriced {
            addInfo(menu, "    + no pricing entry for models shown with a dash",
                    secondary: true)
        }
    }

    // Only the name indents. Every column after it sits at a fixed offset so provider and
    // model bars start in the same place and can be compared at a glance, and all bars share
    // one width so equal shares look equal.
    private static let leadWidth = 30
    private static let barWidth = 10

    private func addRow(_ menu: NSMenu, indent: Int, dot: NSColor?, name: String,
                        share: Double, cost: Double, priced: Bool, io: Int, tint: NSColor) {
        let pad = String(repeating: "    ", count: indent)
        let marker = dot == nil ? "  " : "● "
        let nameWidth = max(4, Self.leadWidth - pad.count - marker.count)
        var parts: [(String, NSColor?)] = []
        parts.append(("\(pad)\(marker)", dot.map { contrasted($0) }))
        parts.append((Sparkline.pad(name, to: nameWidth), Brandkit.menuPrimary))
        parts.append((" \(Sparkline.bar(share: share, width: Self.barWidth))",
                      contrasted(tint)))
        parts.append((" \(Sparkline.percent(share))", Brandkit.menuSecondary))
        // A dash rather than $0.00, which would read as free
        let costText = priced ? fmtCost(cost) : "—"
        parts.append(("  \(Sparkline.pad(costText, to: 9, alignRight: true))",
                      Brandkit.menuPrimary))
        parts.append(("  \(Sparkline.pad(fmtTokens(io), to: 7, alignRight: true))",
                      Brandkit.menuSecondary))
        addStatic(menu, monoTitle(parts))
    }

    private func monoTitle(_ parts: [(String, NSColor?)],
                          mono: Bool = true) -> NSAttributedString {
        let out = NSMutableAttributedString()
        let size = NSFont.smallSystemFontSize
        let font = mono ? NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
                        : NSFont.systemFont(ofSize: NSFont.systemFontSize)
        for (text, color) in parts {
            var attrs: [NSAttributedString.Key: Any] = [.font: font]
            if let color { attrs[.foregroundColor] = color }
            out.append(NSAttributedString(string: text, attributes: attrs))
        }
        return out
    }

    private func addInfo(_ menu: NSMenu, _ title: String, secondary: Bool = false) {
        addStatic(menu, monoTitle([(title, secondary ? Brandkit.menuSecondary
                                                     : Brandkit.menuPrimary)], mono: false))
    }

    /// Adds a row that reads as information, not as a control: full contrast, no hover
    /// highlight, no click target. Enabled menu items highlight even without an action, which
    /// made status lines look actionable.
    private func addStatic(_ menu: NSMenu, _ attributed: NSAttributedString) {
        let item = NSMenuItem(title: "", action: nil, keyEquivalent: "")
        item.view = MenuRowView(attributed: attributed)
        menu.addItem(item)
    }

    private func fmtReset(_ d: Date?) -> String {
        guard let d else { return "" }
        let f = DateFormatter()
        f.dateFormat = Calendar.current.isDateInToday(d) ? "h:mm a" : "EEE h:mm a"
        return ", resets \(f.string(from: d))"
    }
}

// RedlineCore stays free of AppKit, so brand tokens arrive as plain RGB and are bridged here
extension NSColor {
    convenience init(_ c: BrandColor) {
        self.init(srgbRed: c.red, green: c.green, blue: c.blue, alpha: 1)
    }
}
