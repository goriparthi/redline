// The settings window: everything that changes how RedLine behaves, in named sections rather
// than in one long menu. Every control writes straight through to config.json, which is still
// the single source of truth; nothing here holds unsaved state.
import RedlineCore
import SwiftUI

/// The things settings can ask the app to do.
///
/// Every preference with a side effect beyond writing the file routes through one of these
/// rather than being written here: switching alerts on is the moment macOS is asked for
/// permission, switching the agent fleet on starts two file watchers, and switching the CLI
/// token on triggers a Keychain read that may need the user. Those decisions live in the app,
/// and this window drives them instead of reimplementing them.
struct SettingsActions {
    var openSetup: () -> Void = {}
    var installClaudeFeed: () -> Void = {}
    var signIn: () -> Void = {}
    var signOut: () -> Void = {}
    var installOllamaShim: () -> Void = {}
    var openNotificationSettings: () -> Void = {}
    var grantFullDiskAccess: () -> Void = {}
    var toggleLaunchAtLogin: () -> Void = {}
    var checkForUpdates: () -> Void = {}
    var editConfig: () -> Void = {}
    var openDataFolder: () -> Void = {}
    var uninstall: () -> Void = {}
    /// Reveals one of the bundled notice files. Nothing here reaches the network.
    var openNotice: (String) -> Void = { _ in }

    // Preferences whose side effects belong to the app
    var setProviders: ([String]) -> Void = { _ in }
    var setCLIToken: (Bool) -> Void = { _ in }
    var setAlerts: (Bool) -> Void = { _ in }
    var setCues: (Bool) -> Void = { _ in }
    var setHistory: (Bool) -> Void = { _ in }
    var setSidecar: (Bool) -> Void = { _ in }
    var setStatusChecks: (Bool) -> Void = { _ in }
    var setAutoUpdates: (Bool) -> Void = { _ in }
    var setUpdateChannel: (String) -> Void = { _ in }
    var setAgentFleet: (Bool) -> Void = { _ in }
    var setMenuIcon: (Bool) -> Void = { _ in }
    var setResetTimes: (Bool) -> Void = { _ in }
    var setLimitWindows: (String) -> Void = { _ in }
    var setMenuBarProvider: (String) -> Void = { _ in }
    var setTheme: (String) -> Void = { _ in }
}

/// State settings needs that does not live in the config: what is installed, and whether the
/// app is signed in. Refreshed by the app when the window opens, because each of these is a
/// question about the machine rather than about a preference.
struct SettingsEnvironmentState {
    var signedIn = false
    var oauthConfigured = false
    var claudeFeedInstalled = false
    var ollamaShimInstalled = false
    var launchAtLogin = false
    var fullDiskAccess = false
    var availability = ProviderAvailability(installed: [])
    var appVersion = ""
}

final class SettingsModel: ObservableObject {
    @Published var config: Config
    @Published var state = SettingsEnvironmentState()
    @Published var section: SettingsSection = .providers
    var actions = SettingsActions()
    /// Called after any write, so the app reloads the config it is actually running on
    /// instead of drifting from the file.
    var onConfigChanged: (() -> Void)?

    init(config: Config = Config.load()) {
        self.config = config
    }

    /// Writes the given keys and re-reads the file. Re-reading rather than mutating in place
    /// is what keeps validation and clamping in one place: the config decides what a value
    /// becomes, not the control that offered it.
    ///
    /// Only for preferences whose whole effect is the stored value. Anything with a side
    /// effect goes through `SettingsActions`.
    func write(_ values: [String: Any]) {
        guard Config.write(values) else { return }
        config = Config.load()
        onConfigChanged?()
    }

    /// A binding over a plain preference: writes through on change, no side effect.
    func binding<T: Equatable>(_ key: String, _ path: KeyPath<Config, T>,
                               transform: @escaping (T) -> Any = { $0 }) -> Binding<T> {
        Binding(
            get: { self.config[keyPath: path] },
            set: { self.write([key: transform($0)]) })
    }

    /// A binding over a preference the app owns: reads the current value, and hands the new
    /// one to the app so its side effects run exactly once, in one place.
    func action<T: Equatable>(_ path: KeyPath<Config, T>,
                              _ apply: @escaping (T) -> Void) -> Binding<T> {
        Binding(
            get: { self.config[keyPath: path] },
            set: { next in
                guard next != self.config[keyPath: path] else { return }
                apply(next)
                // The app writes the file and reloads; re-read so the control settles on
                // whatever the config actually accepted rather than on what was asked for.
                self.config = Config.load()
            })
    }

    /// Re-reads the config, for the app to call after it has changed something itself.
    func reload() { config = Config.load() }

    func setProvider(_ provider: String, on: Bool) {
        var providers = config.providers.filter { !$0.isEmpty }
        if on {
            guard !providers.contains(where: {
                $0.caseInsensitiveCompare(provider) == .orderedSame
            }) else { return }
            providers.append(provider)
        } else {
            providers.removeAll { $0.caseInsensitiveCompare(provider) == .orderedSame }
        }
        // An empty list means "read everything" further down, so the last provider cannot be
        // switched off here; the message beside the toggles says so.
        guard !providers.isEmpty else { return }
        // Through the app, because changing what is read changes what is detected and has to
        // trigger a rescan
        actions.setProviders(Config.knownProviders.filter { known in
            providers.contains { $0.caseInsensitiveCompare(known) == .orderedSame }
        })
        config = Config.load()
    }

    func reads(_ provider: String) -> Bool { config.wants(provider) }

    /// True when this is the only provider still switched on, so the toggle can say why it
    /// will not turn off rather than simply refusing.
    func isLastEnabled(_ provider: String) -> Bool {
        reads(provider) && config.providers.count <= 1
    }
}

enum SettingsSection: String, CaseIterable, Identifiable {
    case providers, monitoring, limits, appearance, data, about

    var id: String { rawValue }

    var title: String {
        switch self {
        case .providers:  return "Providers"
        case .monitoring: return "Refresh and Monitoring"
        case .limits:     return "Limits and Alerts"
        case .appearance: return "Appearance"
        case .data:       return "Data and Privacy"
        case .about:      return "About"
        }
    }

    var symbol: String {
        switch self {
        case .providers:  return "square.stack.3d.up"
        case .monitoring: return "arrow.clockwise"
        case .limits:     return "gauge.with.needle"
        case .appearance: return "paintbrush"
        case .data:       return "lock.shield"
        case .about:      return "info.circle"
        }
    }
}

struct SettingsView: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        // Two explicit columns rather than a NavigationSplitView.
        //
        // On macOS 26 the split view floats its sidebar over the detail pane and puts a
        // sidebar-toggle in a titlebar this window has no toolbar for, so the detail content
        // slid under both and the section heading was clipped. A settings window with six fixed
        // sections gains nothing from a collapsible sidebar, and this way the layout is the
        // same on every OS version the app supports.
        HStack(spacing: 0) {
            sidebar
            Divider().overlay(RL.Stroke.hairline)
            detail
        }
        .frame(minWidth: 720, minHeight: 470)
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: RL.Space.xxs) {
            ForEach(SettingsSection.allCases) { section in
                SettingsSidebarRow(section: section,
                                   selected: section == model.section,
                                   help: hint(for: section)) {
                    model.section = section
                }
            }
            Spacer(minLength: 0)
        }
        .padding(RL.Space.md)
        // Wide enough for "Refresh and Monitoring", which truncated at 186
        .frame(width: 216)
        .frame(maxHeight: .infinity)
        .background(RL.Surface.raised)
    }

    private var detail: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: RL.Space.xl) {
                Text(model.section.title)
                    .font(RL.Typography.heading)
                    .foregroundStyle(RL.Ink.primary)
                switch model.section {
                case .providers:  ProvidersSettings(model: model)
                case .monitoring: MonitoringSettings(model: model)
                case .limits:     LimitsSettings(model: model)
                case .appearance: AppearanceSettings(model: model)
                case .data:       DataSettings(model: model)
                case .about:      AboutSettings(model: model)
                }
            }
            .padding(RL.Space.xxl)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RL.Surface.ground)
    }

    private func hint(for section: SettingsSection) -> String {
        switch section {
        case .providers:  return "Which tools RedLine reads, and where Claude's percentages "
                               + "come from"
        case .monitoring: return "How often RedLine rescans, and what the menu bar shows"
        case .limits:     return "Where the thresholds sit, and what RedLine says when one is "
                               + "crossed"
        case .appearance: return "The dashboard's appearance"
        case .data:       return "What is written to disk, and what leaves this Mac"
        case .about:      return "Version, updates, and third-party notices"
        }
    }
}

/// One sidebar row. Its own selection styling, so the accent is the product's rather than the
/// system's, and its own focus ring, because a plain button style draws none.
private struct SettingsSidebarRow: View {
    let section: SettingsSection
    let selected: Bool
    let help: String
    let action: () -> Void

    @State private var hovering = false
    @FocusState private var focused: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var fill: Color {
        if selected { return RL.Brandmark.signal.opacity(0.18) }
        return hovering ? RL.Surface.sunken : .clear
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: RL.Space.md) {
                Image(systemName: section.symbol)
                    .font(.system(size: 12, weight: .medium))
                    .frame(width: 16)
                Text(section.title)
                    .font(RL.Typography.body)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            .foregroundStyle(selected ? RL.Ink.primary : RL.Ink.secondary)
            .padding(.horizontal, RL.Space.md)
            .padding(.vertical, RL.Space.md)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(fill, in: RoundedRectangle(cornerRadius: RL.Radius.control,
                                                   style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: RL.Radius.control, style: .continuous)
                    .strokeBorder(focused ? Color.accentColor : .clear, lineWidth: 2)
            )
        }
        .buttonStyle(.plain)
        .focused($focused)
        .help(help)
        .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.hover), value: hovering)
        .onHover { hovering = $0 }
        .accessibilityLabel(section.title)
        .accessibilityHint(help)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }
}

// MARK: - Shared pieces

/// A titled group of controls. `Form` with `.grouped` would look native but takes over the
/// row layout; these sections carry the app's own card styling instead so settings and the
/// dashboard read as one product.
private struct SettingsGroup<Content: View>: View {
    let title: String
    var note: String? = nil
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.md) {
            RLSectionHeader(title)
            if let note {
                Text(note)
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            RLCard {
                VStack(alignment: .leading, spacing: RL.Space.lg) {
                    content
                }
            }
        }
    }
}

/// A labelled toggle with its explanation underneath, so the reason a preference exists sits
/// with the preference rather than in a tooltip nobody opens.
private struct SettingRow: View {
    let title: String
    let explanation: String
    @Binding var isOn: Bool
    var disabled = false
    var disabledNote: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xxs) {
            Toggle(isOn: $isOn) {
                Text(title)
                    .font(RL.Typography.body)
                    .foregroundStyle(RL.Ink.primary)
            }
            .disabled(disabled)
            .help(explanation)
            Text(disabled ? (disabledNote ?? explanation) : explanation)
                .font(RL.Typography.caption)
                .foregroundStyle(RL.Ink.muted)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.leading, 22)
        }
        .accessibilityHint(explanation)
    }
}

/// An action that opens something outside settings. Kept visually quieter than a toggle,
/// because it changes nothing on its own.
private struct SettingAction: View {
    let title: String
    let explanation: String
    var isDestructive = false
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xxs) {
            Button(title, action: action)
                .buttonStyle(.bordered)
                .foregroundStyle(isDestructive ? RL.State.error : RL.Ink.primary)
                .help(explanation)
            Text(explanation)
                .font(RL.Typography.caption)
                .foregroundStyle(RL.Ink.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

// MARK: - Providers

private struct ProvidersSettings: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            SettingsGroup(title: "What RedLine reads",
                          note: "Everything is read from files already on this Mac. At least "
                              + "one provider stays switched on.") {
                ForEach(Config.knownProviders, id: \.self) { provider in
                    providerRow(provider)
                    if provider != Config.knownProviders.last {
                        Divider().overlay(RL.Stroke.hairline)
                    }
                }
            }

            SettingsGroup(title: "Claude rate-limit percentages",
                          note: "The same session and week percentages /usage shows. "
                              + "Everything else works with these off.") {
                SettingAction(title: model.state.claudeFeedInstalled
                                  ? "Reinstall the Usage Feed…" : "Set Up Claude Tracking…",
                              explanation: model.state.claudeFeedInstalled
                                  ? "Installed. Reads the windows Claude Code hands its "
                                      + "statusline; re-running updates the wrapper."
                                  : "The recommended source: the windows Claude Code hands "
                                      + "its statusline. No sign-in, no Keychain, no network.",
                              action: model.actions.installClaudeFeed)
                Divider().overlay(RL.Stroke.hairline)
                SettingAction(title: model.state.signedIn ? "Sign Out of Claude"
                                                          : "Sign In with Browser…",
                              explanation: model.state.signedIn
                                  ? "Signed in. RedLine fetches live percentages with its own "
                                      + "grant whenever the feed is quiet."
                                  : model.state.oauthConfigured
                                      ? "Live between sessions too, and the route for "
                                          + "claude.ai users without Claude Code."
                                      : "Needs oauth.clientId in the config before it can be "
                                          + "used.",
                              action: model.state.signedIn ? model.actions.signOut
                                                           : model.actions.signIn)
                Divider().overlay(RL.Stroke.hairline)
                SettingRow(title: "Use the Claude Code CLI's token",
                           explanation: "Reads Claude Code's token from your Keychain, and "
                               + "only ever reads it, so it cannot sign the CLI out. The "
                               + "endpoint it is used against is undocumented.",
                           isOn: model.action(\.useCLIToken, model.actions.setCLIToken))
            }

            if model.state.availability.has(OllamaStore.provider) {
                SettingsGroup(title: "Ollama tracking",
                              note: "Ollama keeps no usage history of its own, so anything "
                                  + "that bypasses the shim is invisible by design.") {
                    SettingAction(title: model.state.ollamaShimInstalled
                                      ? "Reinstall the Ollama Shim…"
                                      : "Set Up Ollama Tracking…",
                                  explanation: "Installs a transparent ollama shim in "
                                      + "~/.local/bin so plain `ollama run` calls are counted.",
                                  action: model.actions.installOllamaShim)
                }
            }

            SettingsGroup(title: "Setup") {
                SettingAction(title: "Open the Setup Window…",
                              explanation: "The same first-run screen: which providers to "
                                  + "read, and where Claude's percentages come from.",
                              action: model.actions.openSetup)
            }
        }
    }
}

private extension ProvidersSettings {
    @ViewBuilder
    func providerRow(_ provider: String) -> some View {
        let installed = model.state.availability.has(provider)
        let identity = ProviderIdentity.of(provider)
        HStack(alignment: .top, spacing: RL.Space.lg) {
            Toggle(isOn: Binding(
                get: { model.reads(provider) },
                set: { model.setProvider(provider, on: $0) }
            )) {
                EmptyView()
            }
            .labelsHidden()
            .disabled(!installed || model.isLastEnabled(provider))
            .accessibilityLabel("Read \(provider)")

            VStack(alignment: .leading, spacing: RL.Space.xxs) {
                // The mark identifies the provider and the name states it; neither stands in
                // for the other
                ProviderBadge.forProvider(provider, size: 14)
                Text(identity?.blurb ?? "")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
                if !installed {
                    Text("Not found on this Mac")
                        .font(RL.Typography.caption)
                        .foregroundStyle(RL.State.warning)
                } else if model.isLastEnabled(provider) {
                    Text("The last provider stays on; switch another on first")
                        .font(RL.Typography.caption)
                        .foregroundStyle(RL.Ink.muted)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

// MARK: - Refresh and monitoring

private struct MonitoringSettings: View {
    @ObservedObject var model: SettingsModel

    private let intervals: [(Double, String)] = [(60, "1m"), (300, "5m"), (600, "10m"),
                                                 (1800, "30m")]

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            SettingsGroup(title: "Rescan interval",
                          note: "How often transcripts are rescanned. Claude's windows also "
                              + "update the moment Claude Code writes them, without waiting "
                              + "for this.") {
                RLSegmented(options: intervals.map {
                                (value: $0.0, label: $0.1,
                                 help: "Rescan every \($0.1)")
                            },
                            selection: nearestInterval,
                            width: 52,
                            onSelect: { model.write(["pollIntervalSeconds": $0]) })
                Text("Currently every \(Int(model.config.pollIntervalSeconds))s. A value "
                     + "outside these choices can be set in the config file.")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
            }

            SettingsGroup(title: "Menu bar readout") {
                LabeledContent("Shows") {
                    Picker("", selection: model.binding("menuBarDisplay", \.menuBarDisplay)) {
                        Text("Rate limits").tag("limits")
                        Text("Session only").tag("session")
                        Text("Cost today").tag("cost")
                        Text("Tokens today").tag("tokens")
                        Text("Tokens and cost").tag("both")
                    }
                    .labelsHidden()
                    .frame(width: 190)
                }
                LabeledContent("Provider") {
                    Picker("", selection: model.action(\.menuBarProvider,
                                                       model.actions.setMenuBarProvider)) {
                        Text("Nearest limit (any provider)").tag(Config.autoProvider)
                        ForEach(Config.knownProviders, id: \.self) { provider in
                            Text(provider).tag(provider)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 190)
                    .help("Which provider the readout reports. Nearest limit shows whichever "
                          + "is closest to its cap, since that is the one that will "
                          + "interrupt you first.")
                }
                LabeledContent("Windows") {
                    Picker("", selection: model.action(\.limitWindows, model.actions.setLimitWindows)) {
                        Text("All limits").tag("all")
                        Text("Session only").tag("session")
                        Text("Week only").tag("week")
                    }
                    .labelsHidden()
                    .frame(width: 190)
                }
                Divider().overlay(RL.Stroke.hairline)
                SettingRow(title: "Show the RedLine mark",
                           explanation: "The template mark beside the readout. Turning it off "
                               + "leaves the numbers alone in the menu bar.",
                           isOn: model.action(\.showMenuIcon, model.actions.setMenuIcon))
                SettingRow(title: "Show reset times",
                           explanation: "Adds when each window rolls over, next to its "
                               + "percentage.",
                           isOn: model.action(\.showResetTimes, model.actions.setResetTimes))
                SettingRow(title: "Show running agents",
                           explanation: "Lists the Claude Code sessions running on this Mac "
                               + "and marks the menu bar when one is waiting on you. Reads "
                               + "Claude Code's own session registry; nothing is written "
                               + "there.",
                           isOn: model.action(\.agentFleet, model.actions.setAgentFleet))
            }

            SettingsGroup(title: "Start-up") {
                Toggle(isOn: Binding(
                    get: { model.state.launchAtLogin },
                    set: { _ in model.actions.toggleLaunchAtLogin() }
                )) {
                    Text("Launch at login")
                        .font(RL.Typography.body)
                        .foregroundStyle(RL.Ink.primary)
                }
                Text("Managed as a plain LaunchAgent, restarted after a crash but never after "
                     + "you quit.")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
                    .padding(.leading, 22)
            }
        }
    }

    /// The offered interval nearest the configured one, so a hand-edited value still lights
    /// the closest button rather than none at all.
    private var nearestInterval: Double {
        intervals.map(\.0).min {
            abs($0 - model.config.pollIntervalSeconds)
                < abs($1 - model.config.pollIntervalSeconds)
        } ?? 300
    }
}

// MARK: - Limits and alerts

private struct LimitsSettings: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            SettingsGroup(title: "Thresholds",
                          note: "Where a window stops reading as healthy. These drive the "
                              + "rails, the cards, the menu bar colour and the "
                              + "notifications, so they can never disagree.") {
                thresholdRow(title: "Approaching",
                             value: model.binding("limitYellowPct", \.limitYellowPct),
                             status: RLStatus(.approaching),
                             help: "Above this a window is drawn as approaching its limit")
                thresholdRow(title: "At the limit",
                             value: model.binding("limitRedPct", \.limitRedPct),
                             status: RLStatus(.atLimit),
                             help: "Above this a window is drawn as at its limit")
                if model.config.limitYellowPct >= model.config.limitRedPct {
                    RLStateBlock(.error("Approaching sits at or above the limit threshold, so "
                                        + "nothing will ever read as approaching."))
                }
            }

            SettingsGroup(title: "Notifications") {
                SettingRow(title: "Notify at thresholds",
                           explanation: "Posts a notification when a window crosses "
                               + "\(Int(model.config.limitYellowPct))%, "
                               + "\(Int(model.config.limitRedPct))% or 95%, when one is about "
                               + "to run out before it resets, and when one rolls over. "
                               + "Never from a stale reading.",
                           isOn: model.action(\.alerts, model.actions.setAlerts))
                Divider().overlay(RL.Stroke.hairline)
                SettingAction(title: "Notification Style…",
                              explanation: "Opens System Settings. Banners disappear on their "
                                  + "own; Alerts stay until you dismiss them. How long a "
                                  + "banner lasts is macOS's to decide, not RedLine's.",
                              action: model.actions.openNotificationSettings)
            }

            SettingsGroup(title: "How the day is going",
                          note: "Counted from timestamps, stated once, never a sound and "
                              + "never advice.") {
                SettingRow(title: "Say how the day is going",
                           explanation: "Says when a run has gone "
                               + "\(Int(model.config.stretchMinutes)) minutes without a "
                               + "break, when you are still going after "
                               + "\(model.config.lateHour):00, and when "
                               + "\(model.config.streakDays) days have run together.",
                           isOn: model.action(\.mindfulCues, model.actions.setCues))
                if model.config.mindfulCues {
                    Divider().overlay(RL.Stroke.hairline)
                    LabeledContent("Long run") {
                        Stepper(value: model.binding("stretchMinutes", \.stretchMinutes),
                                in: 15...600, step: 15) {
                            Text("\(Int(model.config.stretchMinutes)) minutes")
                                .font(RL.Typography.mono)
                                .foregroundStyle(RL.Ink.primary)
                        }
                    }
                    LabeledContent("Late after") {
                        Stepper(value: model.binding("lateHour", \.lateHour),
                                in: 18...23) {
                            Text(String(format: "%02d:00", model.config.lateHour))
                                .font(RL.Typography.mono)
                                .foregroundStyle(RL.Ink.primary)
                        }
                    }
                    LabeledContent("Days in a row") {
                        Stepper(value: model.binding("streakDays", \.streakDays),
                                in: 2...90) {
                            Text("\(model.config.streakDays) days")
                                .font(RL.Typography.mono)
                                .foregroundStyle(RL.Ink.primary)
                        }
                    }
                }
            }
        }
    }

    private func thresholdRow(title: String, value: Binding<Double>, status: RLStatus,
                              help: String) -> some View {
        VStack(alignment: .leading, spacing: RL.Space.xs) {
            HStack(spacing: RL.Space.md) {
                RLStatusIndicator(status, size: 12)
                Text(title)
                    .font(RL.Typography.body)
                    .foregroundStyle(RL.Ink.primary)
                    .frame(width: 106, alignment: .leading)
                Slider(value: value, in: 5...100, step: 5)
                    .frame(maxWidth: 260)
                    // The system accent would put a blue track above an amber or red rail
                    // showing the same threshold
                    .tint(status.color)
                    .help(help)
                Text("\(Int(value.wrappedValue))%")
                    .font(RL.Typography.mono)
                    .foregroundStyle(status.color)
                    .frame(width: 46, alignment: .trailing)
                    .contentTransition(.numericText())
            }
            RLUsageRail(utilization: value.wrappedValue, status: status, height: 6,
                        showsLimit: true)
                .frame(maxWidth: 420)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(title) threshold")
        .accessibilityValue("\(Int(value.wrappedValue)) percent")
    }
}

// MARK: - Appearance

private struct AppearanceSettings: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            SettingsGroup(title: "Dashboard appearance",
                          note: "Auto follows the system. The menu bar always follows the "
                              + "system, because macOS owns how a menu is drawn.") {
                RLSegmented(options: [("auto", "Auto", "Follow the system appearance"),
                                      ("light", "Light", "Always light"),
                                      ("dark", "Dark", "Always dark")]
                                .map { (value: $0.0, label: $0.1, help: $0.2) },
                            selection: model.config.dashboardTheme,
                            width: 60,
                            onSelect: { model.actions.setTheme($0); model.reload() })
            }

            SettingsGroup(title: "Provider colours",
                          note: "RedLine's own accents, used on the chip, dot, rail and chart "
                              + "series around each provider's mark. The marks themselves "
                              + "stay monochrome.") {
                ForEach(Config.knownProviders, id: \.self) { provider in
                    HStack(spacing: RL.Space.lg) {
                        ProviderBadge.forProvider(provider, size: 14)
                        RLUsageRail(utilization: 72,
                                    status: RLStatus(.healthy), height: 8,
                                    showsLimit: false,
                                    tint: ProviderIdentity.accent(for: provider))
                            .frame(maxWidth: 260)
                        Spacer(minLength: 0)
                    }
                    .accessibilityLabel("\(provider) accent colour")
                }
                Text("Checked for contrast on both appearances, for one lightness band and "
                     + "hue separation between providers, and for separability under "
                     + "protanopia, deuteranopia and tritanopia. Status is never carried by "
                     + "colour alone.")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// MARK: - Data and privacy

private struct DataSettings: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            SettingsGroup(title: "What is kept on disk") {
                SettingRow(title: "Keep local history",
                           explanation: "Rolls each day up into "
                               + "~/.local/share/redline/history so your own numbers outlive "
                               + "Claude Code's 30 day transcript cleanup. Local file, no "
                               + "network.",
                           isOn: model.action(\.recordHistory, model.actions.setHistory))
                Divider().overlay(RL.Stroke.hairline)
                SettingRow(title: "Publish the usage sidecar",
                           explanation: "Writes the current windows to "
                               + "~/.local/share/redline/usage-snapshot.json in the shape "
                               + "other local tools already read. Nothing leaves this Mac.",
                           isOn: model.action(\.publishSidecar, model.actions.setSidecar))
                Divider().overlay(RL.Stroke.hairline)
                SettingRow(title: "Scan for setup findings",
                           explanation: "Looks through transcripts in the background for "
                               + "things worth changing about how Claude Code is set up. At "
                               + "most once every few hours, never on the main thread.",
                           isOn: model.binding("findingsScans", \.findingsScans))
                if model.config.findingsScans {
                    LabeledContent("Hide a finding for") {
                        Stepper(value: model.binding("findingsSnoozeDays",
                                                     \.findingsSnoozeDays),
                                in: 1...365) {
                            Text("\(model.config.findingsSnoozeDays) days")
                                .font(RL.Typography.mono)
                                .foregroundStyle(RL.Ink.primary)
                        }
                        .help("A dismissed finding returns after this if it is still true, "
                              + "because silently dropping something real is worse than "
                              + "repeating it.")
                    }
                }
            }

            SettingsGroup(title: "What leaves this Mac",
                          note: "RedLine reads local files by default. These are the only two "
                              + "settings that make a network request, and both are listed "
                              + "here rather than buried.") {
                SettingRow(title: "Check service status pages",
                           explanation: "Polls the providers' public status pages every 15 "
                               + "minutes. Off by default.",
                           isOn: model.action(\.statusChecks, model.actions.setStatusChecks))
                Divider().overlay(RL.Stroke.hairline)
                SettingRow(title: "Check for updates daily",
                           explanation: "One call a day to the GitHub releases API, and it "
                               + "speaks up only when an update exists. This is the one "
                               + "request RedLine makes without being asked; switch it off "
                               + "and updates are yours to check for.",
                           isOn: model.action(\.autoCheckUpdates, model.actions.setAutoUpdates))
            }

            SettingsGroup(title: "Permissions") {
                if model.state.fullDiskAccess {
                    HStack(spacing: RL.Space.md) {
                        RLStatusIndicator(RLStatus(.healthy, phrase: "Full Disk Access is on"),
                                          size: 13, showsLabel: true)
                    }
                    Text("The one grant macOS remembers, so RedLine stops asking for "
                         + "permission to read transcripts.")
                        .font(RL.Typography.caption)
                        .foregroundStyle(RL.Ink.muted)
                } else {
                    SettingAction(title: "Stop Permission Prompts…",
                                  explanation: "Full Disk Access is the one grant macOS "
                                      + "remembers. Without it, reading transcripts can ask "
                                      + "again after an update.",
                                  action: model.actions.grantFullDiskAccess)
                }
            }

            SettingsGroup(title: "The files themselves") {
                SettingAction(title: "Open the Data Folder…",
                              explanation: "~/.local/share/redline, where history, the "
                                  + "sidecar and the usage feed are written.",
                              action: model.actions.openDataFolder)
                Divider().overlay(RL.Stroke.hairline)
                SettingAction(title: "Edit the Config File…",
                              explanation: "The raw config.json. Everything in this window "
                                  + "edits the same file.",
                              action: model.actions.editConfig)
                Divider().overlay(RL.Stroke.hairline)
                SettingAction(title: "Uninstall RedLine…",
                              explanation: "Removes the app, the LaunchAgent and the Keychain "
                                  + "token. Asks what to do with your config and history "
                                  + "first.",
                              isDestructive: true,
                              action: model.actions.uninstall)
            }
        }
    }
}

// MARK: - About

private struct AboutSettings: View {
    @ObservedObject var model: SettingsModel

    /// The bundled notice files. Retained with the glyphs they describe, and reachable from
    /// the app rather than only from the repository.
    private let notices = [
        ("Third-party notices", "THIRD_PARTY_NOTICES.md",
         "Which provider mark came from where, and the brand guidance for each"),
        ("Simple Icons licence", "LICENSE-simple-icons.md",
         "CC0-1.0, covering the Anthropic, Claude and Ollama marks"),
        ("Bootstrap Icons licence", "LICENSE-bootstrap-icons.txt",
         "MIT, covering the OpenAI blossom used for Codex"),
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
            RLCard {
                HStack(alignment: .center, spacing: RL.Space.xl) {
                    RedlineMarkAdaptive(size: 44)
                    VStack(alignment: .leading, spacing: RL.Space.xxs) {
                        Text("RedLine")
                            .font(RL.Typography.title)
                            .tracking(-0.7)
                            .foregroundStyle(RL.Ink.primary)
                        Text("Know your limit.")
                            .font(RL.Typography.mono)
                            .tracking(0.8)
                            .foregroundStyle(RL.Ink.muted)
                        if !model.state.appVersion.isEmpty {
                            Text("Version \(model.state.appVersion)")
                                .font(RL.Typography.monoSmall)
                                .foregroundStyle(RL.Ink.secondary)
                        }
                        Link("github.com/goriparthi/redline",
                             destination: Updates.repoURL)
                            .font(RL.Typography.monoSmall)
                            .foregroundStyle(RL.Accent.codex)
                            .help("Source, releases and issues. Opens in your browser.")
                            .padding(.top, RL.Space.xxs)
                    }
                    Spacer(minLength: 0)
                }
            }

            SettingsGroup(title: "Updates") {
                LabeledContent("Channel") {
                    Picker("", selection: model.action(\.updateChannel, model.actions.setUpdateChannel)) {
                        Text("Stable releases").tag("stable")
                        Text("Beta releases").tag("beta")
                    }
                    .labelsHidden()
                    .frame(width: 190)
                    .help("Beta also offers prerelease builds. Newest always wins, so a "
                          + "stable release still reaches you the moment it outranks them.")
                }
                Divider().overlay(RL.Stroke.hairline)
                SettingAction(title: "Check for Updates…",
                              explanation: "Asks the GitHub releases API now.",
                              action: model.actions.checkForUpdates)
            }

            SettingsGroup(title: "Provider marks",
                          note: "The marks below identify each provider and are not RedLine "
                              + "branding. They do not imply sponsorship or endorsement, and "
                              + "open-source licensing of the vector data does not waive "
                              + "trademark restrictions.") {
                HStack(spacing: RL.Space.xl) {
                    ForEach(Config.knownProviders, id: \.self) { provider in
                        ProviderBadge.forProvider(provider, size: 15)
                    }
                    Spacer(minLength: 0)
                }
                Divider().overlay(RL.Stroke.hairline)
                ForEach(notices, id: \.1) { title, file, blurb in
                    SettingAction(title: title, explanation: blurb) {
                        model.actions.openNotice(file)
                    }
                }
            }
        }
    }
}
