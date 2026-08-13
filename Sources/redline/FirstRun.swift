// Shown once, on the first launch, so the user chooses what Redline reads rather than having
// every provider switched on by default. Nothing here reaches the network.
import RedlineCore
import SwiftUI

struct FirstRunView: View {
    let availability: ProviderAvailability
    @State private var selection: Set<String>
    @State private var useCLIToken = false
    let onDone: (_ providers: [String], _ useCLIToken: Bool) -> Void

    init(availability: ProviderAvailability,
         onDone: @escaping (_ providers: [String], _ useCLIToken: Bool) -> Void) {
        self.availability = availability
        self.onDone = onDone
        // Everything found is pre-selected: the common case is "read what I have"
        _selection = State(initialValue: Set(availability.installed))
    }

    private var claudeFound: Bool { availability.has("Claude") }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 11) {
                RedlineMark(size: 34)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Redline")
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
                     Redline reads Claude Code, Codex, and Ollama. None of them appear to be \
                     installed for this user, so there is nothing to report yet. Install one \
                     and reopen Redline.
                     """)
                    .font(.system(size: 13))
                    .foregroundStyle(BrandUI.steel)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("What should Redline read?")
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

                if claudeFound {
                    Divider().overlay(BrandUI.steel.opacity(0.3))
                    Toggle(isOn: $useCLIToken) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Also show Claude rate-limit percentages")
                                .font(.system(size: 13, weight: .medium))
                                .foregroundStyle(BrandUI.chalk)
                            // The honest version, not a euphemism
                            Text("""
                                 Reads the Claude CLI's own token from your Keychain and calls \
                                 an undocumented Anthropic endpoint. There is no published \
                                 permission for that, so it may fall outside their terms. \
                                 Leave this off and everything else still works.
                                 """)
                                .font(.system(size: 11))
                                .foregroundStyle(BrandUI.steel)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .toggleStyle(.checkbox)
                }
            }

            HStack {
                Spacer()
                Button(availability.isEmpty ? "Close" : "Start") {
                    onDone(Array(selection).sorted(), useCLIToken)
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 470)
        .background(BrandUI.carbon)
    }

    private func note(for provider: String) -> String {
        switch provider {
        case "Claude": return "Tokens and cost from transcripts on disk"
        case "Codex":  return "Limits and tokens, read entirely from disk"
        case "Ollama": return "Local models, and calls made through the bundled wrapper"
        default:       return ""
        }
    }
}
