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
        RedlineHome.url
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func install() {
        let bin = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let logs = RedlineHome.url
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

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate {
    // Posted by a duplicate launch that found this copy already holding the instance lock,
    // so the launch the user asked for still puts something on screen.
    static let showDashboardNotification =
        Notification.Name("com.goriparthi.redline.showDashboard")

    private var statusItem: NSStatusItem!
    private var timer: Timer?
    private var config = Config.load()
    private let claudeStore = UsageStore()
    private let codexStore = CodexStore()
    private let ollamaStore = OllamaStore()
    private lazy var oauth = OAuthManager(settings: config.oauth,
                                          useCLIToken: config.useCLIToken)
    private let queue = DispatchQueue(label: "usage-scan", qos: .utility)
    // Its own queue: a fleet read behind a full transcript ingest would answer minutes late
    private let fleetQueue = DispatchQueue(label: "fleet-scan", qos: .utility)

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
    private var updateTimer: Timer?
    private var claudeService: ServiceStatus.Report?
    private var codexService: ServiceStatus.Report?
    private var serviceStatusAt: Date?
    private var claudeLimitsAt: Date?
    /// Which source produced the current Claude windows. Shown as a quiet provenance line
    /// under the section, because three different sources can feed the same number and a
    /// reading whose origin cannot be named is a reading that cannot be trusted or fixed.
    enum ClaudeLimitsSource { case feed, external, signIn, cliToken }
    private var claudeLimitsSource: ClaudeLimitsSource?
    /// Watches the feed sidecar so the title tracks Claude Code in near real time. The poll
    /// remains for everything that genuinely needs a scan; the feed no longer waits on it.
    private var feedWatcher: DispatchSourceFileSystemObject?
    private var feedWatchDebounce: DispatchWorkItem?
    private var feedSeenMtime: Date?
    /// Rebuilding the menu while it is open replaces the items under the cursor and AppKit
    /// collapses any open submenu, which the live feed watcher turned from a rare race into
    /// a once-a-second certainty. While open, rebuilds are deferred to menuDidClose.
    private var menuIsOpen = false
    private var menuNeedsRebuild = false
    private var ollamaSection: Snapshot.Ollama?
    /// Daily rollups and limit samples that outlive the transcripts they came from
    private let warehouse = Warehouse()
    /// When entries were last aged out, so the pass runs about daily rather than per poll
    private var lastPrune: Date?
    private lazy var alertCenter = AlertCenter()
    private lazy var findingsService = FindingsService()
    private var findingsReport: FindingsReport?
    /// Recent limit readings, kept in memory so pace can be recomputed on every publish
    /// without touching the history file from the main thread.
    private var limitSamples: [LimitSample] = []
    private var samplesLoadedAt: Date?
    private var paces: [Pace] = []
    /// The windows last handed to the recorder, so an unchanged reading does not queue
    /// another pass over the history file
    private var recordedWindows: [LimitWindow] = []
    /// Which Claude Code sessions are running on this Mac, and which are blocked on the user
    private let fleetStore = ClaudeFleetStore()
    private var fleet = FleetSnapshot()
    /// Session records are rewritten in place on every status change, so a watcher gives
    /// push semantics with no subprocess. The sweep is only a backstop for missed events.
    private var fleetWatcher: DispatchSourceFileSystemObject?
    /// One per live record. A status change rewrites the file in place, which never touches
    /// the directory, so the directory watcher alone would only see sessions come and go.
    private var fleetRecordWatchers: [String: DispatchSourceFileSystemObject] = [:]
    private var fleetWatchDebounce: DispatchWorkItem?
    private var fleetTimer: Timer?
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
        applyMenuBarIcon()
        statusItem.button?.title = ""
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        let firstRun = Config.isFirstRun()
        // The dashboard's "Check now" button: past the throttle, straight to the feeds
        dashboardModel.onSetupClaudeTracking = { [weak self] in
            self?.installStatuslineFeed(nil)
        }
        dashboardModel.onStatusRefresh = { [weak self] in
            guard let self else { return }
            self.serviceStatusAt = nil
            self.refreshServiceStatus()
        }
        findingsService.onUpdate = { [weak self] report in
            guard let self else { return }
            self.findingsReport = report
            self.dashboardModel.data.findings = report
            self.dashboardModel.data.findingsScanning = false
            if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
        }
        dashboardModel.onRescanFindings = { [weak self] in
            guard let self else { return }
            // Flipped before the scan starts, and left on if one was already running, so the
            // click always changes something on screen. A rescan that finished inside the
            // same minute used to be indistinguishable from a dead button.
            self.dashboardModel.data.findingsScanning = true
            if !self.findingsService.refresh(config: self.config),
               !self.findingsService.isRunning {
                self.dashboardModel.data.findingsScanning = false
            }
        }
        scheduleTimer()
        scheduleUpdateTimer()
        watchFeedDirectory()
        watchFleetDirectory()
        refresh()
        // Deliberately after the first refresh and off the critical path: a findings pass
        // reads more of each transcript than the usage scan does, and nothing waits on it.
        loadStoredSamples()
        if CommandLine.arguments.contains("--dashboard") { openDashboard(nil) }
        // Ask once what to read, rather than switching every provider on by default
        if firstRun { showSetup() }
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(wokeUp),
            name: NSWorkspace.didWakeNotification, object: nil)
        DistributedNotificationCenter.default().addObserver(
            self, selector: #selector(openDashboard(_:)),
            name: AppDelegate.showDashboardNotification, object: nil)
    }

    // Shown next to the Apple logo while a window has the app in .regular mode; in
    // accessory mode it is invisible but still routes ⌘Q/⌘W/⌘V key equivalents.
    private func buildMainMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu(title: "RedLine")
        appMenu.addItem(NSMenuItem(
            title: "About RedLine",
            action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)),
            keyEquivalent: ""))
        appMenu.addItem(.separator())
        // ⌘Q from a window closes it and leaves the menu bar app running; a full
        // quit stays on ⌥⌘Q here and on Quit in the status item menu.
        let close = NSMenuItem(title: "Close Dashboard", action: #selector(closeWindows(_:)),
                               keyEquivalent: "q")
        close.target = self
        appMenu.addItem(close)
        let q = NSMenuItem(title: "Quit RedLine", action: #selector(quit(_:)), keyEquivalent: "q")
        q.keyEquivalentModifierMask = [.option, .command]
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

    // The icon is a preference: with it off, the readout is just the numbers
    private func applyMenuBarIcon() {
        guard config.showMenuIcon, let mark = NSImage(named: "RedlineTemplate") else {
            statusItem.button?.image = nil
            return
        }
        mark.isTemplate = true
        mark.size = NSSize(width: 18, height: 18)
        statusItem.button?.image = mark
        statusItem.button?.imagePosition = .imageLeading
    }

    // MARK: - Service status

    // Statuspage feeds for the hosted providers; Ollama is probed locally and its cloud
    // publishes no status feed to read. Only fetched when the user switched it on.
    private func refreshServiceStatus() {
        guard config.statusChecks else { return }
        if let last = serviceStatusAt, Date().timeIntervalSince(last) < 900 { return }
        serviceStatusAt = Date()
        ServiceStatus.fetch(ServiceStatus.claudeURL) { [weak self] report in
            DispatchQueue.main.async {
                guard let self else { return }
                self.claudeService = report
                self.publishSnapshot()
                self.dashboardModel.data.services = self.snapshotServices()
                self.dashboardModel.data.servicesCheckedAt = Date()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
        ServiceStatus.fetch(ServiceStatus.codexURL) { [weak self] report in
            DispatchQueue.main.async {
                guard let self else { return }
                self.codexService = report
                self.publishSnapshot()
                self.dashboardModel.data.services = self.snapshotServices()
                self.dashboardModel.data.servicesCheckedAt = Date()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    private func snapshotServices() -> [Snapshot.Service] {
        var services: [Snapshot.Service] = []
        if let c = claudeService {
            services.append(.init(provider: UsageStore.provider, indicator: c.indicator,
                                  description: c.description))
        }
        if let x = codexService {
            services.append(.init(provider: CodexStore.provider, indicator: x.indicator,
                                  description: x.description))
        }
        // Ollama is always present when read: the probe is local, so reporting it needs
        // no network opt-in, and it must come from the live section, never a default
        if config.wants(OllamaStore.provider), let o = ollamaSection {
            services.append(.init(provider: OllamaStore.provider,
                                  indicator: o.reachable ? "local" : "local-down",
                                  description: "checked directly, no network leaves this Mac"))
        }
        return services
    }

    /// The provider's health as the indicator the shared glyph vocabulary understands, plus
    /// the words to print beside it. nil when nothing has been checked and nothing is claimed.
    private func serviceMark(for provider: String) -> (indicator: String, phrase: String)? {
        if provider == OllamaStore.provider {
            guard let reachable = ollamaSection?.reachable else { return nil }
            return reachable ? ("local", "local, running")
                             : ("local-down", "local, not reachable")
        }
        guard config.statusChecks else { return nil }
        let report = provider == UsageStore.provider ? claudeService
                   : provider == CodexStore.provider ? codexService : nil
        // Honest interim state: the fetch is in flight, and silence would read as broken
        guard let report else { return (ServiceGlyph.checking, "checking status…") }
        return (report.indicator, report.phrase)
    }

    /// An SF Symbol as inline text, so a menu row can carry the same glyph the dashboard
    /// draws. Menu rows are attributed strings, and an attachment is how an image gets in.
    private func symbolRun(_ name: String, color: NSColor, size: CGFloat) -> NSAttributedString {
        let config = NSImage.SymbolConfiguration(pointSize: size, weight: .semibold)
            .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
        guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(config) else {
            // A symbol this OS does not know must not leave a blank gap where health goes
            return monoTitle([("●", color)], mono: false)
        }
        let attachment = NSTextAttachment()
        attachment.image = image
        // Sits the glyph on the text's optical centre rather than its baseline
        attachment.bounds = CGRect(x: 0, y: -2, width: image.size.width, height: image.size.height)
        return NSAttributedString(attachment: attachment)
    }

    /// Alerts are opt-in, and turning them on is when macOS is asked for permission. Asking
    /// at launch, before the app has anything to say, is how an app gets refused once and
    /// forever.
    @objc func toggleAlerts(_ sender: Any?) {
        let next = !config.alerts
        guard Config.write(["alerts": next]) else { return }
        config.alerts = next
        if next { alertCenter.requestAuthorization() }
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    @objc func toggleHistory(_ sender: Any?) {
        let next = !config.recordHistory
        guard Config.write(["recordHistory": next]) else { return }
        config.recordHistory = next
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    /// Same permission path as the alerts: turning it on is the moment macOS is asked, and
    /// a cue is never posted before then.
    @objc func toggleCues(_ sender: Any?) {
        let next = !config.mindfulCues
        guard Config.write(["mindfulCues": next]) else { return }
        config.mindfulCues = next
        if next { alertCenter.requestAuthorization() }
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    @objc func toggleSidecar(_ sender: Any?) {
        let next = !config.publishSidecar
        guard Config.write(["publishSidecar": next]) else { return }
        config.publishSidecar = next
        // Off means gone, not merely no longer updated: a file left behind would be read
        // by whatever was pointed at it, forever, as if it were current.
        publishSidecar()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    @objc func toggleStatusChecks(_ sender: Any?) {
        guard Config.write(["statusChecks": !config.statusChecks]) else { return }
        config = Config.load()
        serviceStatusAt = nil
        claudeService = nil
        codexService = nil
        // Both directions must reach every surface immediately: turning it off used to
        // leave stale status rows on the dashboard and widgets, which read as the toggle
        // doing nothing at all
        dashboardModel.data.services = snapshotServices()
                dashboardModel.data.servicesCheckedAt = Date()
        publishSnapshot()
        refreshServiceStatus()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    // Full Disk Access is the grant macOS actually remembers. The per-app data prompts can
    // recur (per translocated launch, and per target app); FDA is granted once in System
    // Settings and covers every transcript folder durably. Detection is a probe of a
    // TCC-gated path: readable means FDA is on.
    private var hasFullDiskAccess: Bool {
        let home = RedlineHome.url
        let probe = home.appendingPathComponent("Library/Safari")
        return (try? FileManager.default.contentsOfDirectory(atPath: probe.path)) != nil
    }

    @objc func grantFullDiskAccess(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Stop the repeated permission prompts"
        alert.informativeText = """
            macOS asks before RedLine reads other apps' files, and that consent does not \
            always stick. Full Disk Access is the one grant macOS remembers: give it once \
            in System Settings and the prompts stop.

            It is a broad permission. RedLine still reads only what SECURITY.md lists: \
            transcript files, its own config, and nothing else.

            In System Settings, add RedLine under Privacy & Security > Full Disk Access, \
            then relaunch it.
            """
        alert.addButton(withTitle: "Open System Settings")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn,
           let url = URL(string:
               "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - Menu bar style toggles

    @objc func toggleMenuIcon(_ sender: Any?) {
        setStyle(["showMenuIcon": !config.showMenuIcon])
        applyMenuBarIcon()
    }

    @objc func toggleResetTimes(_ sender: Any?) {
        setStyle(["showResetTimes": !config.showResetTimes])
    }

    @objc func pickLimitWindows(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? String else { return }
        setStyle(["limitWindows": choice])
    }

    /// Switching channels rechecks straight away, so the choice answers with the build it
    /// found rather than with silence until tomorrow's poll.
    @objc func pickUpdateChannel(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? String,
              choice != config.updateChannel else { return }
        guard Config.write(["updateChannel": choice]) else {
            updateStatus = "Could not write config"
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        config = Config.load()
        checkForUpdates(nil)
    }

    private func setStyle(_ values: [String: Any]) {
        guard Config.write(values) else {
            limitsStatus = "Could not write config"
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        config = Config.load()
        updateTitle()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

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
        let oldAuto = config.autoCheckUpdates
        config = Config.load()
        oauth.update(settings: config.oauth, useCLIToken: config.useCLIToken)
        if config.pollIntervalSeconds != old { scheduleTimer() }
        if config.autoCheckUpdates != oldAuto { scheduleUpdateTimer() }
        applyMenuBarIcon()
    }

    // MARK: - Actions

    @objc func refreshNow(_ sender: Any?) {
        reloadConfig()
        // On demand means now: the status throttle yields to an explicit refresh
        serviceStatusAt = nil
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

    // The percentages without a credential: Claude Code already reports its own rate limits to
    // whatever statusline command is configured, so this points that at a wrapper which files
    // them where RedLine can read them.
    @objc func installStatuslineFeed(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        switch StatuslineInstaller.install() {
        case .failed(let message):
            alert.messageText = "Could not set up the usage feed"
            alert.informativeText = message
            alert.runModal()
        case .alreadyInstalled(let script):
            alert.messageText = "The usage feed is already set up"
            alert.informativeText = "\(script.path)\n\nStart or continue a Claude Code session "
                + "and the percentages appear at the next refresh."
            alert.runModal()
        case .installed(let script, let chained):
            alert.messageText = "Claude usage feed is set up"
            var text = """
                Installed at \(script.path) and pointed to by statusLine in \
                ~/.claude/settings.json.

                Claude Code passes its rate-limit windows to that command, so RedLine reads \
                them from disk. No token, no Keychain, and no request to Anthropic.

                The figures update while Claude Code is running and carry their own timestamp \
                between sessions.
                """
            if let chained {
                text += "\n\nYour existing statusline is kept and still draws the line:\n"
                    + chained
            }
            alert.informativeText = text
            alert.addButton(withTitle: "OK")
            alert.addButton(withTitle: "Show in Finder")
            if alert.runModal() == .alertSecondButtonReturn {
                NSWorkspace.shared.activateFileViewerSelecting([script])
            }
            refresh()
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
        claudeLimitsAt = nil
        claudeLimitsSource = nil
        limitsStatus = nil
        updateTitle()
        // The feed, if installed, repopulates the percentages on the next poll
        refresh()
    }

    /// nil hands the window back to the OS setting. Setting the window's own appearance is
    /// what makes the change land at once: it re-resolves every dynamic token immediately,
    /// where the SwiftUI modifier waited for the window to change state.
    private func applyDashboardTheme(_ theme: String, to window: NSWindow) {
        switch theme {
        case "light": window.appearance = NSAppearance(named: .aqua)
        case "dark":  window.appearance = NSAppearance(named: .darkAqua)
        default:      window.appearance = nil
        }
        window.contentView?.needsDisplay = true
        window.displayIfNeeded()
    }

    /// A visible window promotes the app to .regular so its menu bar and Dock icon show;
    /// windowWillClose demotes it back to a bare status item.
    private func becomeRegularApp() {
        NSApp.setActivationPolicy(.regular)
        // Deferred: activating in the same runloop turn as the policy flip can leave the
        // previous app's menus in the menu bar.
        DispatchQueue.main.async { NSApp.activate(ignoringOtherApps: true) }
    }

    @objc func closeWindows(_ sender: Any?) {
        for w in [dashboardWindow, setupWindow].compactMap({ $0 }) where w.isVisible {
            w.close()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow,
              closing == dashboardWindow || closing == setupWindow else { return }
        let stillOpen = [dashboardWindow, setupWindow].compactMap { $0 }
            .contains { $0 != closing && $0.isVisible }
        if !stillOpen { NSApp.setActivationPolicy(.accessory) }
    }

    @objc func openDashboard(_ sender: Any?) {
        // The findings panel is the one thing here that is not derived from the poll, so
        // opening the window is a good moment to make sure it has run at least once.
        if findingsService.refreshIfDue(config: config) {
            dashboardModel.data.findingsScanning = true
        }
        dashboardModel.data.paces = paces
        if let w = dashboardWindow {
            becomeRegularApp()
            w.makeKeyAndOrderFront(nil)
            dashboardModel.load(range: dashboardModel.data.range, limits: allLimits)
            dashboardModel.data.claudeLimitsAsOf = claudeLimitsAt
            ollamaService.refresh()
            return
        }
        let view = DashboardView(model: dashboardModel, ollama: ollamaService,
                                 onReload: { [weak self] range in
            guard let self else { return }
            self.dashboardModel.load(range: range, limits: self.allLimits)
            self.dashboardModel.data.claudeLimitsAsOf = self.claudeLimitsAt
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
        applyDashboardTheme(dashboardModel.data.theme, to: window)
        dashboardModel.onThemeChange = { [weak self, weak window] theme in
            guard let window else { return }
            self?.applyDashboardTheme(theme, to: window)
        }
        window.delegate = self
        becomeRegularApp()
        window.makeKeyAndOrderFront(nil)
        dashboardWindow = window
        dashboardModel.load(range: 14, limits: allLimits)
        dashboardModel.data.claudeLimitsAsOf = claudeLimitsAt
        ollamaService.refresh()
    }

    @objc func showSetup(_ sender: Any? = nil) {
        if let w = setupWindow {
            becomeRegularApp()
            w.makeKeyAndOrderFront(nil)
            return
        }
        let detected = ProviderAvailability.detect(
            ollamaReachable: ollamaSection?.reachable ?? false,
            claudeAccount: oauth.isSignedIn || config.oauth.isConfigured)
        let view = FirstRunView(availability: detected,
                                currentProviders: config.providers,
                                useCLIToken: config.useCLIToken,
                                oauthClientId: config.oauth.clientId,
                                feedInstalled: StatuslineInstaller.isInstalled(),
                                signedIn: oauth.isSignedIn) {
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
            // The feed installs quietly here: Start is the consent, and the percentages
            // appearing at the next poll is the confirmation. Failures still speak up.
            if choice == .feed, !StatuslineInstaller.isInstalled() {
                if case .failed(let message) = StatuslineInstaller.install() {
                    self.limitsStatus = "Usage feed: \(message)"
                }
            }
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
        window.delegate = self
        becomeRegularApp()
        window.makeKeyAndOrderFront(nil)
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
    /// The menu checkbox: on enables the borrow (and verifies the read), off disables it.
    /// enableCLIToken stays separate because first-run and the checkbox share the enable path.
    @objc func toggleCLIToken(_ sender: Any?) {
        if config.useCLIToken {
            guard Config.write(["useCLIToken": false]) else {
                limitsStatus = "Could not write config"
                if let menu = statusItem.menu { rebuildMenu(menu) }
                return
            }
            config = Config.load()
            oauth.update(settings: config.oauth, useCLIToken: false)
            refresh()
        } else {
            enableCLIToken(sender)
        }
    }

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

    // Once a day, on by default: a security fix nobody hears about is not a fix. The poll
    // stays silent unless there is news, and the menu switches it off.
    private func scheduleUpdateTimer() {
        updateTimer?.invalidate()
        updateTimer = nil
        guard config.autoCheckUpdates else { return }
        updateTimer = Timer.scheduledTimer(withTimeInterval: 24 * 3600,
                                           repeats: true) { [weak self] _ in
            self?.runUpdateCheck(interactive: false)
        }
        // One check now, so a fresh launch does not wait a day for the first look
        runUpdateCheck(interactive: false)
    }

    @objc func toggleAutoUpdates(_ sender: Any?) {
        guard Config.write(["autoCheckUpdates": !config.autoCheckUpdates]) else { return }
        config = Config.load()
        scheduleUpdateTimer()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    @objc func checkForUpdates(_ sender: Any?) {
        updateStatus = "Checking…"
        if let menu = statusItem.menu { rebuildMenu(menu) }
        runUpdateCheck(interactive: true)
    }

    // interactive: the user asked, so every outcome is a dialog. Background: only an
    // available update earns a popup; "no news" must never interrupt anyone.
    private func runUpdateCheck(interactive: Bool) {
        Updates.check(currentVersion: Updates.bundleVersion,
                      channel: config.updateChannel) { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                if case .available = result {} else if !interactive {
                    if case .upToDate = result {
                        self.updateStatus = "Up to date (\(Updates.bundleVersion))"
                    }
                    if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
                    return
                }
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
            // Before the data directory goes, since the usage feed script lives inside it and
            // ~/.claude/settings.json still points at it. Removing the script first would
            // break the statusline, including whatever was chained behind ours.
            StatuslineInstaller.uninstall()
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
        menuIsOpen = true
        if let last = lastRefresh, Date().timeIntervalSince(last) < 60 { return }
        refresh()
    }

    func menuDidClose(_ menu: NSMenu) {
        menuIsOpen = false
        if menuNeedsRebuild {
            menuNeedsRebuild = false
            rebuildMenu(menu)
        }
    }

    // MARK: - Refresh

    /// Claude Code rewrites the feed sidecar on every statusline draw, so waiting for the
    /// next poll leaves the title up to five minutes behind a file that already holds the
    /// answer. This watches the app's own data directory and re-reads limits when the
    /// sidecar's mtime moves. The directory is watched rather than the file because the
    /// feeder replaces the file atomically, which would orphan a file-level watcher; the
    /// mtime check is what keeps our own snapshot writes into the same directory from
    /// turning this into a refresh loop.
    private func watchFeedDirectory() {
        let dir = StatuslineFeed.defaultPath().deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let fd = open(dir.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: .write, queue: .main)
        source.setEventHandler { [weak self] in
            guard let self else { return }
            // Coalesce the burst of writes a statusline redraw produces into one read
            self.feedWatchDebounce?.cancel()
            let work = DispatchWorkItem { [weak self] in
                guard let self else { return }
                let mtime = (try? FileManager.default.attributesOfItem(
                    atPath: StatuslineFeed.defaultPath().path))?[.modificationDate] as? Date
                guard let mtime, mtime != self.feedSeenMtime else { return }
                self.feedSeenMtime = mtime
                self.refreshLimits()
            }
            self.feedWatchDebounce = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: work)
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        feedWatcher = source
    }

    // MARK: - Agent fleet

    /// Claude Code rewrites a session's record in place on every status change, so watching
    /// the registry directory turns "which agent is blocked" into a push signal rather than
    /// a poll. The sweep beside it is a backstop: a killed session writes nothing at all, so
    /// its leftover record has to be reaped on a timer.
    private func watchFleetDirectory() {
        guard config.agentFleet else { return }
        if fleetTimer == nil {
            let t = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
                self?.refreshFleet()
            }
            t.tolerance = 5
            fleetTimer = t
        }
        guard fleetWatcher == nil else { return }
        // Never created here. This feature is read only against ~/.claude, and a missing
        // directory only means Claude Code has not run yet; the sweep retries the watch.
        let fd = open(ClaudeFleetStore.defaultRoot.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .delete, .rename], queue: .main)
        source.setEventHandler { [weak self] in self?.scheduleFleetRead() }
        source.setCancelHandler { close(fd) }
        source.resume()
        fleetWatcher = source
    }

    /// Follows the current records, so a session that starts is watched and one that exits
    /// stops being. Cheap: a handful of descriptors that turn over only as sessions do.
    private func syncFleetRecordWatchers() {
        let wanted = Set(fleet.sessions.compactMap { $0.recordPath })
        for (path, source) in fleetRecordWatchers where !wanted.contains(path) {
            source.cancel()
            fleetRecordWatchers[path] = nil
        }
        for path in wanted where fleetRecordWatchers[path] == nil {
            let fd = open(path, O_EVTONLY)
            guard fd >= 0 else { continue }
            // A rewrite in place is .write; an atomic replace arrives as .rename or .delete,
            // and either way the answer is to read the directory again
            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: fd, eventMask: [.write, .extend, .delete, .rename],
                queue: .main)
            source.setEventHandler { [weak self] in self?.scheduleFleetRead() }
            source.setCancelHandler { close(fd) }
            source.resume()
            fleetRecordWatchers[path] = source
        }
    }

    /// A fleet of ten writes ten records for one user-visible change, so the burst is
    /// collapsed into a single read.
    private func scheduleFleetRead() {
        fleetWatchDebounce?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.refreshFleet() }
        fleetWatchDebounce = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: work)
    }

    private func stopWatchingFleet() {
        fleetWatcher?.cancel()
        fleetWatcher = nil
        fleetRecordWatchers.values.forEach { $0.cancel() }
        fleetRecordWatchers.removeAll()
        fleetWatchDebounce?.cancel()
        fleetWatchDebounce = nil
        fleetTimer?.invalidate()
        fleetTimer = nil
    }

    /// Off the main thread because it is a directory walk plus a kernel lookup per record,
    /// and the watcher can ask for it several times a second while a fleet is working.
    private func refreshFleet() {
        guard config.agentFleet else { return }
        // Claude Code may have started since launch, when the directory did not exist
        if fleetWatcher == nil { watchFleetDirectory() }
        fleetQueue.async { [weak self] in
            guard let self else { return }
            let snap = self.fleetStore.scan()
            DispatchQueue.main.async {
                guard snap != self.fleet else { return }
                self.fleet = snap
                self.syncFleetRecordWatchers()
                // The badge is the whole point, so it cannot wait for the next poll
                self.updateTitle()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    /// One row per session, waiting first. Sessions are what the agents are; the sections
    /// above are what they cost, which is why this sits between the limits and the volume.
    ///
    /// Local sessions only. Cloud sessions and sessions on other Macs have no public API,
    /// and Codex publishes no live registry at all, so neither belongs here; see the note on
    /// ClaudeFleetStore before trying to widen this.
    private func addFleet(_ menu: NSMenu) {
        guard config.agentFleet, !fleet.isEmpty else { return }
        let now = Date()
        addInfo(menu, "Agents:")
        for s in fleet.sessions {
            let item = NSMenuItem(title: "", action: nil, keyEquivalent: "")
            item.attributedTitle = fleetRowTitle(s, now: now)
            item.toolTip = s.cwd
            item.submenu = fleetRowMenu(s)
            menu.addItem(item)
        }
        menu.addItem(.separator())
    }

    private func fleetRowTitle(_ s: FleetSession, now: Date) -> NSAttributedString {
        let tone = fleetTone(s.state)
        var parts: [(String, NSColor?)] = [("    ● ", contrasted(Brandkit.nsTone(tone)))]
        parts.append((s.label, Brandkit.menuPrimary))
        // The folder earns its place only when it is not already the name
        if s.folder != s.label { parts.append(("  \(s.folder)", Brandkit.menuSecondary)) }
        parts.append(("  \(fleetStatusPhrase(s, now: now))",
                      s.state == .waiting ? contrasted(Brandkit.nsTone(.warning))
                                          : Brandkit.menuSecondary))
        return monoTitle(parts, mono: false)
    }

    /// "waiting 14m, input needed". The status string is echoed verbatim when it is one this
    /// build has never seen, so an upstream addition reads as itself rather than as nothing.
    private func fleetStatusPhrase(_ s: FleetSession, now: Date) -> String {
        var phrase = s.status ?? "unknown"
        if let seconds = s.timeInStatus(now: now) {
            phrase += " \(Pace.short(seconds))"
        }
        if s.state == .waiting, let waitingFor = s.waitingFor, !waitingFor.isEmpty {
            phrase += ", \(waitingFor)"
        }
        return phrase
    }

    private func fleetTone(_ state: FleetState) -> ServiceGlyph.Tone {
        switch state {
        case .waiting: return .warning
        case .busy:    return .healthy
        case .idle:    return .unknown
        case .unknown: return .unknown
        }
    }

    private func fleetRowMenu(_ s: FleetSession) -> NSMenu {
        let m = NSMenu()
        // Same reason as the parent menu: an item with no action is dimmed otherwise
        m.autoenablesItems = false
        // Resolved up front so an unfocusable session offers the copy instead of a dead row,
        // and so the wording promises a tab only where one can actually be reached
        let owner = TerminalFocus.owner(of: s.pid)
        if let owner {
            let app = owner.localizedName ?? "Terminal"
            let toTab = TerminalFocus.canFocusTab(pid: s.pid)
            let f = NSMenuItem(title: toTab ? "Go to This Session in \(app)"
                                            : "Focus \(app)",
                               action: #selector(focusFleetSession(_:)), keyEquivalent: "")
            f.target = self
            f.representedObject = NSNumber(value: s.pid)
            f.toolTip = toTab
                ? "Selects the tab or split pane this session is running in, matched on its "
                    + "terminal device. macOS asks once for permission to ask \(app)."
                : "Brings \(app) forward. It publishes no way to say which tab a session is "
                    + "in, so the tab is yours to find."
            m.addItem(f)
        }
        let c = NSMenuItem(title: "Copy Folder Path", action: #selector(copyFleetPath(_:)),
                           keyEquivalent: "")
        c.target = self
        c.representedObject = s.cwd
        c.toolTip = s.cwd
        m.addItem(c)
        if let url = s.claudeURL {
            let o = NSMenuItem(title: "Open in claude.ai", action: #selector(openFleetURL(_:)),
                               keyEquivalent: "")
            o.target = self
            o.representedObject = url
            o.toolTip = "The same session on the web. The only link between the local and "
                      + "cloud views that Claude Code hands out."
            m.addItem(o)
        }
        m.addItem(.separator())
        var detail = "PID \(s.pid)"
        if let v = s.version { detail += " · Claude Code \(v)" }
        if let e = s.entrypoint { detail += " · \(e)" }
        addInfo(m, detail, secondary: true)
        addInfo(m, s.cwd, secondary: true)
        return m
    }

    @objc func focusFleetSession(_ sender: NSMenuItem) {
        guard let pid = (sender.representedObject as? NSNumber)?.int32Value else { return }
        TerminalFocus.focus(pid: pid) { [weak self] result in
            // A session that exited between the menu opening and the click has nothing to
            // raise, and the row that offered it is already wrong
            if result == .failed { self?.refreshFleet() }
        }
    }

    @objc func copyFleetPath(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(path, forType: .string)
    }

    @objc func openFleetURL(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        NSWorkspace.shared.open(url)
    }

    @objc func toggleAgentFleet(_ sender: Any?) {
        let next = !config.agentFleet
        guard Config.write(["agentFleet": next]) else { return }
        config.agentFleet = next
        if next {
            watchFleetDirectory()
            refreshFleet()
        } else {
            stopWatchingFleet()
            fleet = FleetSnapshot()
            updateTitle()
        }
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    private func refresh() {
        // Cheap wiring check first, so a clobbered settings.json costs at most one poll of
        // stale percentages rather than going quietly dark until someone notices.
        StatuslineInstaller.repairIfNeeded()
        refreshLocal()
        refreshLimits()
        refreshOllamaSection()
        refreshFleet()
        refreshAvailability()
        refreshServiceStatus()
        findingsService.refreshIfDue(config: config)
        pruneHistoryIfDue(now: Date())
    }

    /// Pace needs yesterday's readings, and they live in a file. Read once at launch, off
    /// the main thread, so the first projection does not wait for the second poll.
    private func loadStoredSamples() {
        guard config.recordHistory else { return }
        let now = Date()
        queue.async { [weak self] in
            guard let self else { return }
            let samples = self.warehouse.limitSamples(since: now.addingTimeInterval(-86400))
            DispatchQueue.main.async {
                self.limitSamples = samples
                self.samplesLoadedAt = now
                self.updatePaces()
            }
        }
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
                // The dashboard renders these; stale defaults here painted "not reachable"
                // while ollama was answering prompts in the next window
                self.dashboardModel.data.services = self.snapshotServices()
                self.dashboardModel.data.servicesCheckedAt = Date()
                self.dashboardModel.data.ollamaReachableHint = section?.reachable ?? false
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    private func refreshLocal() {
        guard !refreshing else { return }
        refreshing = true
        let cfg = config
        queue.async { [weak self] in
            guard let self else { return }
            let now = Date()
            var entries: [Entry] = []
            var codex: [LimitWindow] = []
            if cfg.recordHistory {
                // The durable path. Each transcript is read from where the last pass
                // stopped, so a quiet poll costs a directory walk rather than a parse of
                // every byte anyone has ever written, and the questions are then asked of
                // the store instead of of the files.
                if cfg.wants(UsageStore.provider) {
                    self.claudeStore.ingest(into: self.warehouse, now: now)
                }
                if cfg.wants(CodexStore.provider) {
                    codex = self.codexStore.ingest(into: self.warehouse, now: now).limits
                }
                if cfg.wants(OllamaStore.provider) {
                    self.ollamaStore.ingest(into: self.warehouse, now: now)
                }
                self.warehouse.rollupPending(config: cfg)
                entries = self.warehouse.entries(since: now.addingTimeInterval(-7 * 86400))
            } else {
                // Keeping no history means there is no store to ask, so the whole window is
                // parsed on every poll. That is the cost of the setting, not a fallback.
                if cfg.wants(UsageStore.provider) {
                    entries += self.claudeStore.scan(lookbackDays: 7)
                }
                if cfg.wants(CodexStore.provider) {
                    let snap = self.codexStore.scan(lookbackDays: 7)
                    entries += snap.entries
                    codex = snap.limits
                }
                if cfg.wants(OllamaStore.provider) {
                    entries += self.ollamaStore.scan(lookbackDays: 7)
                }
            }
            self.evaluateCadence(entries: entries, config: cfg, now: now)
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
        // Everything that reacts to a new reading hangs off this one call, so a limit that
        // arrives by any of the four routes is paced, recorded, alerted and published the
        // same way rather than each path remembering to do it.
        updatePaces()
        recordLimitsIfChanged()
        evaluateAlerts()
        publishSidecar()
        let services = snapshotServices()
        // Same filter the menu applies, so the widget never inherits empty unnamed windows
        let snap = Snapshot(updatedAt: lastRefresh ?? Date(),
                            limits: allLimits.filter { !$0.isUninformative },
                            today: today, week: week, ollama: ollamaSection,
                            services: services.isEmpty ? nil : services,
                            claudeLimitsAsOf: claudeLimitsAt)
        guard SnapshotStore.writeEverywhere(snap) else { return }
        #if canImport(WidgetKit)
        // Nudge the widget rather than waiting for the system's own reload schedule
        WidgetCenter.shared.reloadTimelines(ofKind: "RedlineWidget")
        #endif
    }

    /// Burn rate and time to limit for every window that can support the claim.
    private func updatePaces() {
        paces = PaceEstimator.paces(for: allLimits.filter { !$0.isUninformative },
                                    samples: limitSamples)
        dashboardModel.data.paces = paces
    }

    /// Appends readings to the history file and refreshes the in-memory samples, both off
    /// the main thread. Skipped entirely when the windows have not moved.
    private func recordLimitsIfChanged() {
        guard config.recordHistory else { return }
        let windows = allLimits.filter { !$0.isUninformative }
        guard !windows.isEmpty else { return }
        let now = Date()
        let unchanged = windows == recordedWindows
        let sampleAge = samplesLoadedAt.map { now.timeIntervalSince($0) } ?? .greatestFiniteMagnitude
        guard !unchanged || sampleAge > 300 else { return }
        recordedWindows = windows
        samplesLoadedAt = now
        queue.async { [weak self] in
            guard let self else { return }
            self.warehouse.recordLimits(windows, at: now)
            let samples = self.warehouse.limitSamples(since: now.addingTimeInterval(-86400))
            DispatchQueue.main.async { self.limitSamples = samples }
        }
    }

    /// Cues about how the work is spread out. Off unless asked for, and evaluated on the
    /// scan thread because a streak needs more days than the menu does.
    ///
    /// Called with whatever the poll already had in hand; when history is kept, a wider
    /// window is read instead, because a seven day streak cannot be seen in seven days of
    /// entries once the oldest one falls off the edge.
    private func evaluateCadence(entries: [Entry], config cfg: Config, now: Date) {
        guard cfg.mindfulCues else { return }
        var window = entries
        if cfg.recordHistory {
            let days = Double(max(cfg.streakDays + 2, 9))
            window = warehouse.entries(since: now.addingTimeInterval(-days * 86400))
        }
        let cues = alertCenter.evaluateCadence(entries: window, config: cfg, now: now)
        guard !cues.isEmpty else { return }
        DispatchQueue.main.async { self.dashboardModel.data.cues = cues }
    }

    /// Ages entries out once a day. The rollups they were folded into are kept forever, so
    /// this trims the grain and never the history.
    private func pruneHistoryIfDue(now: Date) {
        guard config.recordHistory else { return }
        if let last = lastPrune, now.timeIntervalSince(last) < 86400 { return }
        lastPrune = now
        queue.async { [weak self] in self?.warehouse.pruneEntries(now: now) }
    }

    /// A reading nobody can vouch for is not news, so staleness is decided per window and
    /// handed to the alerting rules rather than assumed.
    private func evaluateAlerts() {
        let stale = claudeLimitsAreStale
        alertCenter.evaluate(windows: allLimits.filter { !$0.isUninformative },
                             paces: paces, config: config,
                             isStale: { window in
                                 window.provider == UsageStore.provider && stale
                             })
    }

    /// Publishes the windows in the shape other local tools already read. Local file, no
    /// network; removed when the setting is off so a stale file cannot outlive the choice.
    private func publishSidecar() {
        guard config.publishSidecar else {
            Sidecar.remove()
            return
        }
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String
            ?? "dev"
        Sidecar.publish(windows: allLimits.filter { !$0.isUninformative },
                        updatedAt: lastRefresh ?? Date(),
                        producer: "redline/\(version)",
                        today: Sidecar.Totals(io: today.io, cost: today.cost,
                                              priced: !today.hasUnpriced),
                        week: Sidecar.Totals(io: week.io, cost: week.cost,
                                             priced: !week.hasUnpriced),
                        limitsAsOf: claudeLimitsAt)
    }

    private func refreshLimits() {
        guard config.wants(UsageStore.provider) else {
            // Switching Claude off has to drop its rows too. Returning early left the last
            // fetched percentages on display, so the menu looked unchanged.
            if !claudeLimits.isEmpty || claudeLimitsAt != nil || limitsStatus != nil {
                claudeLimits = []
                claudeLimitsAt = nil
                claudeLimitsSource = nil
                limitsStatus = nil
                updateTitle()
                publishSnapshot()
                if let menu = statusItem.menu { rebuildMenu(menu) }
            }
            return
        }
        // Claude Code hands its statusline command the same rate-limit windows this app used
        // to borrow a token to fetch. While the sidecar is fresh it wins outright: nothing
        // here needs a credential, a Keychain prompt, or a request. Freshness matters as much
        // as presence: a sidecar goes quiet the moment Claude Code does, and a non-empty but
        // hours-old reading once shadowed a live sign-in by five points.
        let feed = StatuslineFeed.read(path: StatuslineFeed.defaultPath())
        if let feed, !feed.isEmpty, feed.isFresh() {
            claudeLimits = feed.windows
            claudeLimitsAt = feed.updatedAt
            claudeLimitsSource = .feed
            limitsStatus = nil
            dashboardModel.data.limitsNote = nil
            dashboardModel.data.claudeLimitsAsOf = claudeLimitsAt
            updateTitle()
            publishSnapshot()
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        // Someone else's sidecar, when the user has pointed at one. Read only after our own
        // feed has been given first refusal, and only while it is fresh: a file another tool
        // stopped updating is exactly as misleading as our own would be.
        if let external = Sidecar.readExternal(path: config.externalUsagePath) {
            claudeLimits = external.windows
            claudeLimitsAt = external.updatedAt
            claudeLimitsSource = .external
            limitsStatus = nil
            dashboardModel.data.limitsNote = nil
            dashboardModel.data.claudeLimitsAsOf = claudeLimitsAt
            updateTitle()
            publishSnapshot()
            if let menu = statusItem.menu { rebuildMenu(menu) }
            return
        }
        // Nothing to fetch without a credential, and asking anyway reported the feed's own
        // quiet moment as a missing source. The sidecar's last reading is the whole answer.
        guard oauth.isSignedIn || config.useCLIToken else {
            applyFeedOnly(feed)
            return
        }
        // The feed is quiet or absent, so a live fetch fills the gap: sign-in first, borrowed
        // token when the user enabled that instead.
        oauth.fetchLimits { [weak self] limits, err in
            DispatchQueue.main.async {
                guard let self else { return }
                if let limits, !limits.isEmpty {
                    self.claudeLimits = limits
                    self.claudeLimitsAt = Date()
                    self.claudeLimitsSource = self.oauth.usingCLIToken ? .cliToken : .signIn
                    self.limitsStatus = nil
                } else if let feed, !feed.isEmpty {
                    // No live source answered. The feed's last unexpired reading beats a
                    // blank, and its age travels with it so every surface draws it stale
                    // rather than current. "Not signed in" is the expected state for a
                    // feed-only install, not news; a failure from an actual sign-in is.
                    self.claudeLimits = feed.windows
                    self.claudeLimitsAt = feed.updatedAt
                    self.claudeLimitsSource = .feed
                    self.limitsStatus = self.oauth.isSignedIn ? err : nil
                } else {
                    // A lost token invalidates the cached percentages, but a feed the user
                    // still wants is only quiet, so its windows stay and drain by age instead.
                    if err != nil, !self.oauth.isSignedIn, !StatuslineInstaller.isWanted() {
                        self.claudeLimits = []
                        self.claudeLimitsAt = nil
                        self.claudeLimitsSource = nil
                    } else {
                        self.claudeLimits = LimitParser.unexpired(self.claudeLimits)
                    }
                    self.limitsStatus = err
                        ?? (self.claudeLimits.isEmpty ? self.claudeSourceNote : nil)
                }
                // The dashboard gets the same honesty as the menu: a missing rail with no
                // reason attached reads as broken
                self.dashboardModel.data.limitsNote =
                    self.limitsStatus.map { "Claude limits: \($0)" }
                self.dashboardModel.data.claudeLimitsAsOf = self.claudeLimitsAt
                self.updateTitle()
                self.publishSnapshot()
                if let menu = self.statusItem.menu { self.rebuildMenu(menu) }
            }
        }
    }

    /// The feed alone, with no credential behind it. Its sidecar moves only while Claude Code
    /// draws, so a gap between readings keeps the last windows rather than blanking them.
    private func applyFeedOnly(_ feed: StatuslineSnapshot?) {
        if let feed, !feed.isEmpty {
            claudeLimits = feed.windows
            claudeLimitsAt = feed.updatedAt
            claudeLimitsSource = .feed
        } else {
            claudeLimits = LimitParser.unexpired(claudeLimits)
            if claudeLimits.isEmpty {
                claudeLimitsAt = nil
                claudeLimitsSource = nil
            }
        }
        limitsStatus = claudeLimits.isEmpty ? claudeSourceNote : nil
        dashboardModel.data.limitsNote = limitsStatus.map { "Claude limits: \($0)" }
        dashboardModel.data.claudeLimitsAsOf = claudeLimitsAt
        updateTitle()
        publishSnapshot()
        if let menu = statusItem.menu { rebuildMenu(menu) }
    }

    private static let feedWaiting = "Waiting for Claude Code to report usage"

    /// Which nothing this is. A wired-up feed holding no reading yet waits on Claude Code;
    /// only an unwired one is a source the user still has to pick.
    private var claudeSourceNote: String {
        StatuslineInstaller.isWanted() ? Self.feedWaiting : "No limits source set up"
    }

    // MARK: - UI

    private func updateTitle() {
        renderTitle()
        // After the readout, never inside it: every branch below returns early, and a badge
        // wired into one of them would have been invisible in the ordinary case
        applyFleetBadge()
    }

    private func renderTitle() {
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
            // A source that is merely between readings is pending, not disconnected. Signal
            // red belongs to the one state where clicking through actually fixes something.
            let pending = signedIn || config.useCLIToken || StatuslineInstaller.isWanted()
            button.attributedTitle = coloredTitle([
                                (pending ? "…" : "Connect", pending ? .tertiaryLabelColor
                                                      : contrasted(NSColor(Brand.signal))),
            ])
            // Naming the cause here saves opening the menu to find out what broke
            button.toolTip = signedIn ? "Usage loading…"
                : config.useCLIToken
                    ? "Claude Code's Keychain token is not readable; open RedLine to fix it"
                    : StatuslineInstaller.isWanted() ? Self.feedWaiting
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

    /// An amber dot ahead of the readout while a session is blocked on the user, counted once
    /// there is more than one. Only waiting earns it: a busy fleet is the ordinary state, and
    /// a badge that is always lit says nothing.
    private func applyFleetBadge() {
        guard config.agentFleet, let button = statusItem.button else { return }
        let waiting = fleet.waiting
        guard !waiting.isEmpty else { return }
        let badge = NSMutableAttributedString(attributedString: coloredTitle([
            (waiting.count > 1 ? "●\(waiting.count) " : "● ",
             contrasted(Brandkit.nsTone(.warning))),
        ]))
        badge.append(button.attributedTitle)
        button.attributedTitle = badge
        let head = waiting.count == 1 ? "1 agent waiting on you"
                                      : "\(waiting.count) agents waiting on you"
        let names = waiting.map { $0.label }.joined(separator: ", ")
        button.toolTip = [head + ": " + names, button.toolTip]
            .compactMap { $0 }.joined(separator: "\n")
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

    // Style preferences: which windows the readout reports, and how much text rides along
    private var wantsSessionWindow: Bool { config.limitWindows != "week" }
    private var wantsWeekWindow: Bool { config.limitWindows != "session" }
    private func wantsWindow(_ w: LimitWindow) -> Bool {
        if w.key.hasPrefix("five_hour") { return wantsSessionWindow }
        if w.key.hasPrefix("seven_day") { return wantsWeekWindow }
        return true
    }

    /// Claude's windows are only as current as their source: the statusline feed stops
    /// writing the moment Claude Code does. Past this threshold every surface drains the
    /// window's status color to steel, so an old percentage can never impersonate a live one.
    private var claudeLimitsAreStale: Bool {
        guard let at = claudeLimitsAt else { return false }
        return Date().timeIntervalSince(at) > max(config.pollIntervalSeconds * 2, 600)
    }

    private func isStale(_ w: LimitWindow) -> Bool {
        w.provider == UsageStore.provider && claudeLimitsAreStale
    }

    /// Status color while the reading is live, steel once it is not. The number stays; the
    /// palette is what says "as of earlier", the same way the dashboard and widget draw it.
    private func windowColor(_ w: LimitWindow) -> NSColor {
        isStale(w) ? Brandkit.menuSecondary : limitColor(w.utilization)
    }

    private func limitsTitle() -> NSAttributedString? {
        let session = wantsSessionWindow ? worst(in: ["five_hour"]) : nil
        let week = wantsWeekWindow ? worst(in: ["seven_day"]) : nil
        guard session != nil || week != nil else { return nil }
        var parts: [(String, NSColor?)] = []
        if let s = session {
            parts.append(("\(pct(s))%", windowColor(s)))
            if config.showResetTimes, let r = fmtResetShort(s.resetsAt) {
                parts.append((" \(r)", .labelColor))
            }
        }
        if session != nil && week != nil { parts.append((" | ", .tertiaryLabelColor)) }
        if let w = week {
            parts.append(("\(pct(w))%", windowColor(w)))
            if config.showResetTimes, let r = fmtResetShort(w.resetsAt) {
                parts.append((" \(r)", .labelColor))
            }
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

    /// The one state where the dropdown opens on a problem instead of the numbers. Claude Code
    /// rewrites its Keychain item whenever it refreshes its token, and the rewrite drops
    /// RedLine's Always Allow, so this comes back periodically through no fault of the user.
    /// It used to sit below another provider's section, where it had to be hunted for.
    private func addTokenTrouble(_ menu: NSMenu) {
        guard config.wants(UsageStore.provider), !oauth.isSignedIn, config.useCLIToken
        else { return }
        // The loud row is the one you can click. An inert warning line above a plain action
        // read as decoration and got scrolled past.
        // Named for what the user gets back, not for the macOS machinery that broke. The
        // amber carries the same meaning it does on a limit rail: this needs you.
        let attention = contrasted(Brandkit.nsTone(.warning))
        let fix = NSMenuItem(title: "Reconnect Claude usage…",
                             action: #selector(fixKeychainAccess(_:)), keyEquivalent: "")
        fix.target = self
        let label = NSMutableAttributedString()
        label.append(symbolRun(ServiceGlyph.symbol(for: "minor"),
                               color: attention, size: NSFont.systemFontSize))
        label.append(NSAttributedString(
            string: "  Reconnect Claude usage…",
            attributes: [.font: NSFont.systemFont(ofSize: NSFont.systemFontSize,
                                                  weight: .semibold),
                         .foregroundColor: attention]))
        fix.attributedTitle = label
        fix.toolTip = "Claude Code refreshed its token, and macOS forgets who was allowed to "
                    + "read it. This asks again; choose Always Allow."
        menu.addItem(fix)
        addInfo(menu, "    Percentages are paused until you allow it again", secondary: true)
        menu.addItem(.separator())
    }

    private func rebuildMenu(_ menu: NSMenu) {
        // Never rip the items out from under an open menu; the fresh data lands on close.
        // The menu bar title still updates live, so nothing visible goes stale meanwhile.
        if menuIsOpen {
            menuNeedsRebuild = true
            return
        }
        menu.removeAllItems()
        // Without this, an item with no action is auto-disabled and macOS dims the whole row,
        // which is what made the usage tables hard to read. No action still means inert.
        menu.autoenablesItems = false
        addTokenTrouble(menu)

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
        // Claude's notes belong under Claude's own section; appended after the loop they
        // visually attached to whichever provider sorted last
        var claudeNotesEmitted = false
        func emitClaudeNotes() {
            claudeNotesEmitted = true
            if let s = limitsStatus { addInfo(menu, "    \(s)") }
            // Provenance, always: three sources can produce the same percentage, and a
            // number that cannot say where it came from cannot be trusted or fixed.
            if !claudeLimits.isEmpty, let source = claudeLimitsSource {
                let line = switch source {
                case .feed:     "    via the usage feed"
                case .external: "    via another tool's usage sidecar"
                case .signIn:   "    via your Claude sign-in"
                case .cliToken: "    via the Claude CLI's token"
                }
                addInfo(menu, line, secondary: true)
            }
        }
        for provider in grouped.keys.sorted() {
            addProviderHeader(menu, provider: provider)
            for w in LimitParser.sorted(grouped[provider] ?? []) where wantsWindow(w) {
                let reset = config.showResetTimes ? fmtReset(w.resetsAt) : ""
                // A stale window is drained wholesale: steel dot, steel text. The header's
                // "last updated" suffix says when it was true; the palette says "not now".
                let stale = isStale(w)
                // A percentage says how much is gone; the pace says whether it runs out
                // before it resets, which is the question the percentage was standing in
                // for. It rides on the same row: on its own line it read as a second fact
                // about the section rather than more about this window, and sat next to the
                // provenance note as if the two were a pair. Never drawn from a stale
                // reading, because a rate needs the number to be current.
                var runs: [(String, NSColor)] = [
                    ("    ● ", windowColor(w)),
                    ("\(w.displayName): \(pct(w))%\(reset)",
                     stale ? Brandkit.menuSecondary : Brandkit.menuPrimary),
                ]
                if !stale, let pace = paces.first(where: {
                    $0.provider == w.provider && $0.key == w.key
                }), let summary = pace.compact() {
                    runs.append((" · \(summary)", pace.hitsLimitBeforeReset
                                    ? NSColor(Brand.amber) : Brandkit.menuSecondary))
                }
                addStatic(menu, coloredTitle(runs))
            }
            if provider == UsageStore.provider { emitClaudeNotes() }
        }
        if !claudeNotesEmitted { emitClaudeNotes() }

        // Percentages that are off or broken must say so where the user is looking, with
        // the fix one click away. A working feed already delivers percentages, so the claim
        // "off" is reserved for when nothing is producing them. The full set of source
        // choices lives in one place, Settings > Claude Limits Source; this row is the door.
        if config.wants(UsageStore.provider), !oauth.isSignedIn, !config.useCLIToken,
           claudeLimits.isEmpty, !StatuslineInstaller.isWanted() {
            addInfo(menu, "    Claude percentages are off", secondary: true)
            // One door, opening on the right room: the feed needs Claude Code, so a
            // claude.ai-only user is sent to the browser sign-in instead.
            let hasCLI = availability.has(UsageStore.provider)
            let fix = NSMenuItem(title: "Show Claude Percentages…",
                                 action: hasCLI ? #selector(installStatuslineFeed(_:))
                                                : #selector(browserSignIn(_:)),
                                 keyEquivalent: "")
            fix.target = self
            fix.toolTip = hasCLI
                ? "Sets up the usage feed: the windows Claude Code hands its statusline. "
                    + "No sign-in, no Keychain, no network. Other sources are under "
                    + "Settings > Claude Limits Source."
                : "Signs in with your Claude account in a browser. Other sources are under "
                    + "Settings > Claude Limits Source."
            menu.addItem(fix)
        }
        menu.addItem(.separator())

        addFleet(menu)
        addSection(menu, label: "Today", agg: today, detail: true)
        menu.addItem(.separator())
        addSection(menu, label: "Last 5 hours", agg: block5h, detail: false)
        menu.addItem(.separator())
        addSection(menu, label: "Last 7 days", agg: week, detail: true, showIdle: true)
        menu.addItem(.separator())

        let updated = lastRefresh.map {
            DateFormatter.localizedString(from: $0, dateStyle: .none, timeStyle: .medium)
        } ?? "never"
        // Human units, not developer ones: "polling every 300s" made users ask whether
        // something was wrong. Limits also update live while Claude Code feeds the sidecar.
        let every = config.pollIntervalSeconds.truncatingRemainder(dividingBy: 60) == 0
            ? "\(Int(config.pollIntervalSeconds / 60))m"
            : "\(Int(config.pollIntervalSeconds))s"
        addInfo(menu, "Updated \(updated) · rescans every \(every)")

        // Findings live in the dashboard; this is the line that says there are any. Nothing
        // is shown until a scan has actually run, so an empty row never implies a clean bill.
        if config.findingsScans, let report = findingsReport, !report.isEmpty {
            let f = NSMenuItem(title: "Setup findings: \(report.summary)",
                               action: #selector(openDashboard(_:)), keyEquivalent: "")
            f.target = self
            f.toolTip = "What your transcripts say about how Claude Code is configured. "
                      + "Opens the dashboard."
            menu.addItem(f)
        }

        let d = NSMenuItem(title: "Open Usage Dashboard…",
                           action: #selector(openDashboard(_:)), keyEquivalent: "d")
        d.target = self
        menu.addItem(d)

        let r = NSMenuItem(title: "Refresh Now", action: #selector(refreshNow(_:)),
                           keyEquivalent: "r")
        r.target = self
        menu.addItem(r)
        menu.addItem(.separator())

        // One hover away, rather than a flat column of toggles under the numbers: the
        // readout is what the dropdown is for, and the settings had grown longer than it.
        let settingsItem = NSMenuItem(title: "Settings", action: nil, keyEquivalent: "")
        settingsItem.submenu = buildSettingsMenu()
        menu.addItem(settingsItem)
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

    /// Everything that changes how RedLine behaves, in the order it gets reached for.
    /// Providers are chosen in one place only, the setup window, which also carries the
    /// Claude limits decision.
    /// Sources first, display second, behaviour third, the raw file last. Every credential
    /// and setup decision lives under exactly one roof so it can be found again later, not
    /// only at the moment a conditional item happened to be visible.
    private func buildSettingsMenu() -> NSMenu {
        let menu = NSMenu()
        let setup = NSMenuItem(title: "Set Up RedLine…",
                               action: #selector(showSetup(_:)), keyEquivalent: ",")
        setup.target = self
        setup.toolTip = "Which providers RedLine reads, and where Claude's percentages "
                      + "come from"
        menu.addItem(setup)

        // Every way the Claude percentages can be sourced, in one place, with the active
        // ones checked. These items used to be scattered across conditional menus, which
        // meant sign-in could be found only while it was broken.
        if availability.has(UsageStore.provider) || config.wants(UsageStore.provider)
            || oauth.isSignedIn {
            let srcItem = NSMenuItem(title: "Claude Limits Source",
                                     action: nil, keyEquivalent: "")
            let src = NSMenu()
            if availability.has(UsageStore.provider) {
                let feedInstalled = StatuslineInstaller.isInstalled()
                let f = NSMenuItem(title: "Set Up Claude Tracking…",
                                   action: #selector(installStatuslineFeed(_:)),
                                   keyEquivalent: "")
                f.target = self
                f.state = feedInstalled ? .on : .off
                f.toolTip = feedInstalled
                    ? "Installed: the usage feed reads the windows Claude Code hands its "
                        + "statusline. Re-running updates the wrapper."
                    : "The recommended source: the windows Claude Code hands its statusline. "
                        + "No sign-in, no Keychain, no network."
                src.addItem(f)
            }
            let signedIn = oauth.hasOwnGrant
            let b = NSMenuItem(title: signedIn ? "Sign Out of Claude"
                                               : "Sign In with Browser…",
                               action: signedIn ? #selector(signOut(_:))
                                                : #selector(browserSignIn(_:)),
                               keyEquivalent: "")
            b.target = self
            b.state = signedIn ? .on : .off
            b.toolTip = signedIn
                ? "Signed in: RedLine fetches live percentages with its own grant whenever "
                    + "the feed is quiet"
                : "Live between sessions too; also the route for claude.ai users without "
                    + "Claude Code. Needs oauth.clientId in the config."
            src.addItem(b)
            let cli = NSMenuItem(title: "Use the Claude Code CLI's Token",
                                 action: #selector(toggleCLIToken(_:)), keyEquivalent: "")
            cli.target = self
            cli.state = config.useCLIToken ? .on : .off
            cli.toolTip = "Reads Claude Code's token from your Keychain. Only ever read, "
                        + "never refreshed, so it cannot sign the CLI out."
            src.addItem(cli)
            srcItem.submenu = src
            menu.addItem(srcItem)
        }

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
        menu.addItem(.separator())

        // One submenu for everything the menu bar readout is, was two ("Shows" / "Style")
        let barItem = NSMenuItem(title: "Menu Bar", action: nil, keyEquivalent: "")
        let bar = NSMenu()
        if availability.hasChoice {
            for choice in availability.trackChoices {
                let title = choice == Config.autoProvider
                    ? "Nearest Limit (any provider)" : choice
                let item = NSMenuItem(title: title,
                                      action: #selector(pickMenuBarProvider(_:)),
                                      keyEquivalent: "")
                item.target = self
                item.representedObject = choice
                item.state = choice.caseInsensitiveCompare(config.menuBarProvider)
                    == .orderedSame ? .on : .off
                // A provider that is switched off entirely has nothing to report
                if choice != Config.autoProvider, !config.wants(choice) {
                    item.isEnabled = false
                    item.toolTip = "Enable \(choice) in Set Up RedLine first"
                } else if choice == OllamaStore.provider {
                    item.toolTip = "Ollama has no rate limit; shows tokens used today"
                }
                bar.addItem(item)
            }
            bar.addItem(.separator())
        }
        // Compactness is taste, so every element is a choice rather than a mode
        let icon = NSMenuItem(title: "Show Icon", action: #selector(toggleMenuIcon(_:)),
                              keyEquivalent: "")
        icon.target = self
        icon.state = config.showMenuIcon ? .on : .off
        bar.addItem(icon)
        let resets = NSMenuItem(title: "Show Reset Times",
                                action: #selector(toggleResetTimes(_:)), keyEquivalent: "")
        resets.target = self
        resets.state = config.showResetTimes ? .on : .off
        bar.addItem(resets)
        bar.addItem(.separator())
        for (title, value) in [("All Limits", "all"), ("Session Only", "session"),
                               ("Week Only", "week")] {
            let item = NSMenuItem(title: title, action: #selector(pickLimitWindows(_:)),
                                  keyEquivalent: "")
            item.target = self
            item.representedObject = value
            item.state = config.limitWindows == value ? .on : .off
            bar.addItem(item)
        }
        barItem.submenu = bar
        menu.addItem(barItem)

        let alerts = NSMenuItem(title: "Notify at Thresholds",
                                action: #selector(toggleAlerts(_:)), keyEquivalent: "")
        alerts.target = self
        alerts.state = config.alerts ? .on : .off
        alerts.toolTip = "Posts a notification when a window crosses \(Int(config.limitYellowPct))%, "
                       + "\(Int(config.limitRedPct))% or 95%, when one is about to run out "
                       + "before it resets, and when one rolls over. Never from a stale reading."
        menu.addItem(alerts)

        let history = NSMenuItem(title: "Keep Local History",
                                 action: #selector(toggleHistory(_:)), keyEquivalent: "")
        history.target = self
        history.state = config.recordHistory ? .on : .off
        history.toolTip = "Rolls each day up into ~/.local/share/redline/history so your "
                        + "own numbers outlive Claude Code's 30 day transcript cleanup. "
                        + "Local file, no network."
        menu.addItem(history)

        let cues = NSMenuItem(title: "Say How the Day Is Going",
                              action: #selector(toggleCues(_:)), keyEquivalent: "")
        cues.target = self
        cues.state = config.mindfulCues ? .on : .off
        cues.toolTip = "Says when a run has gone \(Int(config.stretchMinutes)) minutes "
                     + "without a break, when you are still going after "
                     + "\(config.lateHour):00, and when \(config.streakDays) days have run "
                     + "together. Counted from timestamps, stated once, no advice."
        menu.addItem(cues)

        let agents = NSMenuItem(title: "Show Running Agents",
                                action: #selector(toggleAgentFleet(_:)), keyEquivalent: "")
        agents.target = self
        agents.state = config.agentFleet ? .on : .off
        agents.toolTip = "Lists the Claude Code sessions running on this Mac and marks the "
                       + "menu bar when one is waiting on you. Reads Claude Code's own "
                       + "session registry; nothing is written there."
        menu.addItem(agents)

        let sidecar = NSMenuItem(title: "Publish Usage Sidecar",
                                 action: #selector(toggleSidecar(_:)), keyEquivalent: "")
        sidecar.target = self
        sidecar.state = config.publishSidecar ? .on : .off
        sidecar.toolTip = "Writes the current windows to "
                        + "~/.local/share/redline/usage-snapshot.json in the shape other "
                        + "local tools already read. Nothing leaves this machine."
        menu.addItem(sidecar)

        let svc = NSMenuItem(title: "Check Service Status Pages",
                             action: #selector(toggleStatusChecks(_:)), keyEquivalent: "")
        svc.target = self
        svc.state = config.statusChecks ? .on : .off
        svc.toolTip = "Polls the providers' public status pages every 15 minutes; "
                    + "Refresh Now checks immediately"
        menu.addItem(svc)
        menu.addItem(.separator())

        let l = NSMenuItem(title: "Launch at Login",
                           action: #selector(toggleLaunchAtLogin(_:)), keyEquivalent: "")
        l.target = self
        l.state = LaunchAgent.isInstalled ? .on : .off
        menu.addItem(l)

        // Updates under one roof: how often RedLine looks, and which releases it will offer.
        // The channel used to be reachable only by hand-editing config.json.
        let updItem = NSMenuItem(title: "Updates", action: nil, keyEquivalent: "")
        let upd = NSMenu()
        let auto = NSMenuItem(title: "Check Daily",
                              action: #selector(toggleAutoUpdates(_:)), keyEquivalent: "")
        auto.target = self
        auto.state = config.autoCheckUpdates ? .on : .off
        auto.toolTip = "Asks the GitHub releases API once a day and speaks up only when an "
                     + "update exists. This is the one network request RedLine makes without "
                     + "being asked; switch it off and updates are yours to check for."
        upd.addItem(auto)
        upd.addItem(.separator())
        for (title, value) in [("Stable Releases", "stable"), ("Beta Releases", "beta")] {
            let item = NSMenuItem(title: title, action: #selector(pickUpdateChannel(_:)),
                                  keyEquivalent: "")
            item.target = self
            item.representedObject = value
            item.state = config.updateChannel == value ? .on : .off
            item.toolTip = value == "beta"
                ? "Also offers prerelease builds such as 0.5.0-beta.1. Newest always wins, so "
                    + "a stable release still reaches you the moment it outranks them."
                : "Full releases only. Picked while running a beta, RedLine stays on that "
                    + "build until a stable release outranks it."
            upd.addItem(item)
        }
        updItem.submenu = upd
        menu.addItem(updItem)

        // Only shown while the durable grant is missing; once FDA is on there is nothing
        // left to fix
        if !hasFullDiskAccess {
            let fda = NSMenuItem(title: "Stop Permission Prompts…",
                                 action: #selector(grantFullDiskAccess(_:)), keyEquivalent: "")
            fda.target = self
            fda.toolTip = "Full Disk Access is the one grant macOS remembers"
            menu.addItem(fda)
        }
        menu.addItem(.separator())

        let e = NSMenuItem(title: "Edit Config…", action: #selector(editConfig(_:)),
                           keyEquivalent: "")
        e.target = self
        e.toolTip = "The raw config.json; everything above edits the same file"
        menu.addItem(e)
        return menu
    }

    // A fetch that keeps failing must not leave old percentages looking current
    private func staleSuffix(for provider: String) -> String {
        guard provider == UsageStore.provider, claudeLimitsAreStale,
              let at = claudeLimitsAt else { return "" }
        let f = DateFormatter()
        f.dateFormat = "h:mm a"
        return "  (as of \(f.string(from: at)))"
    }

    private func addSection(_ menu: NSMenu, label: String, agg: Agg, detail: Bool,
                            showIdle: Bool = false) {
        let partial = agg.hasUnpriced ? "+" : ""
        addInfo(menu, "\(label): \(fmtCost(agg.cost))\(partial) est, \(fmtTokens(agg.io)) in+out")
        addInfo(menu, "    cache read \(fmtTokens(agg.cacheRead)), write \(fmtTokens(agg.cacheWrite))",
                secondary: true)
        guard detail else { return }

        for (provider, usage) in agg.rankedProviders {
            addRow(menu, indent: 1, dot: Brandkit.nsColor(for: provider),
                   name: provider,
                   share: agg.share(ofIO: usage.io),
                   cost: usage.cost, priced: true, io: usage.io,
                   tint: Brandkit.nsColor(for: provider))

            // Models sit under the provider that produced them, never in one flat list
            for (model, m) in usage.rankedModels {
                // Detected on the full name, since shortening may drop the cloud tag
                let cloudMark = OllamaLocality.isCloud(model) ? "☁ " : ""
                addRow(menu, indent: 2, dot: nil,
                       name: cloudMark + Sparkline.shortModel(model),
                       share: agg.share(ofIO: m.io),
                       cost: m.cost, priced: m.priced, io: m.io,
                       tint: Brandkit.nsColor(for: provider).withAlphaComponent(0.75))
            }
        }
        if agg.hasUnpriced {
            addInfo(menu, "    + no pricing entry for models shown with a dash",
                    secondary: true)
        }
        // An installed provider that simply went quiet is not the same as one that is absent
        // or broken. Saying so stops a silent gap reading as failed tracking.
        if showIdle {
            for provider in idleProviders(in: agg) {
                addIdleRow(menu, provider: provider, period: label.lowercased())
            }
        }
    }

    /// Providers this Mac has, and the config reads, that produced nothing in this window.
    private func idleProviders(in agg: Agg) -> [String] {
        availability.installed.filter { config.wants($0) && agg.providers[$0] == nil }
    }

    /// A hollow dot rather than a filled one, and words rather than a zero: a $0.00 row here
    /// would read as "ran, cost nothing" instead of "did not run".
    private func addIdleRow(_ menu: NSMenu, provider: String, period: String) {
        let pad = "    "
        let marker = "○ "
        let nameWidth = max(4, Self.leadWidth - pad.count - marker.count)
        addStatic(menu, monoTitle([
            ("\(pad)\(marker)", contrasted(Brandkit.nsColor(for: provider))),
            (Sparkline.pad(provider, to: nameWidth), Brandkit.menuSecondary),
            ("no usage in the \(period)", Brandkit.menuSecondary),
        ]))
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
        // A dash rather than $0.00, which would read as free. Money is green wherever it
        // appears, and the column is sized for grouped thousands ("$18,108.58").
        let costText = priced ? fmtCost(cost) : "—"
        parts.append(("  \(Sparkline.pad(costText, to: 11, alignRight: true))",
                      priced ? contrasted(Brandkit.nsTone(.healthy)) : Brandkit.menuSecondary))
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

    /// "Claude limits:" plus, when there is something to report, the same health glyph the
    /// dashboard draws and the words beside it.
    private func addProviderHeader(_ menu: NSMenu, provider: String) {
        let title = NSMutableAttributedString(attributedString: monoTitle(
            [("\(provider) limits:\(staleSuffix(for: provider))", Brandkit.menuPrimary)],
            mono: false))
        if let mark = serviceMark(for: provider) {
            title.append(monoTitle([("  ", nil)], mono: false))
            title.append(symbolRun(ServiceGlyph.symbol(for: mark.indicator),
                                   color: contrasted(Brandkit.nsTone(
                                       ServiceGlyph.tone(for: mark.indicator))),
                                   size: NSFont.systemFontSize))
            title.append(monoTitle([(" \(mark.phrase)", Brandkit.menuSecondary)], mono: false))
        }
        addStatic(menu, title)
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
