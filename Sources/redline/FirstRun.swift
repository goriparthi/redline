// Shown once, on the first launch, so the user chooses what RedLine reads rather than having
// every provider switched on by default. Reopened from the menu, it shows the current choices
// rather than resetting them. Nothing here reaches the network; a browser sign-in the user
// asks for is started by the caller after Start.
import RedlineCore
import SwiftUI

// Where the Claude rate-limit percentages come from, if anywhere
enum ClaudeLimitsChoice {
    case off        // percentages stay hidden; everything else still works
    case feed       // the statusline usage feed: no credentials, updates while Claude Code runs
    case cliToken   // read (never refresh) the Claude Code CLI's Keychain token
    case browser    // RedLine's own OAuth sign-in, live between sessions and for claude.ai users
}

struct FirstRunView: View {
    let availability: ProviderAvailability
    @State private var selection: Set<String>
    @State private var limitsChoice: ClaudeLimitsChoice
    @State private var clientId: String
    let onDone: (_ providers: [String], _ choice: ClaudeLimitsChoice, _ clientId: String) -> Void

    init(availability: ProviderAvailability,
         currentProviders: [String] = [],
         useCLIToken: Bool = false,
         oauthClientId: String = "",
         feedInstalled: Bool = false,
         signedIn: Bool = false,
         onDone: @escaping (_ providers: [String], _ choice: ClaudeLimitsChoice,
                            _ clientId: String) -> Void) {
        self.availability = availability
        self.onDone = onDone
        // First run pre-selects everything found ("read what I have"); reopened later it
        // reflects the config, so Start never silently changes an existing choice
        let current = currentProviders.filter { availability.has($0) }
        _selection = State(initialValue: current.isEmpty ? Set(availability.installed)
                                                         : Set(current))
        // Reopened, the window reflects what is actually running; on a true first run
        // nothing is set up yet, so the recommended zero-credential route is preselected.
        let initial: ClaudeLimitsChoice = feedInstalled ? .feed
            : useCLIToken ? .cliToken
            : signedIn ? .browser
            : .feed
        _limitsChoice = State(initialValue: initial)
        _clientId = State(initialValue: oauthClientId)
    }

    private var claudeFound: Bool { availability.has("Claude") }
    private var trimmedClientId: String {
        clientId.trimmingCharacters(in: .whitespacesAndNewlines)
    }
    private var startDisabled: Bool {
        if availability.isEmpty { return false }
        if selection.isEmpty { return true }
        // A browser sign-in with no client id is a dead end, so refuse it here
        return limitsChoice == .browser && trimmedClientId.isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 11) {
                RedlineMark(size: 34)
                VStack(alignment: .leading, spacing: 2) {
                    Text("RedLine")
                        .font(.system(size: 22, weight: .bold))
                        .foregroundStyle(BrandUI.chalk)
                    Text("Know your limit.")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(BrandUI.steel)
                }
            }
            Rectangle().fill(BrandUI.signal).frame(width: 120, height: 3).clipShape(Capsule())

            if availability.isEmpty {
                Text("No supported tool found")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(BrandUI.chalk)
                Text("""
                     RedLine reads Claude Code, Codex, and Ollama. None of them appear to be \
                     installed for this user, so there is nothing to report yet. Install one \
                     and reopen RedLine.
                     """)
                    .font(.system(size: 13))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("What should RedLine read?")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(BrandUI.chalk)
                Text("""
                     Everything is read from files already on this Mac. You can change this \
                     later from the menu.
                     """)
                    .font(.system(size: 13))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 10) {
                    ForEach(availability.installed, id: \.self) { provider in
                        Toggle(isOn: Binding(
                            get: { selection.contains(provider) },
                            set: { on in
                                if on { selection.insert(provider) }
                                else { selection.remove(provider) }
                            }
                        )) {
                            HStack(spacing: 8) {
                                TrackBadge(provider: provider, size: 20)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(provider)
                                        .font(.system(size: 13, weight: .medium))
                                        .foregroundStyle(BrandUI.chalk)
                                    Text(note(for: provider))
                                        .font(.system(size: 11))
                                        .foregroundStyle(BrandUI.steel)
                                }
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                }
                .padding(.vertical, 2)

                if claudeFound && selection.contains("Claude") {
                    Divider().overlay(BrandUI.steel.opacity(0.3))
                    limitsSection
                }
            }

            HStack {
                // Starting with nothing ticked would silently keep the old choice, since an
                // empty list means "read everything" further down
                if !availability.isEmpty && selection.isEmpty {
                    Text("Pick at least one")
                        .font(.system(size: 11))
                        .foregroundStyle(BrandUI.signal)
                }
                Spacer()
                Button(availability.isEmpty ? "Close" : "Start") {
                    onDone(Array(selection).sorted(), limitsChoice,
                           limitsChoice == .browser ? trimmedClientId : clientId)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(startDisabled)
            }
        }
        .padding(22)
        .frame(width: 470)
        .background(BrandUI.carbon)
        // This window paints Carbon in every OS theme, so dynamic colors resolve as dark
        .colorScheme(.dark)
    }

    // Where the percentages come from is a decision, not a default, and each route carries
    // its own honest cost. The feed is recommended because it has none: it reads data Claude
    // Code already hands to its statusline, a documented feature, with no credentials at all.
    private var limitsSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Claude rate-limit percentages")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(BrandUI.chalk)
            Text("""
                 The same session and week percentages /usage shows. Everything else works \
                 with this off.
                 """)
                .font(.system(size: 11))
                .foregroundStyle(BrandUI.steel)
                .fixedSize(horizontal: false, vertical: true)

            Picker("", selection: $limitsChoice) {
                Text("Read the statusline usage feed (recommended)")
                    .tag(ClaudeLimitsChoice.feed)
                Text("Sign in with your Claude account").tag(ClaudeLimitsChoice.browser)
                Text("Use the Claude Code CLI's token").tag(ClaudeLimitsChoice.cliToken)
                Text("Don't show them").tag(ClaudeLimitsChoice.off)
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()

            switch limitsChoice {
            case .off:
                EmptyView()
            case .feed:
                Text("""
                     Claude Code hands its statusline command the rate-limit windows; RedLine \
                     installs a small wrapper that writes them to disk. A statusline you \
                     already have is kept and still draws the line. No sign-in, no Keychain, \
                     no network. The figures update while Claude Code runs and are shown \
                     greyed with their age in between.
                     """)
                    .font(.system(size: 11))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)
            case .cliToken:
                Text("""
                     Needs Claude Code installed and signed in. Reads its token from your \
                     Keychain and only ever reads it, never refreshes it, so it cannot sign \
                     the CLI out. The token is used against an undocumented endpoint, which \
                     may fall outside Anthropic's terms; that call is yours. macOS may ask \
                     for Keychain consent again after Claude Code rewrites the item.
                     """)
                    .font(.system(size: 11))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)
            case .browser:
                Text("""
                     RedLine's own sign-in, separate from Claude Code's, so the percentages \
                     stay live between sessions and it works for claude.ai users with no CLI \
                     at all. Needs an OAuth client id, which Anthropic does not issue to \
                     third-party apps; the endpoint is undocumented and using it may fall \
                     outside their terms, which is your call to make.
                     """)
                    .font(.system(size: 11))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)
                TextField("OAuth client id", text: $clientId)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 11, design: .monospaced))
            }
        }
    }

    private func note(for provider: String) -> String {
        switch provider {
        case "Claude": return "Tokens and cost from transcripts on disk"
        case "Codex":  return "Limits and tokens, read entirely from disk"
        case "Ollama": return "Local models, plus token counts once tracking is set up"
        default:       return ""
        }
    }
}
