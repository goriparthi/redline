// The design system: every colour, space, radius and text style the UI is allowed to use.
// Values are validated by claude_scripts/redline_palette_check.py for contrast, lightness
// band, hue separation and colour-vision separation in both appearances.
import SwiftUI

/// Namespace for the tokens. Short on purpose, because it is read at every call site.
public enum RL {}

public extension RL {
    /// Resolves a token per appearance. Dark is the design's home; light is a real
    /// equivalent, not an afterthought, because the dashboard follows the OS.
    static func dynamic(dark: BrandColor, light: BrandColor) -> Color {
        Color(nsColor: nsDynamic(dark: dark, light: light))
    }

    /// The AppKit twin, for menus and status items, which are drawn by AppKit and cannot
    /// take a SwiftUI colour.
    static func nsDynamic(dark: BrandColor, light: BrandColor) -> NSColor {
        NSColor(name: nil) { appearance in
            let c = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: c.red, green: c.green, blue: c.blue, alpha: 1)
        }
    }
}

// MARK: - Surfaces

public extension RL {
    /// Background layers, darkest to lightest in dark mode and the reverse in light.
    /// Depth comes from these plus a hairline border, never from a heavy drop shadow.
    enum Surface {
        /// The window's own ground.
        public static let ground = RL.dynamic(dark: BrandColor(0x0B0D10),
                                              light: BrandColor(0xF4F1EA))
        /// Cards and panels sitting on the ground.
        public static let raised = RL.dynamic(dark: BrandColor(0x171A1F),
                                              light: BrandColor(0xFFFFFF))
        /// Wells inside a card: chart grounds, evidence blocks, progress tracks.
        public static let sunken = RL.dynamic(dark: BrandColor(0x07090B),
                                              light: BrandColor(0xE8E4DB))
        /// Popovers and hover readouts, which sit above everything else.
        public static let overlay = RL.dynamic(dark: BrandColor(0x1D2128),
                                               light: BrandColor(0xFFFFFF))
        /// A card the pointer is over. One step, not a colour change.
        public static let raisedHover = RL.dynamic(dark: BrandColor(0x1D2128),
                                                   light: BrandColor(0xFAF8F3))
    }

    /// Text. Three weights of emphasis is the whole vocabulary; a fourth would stop meaning
    /// anything.
    enum Ink {
        public static let primary = RL.dynamic(dark: BrandColor(0xF4F1EA),
                                                light: BrandColor(0x14171C))
        public static let secondary = RL.dynamic(dark: BrandColor(0xA8AEBA),
                                                  light: BrandColor(0x4E5462))
        public static let muted = RL.dynamic(dark: BrandColor(0x848A96),
                                              light: BrandColor(0x6B7280))
        /// On top of a filled brand or state colour.
        public static let onAccent = RL.dynamic(dark: BrandColor(0x0B0D10),
                                                 light: BrandColor(0xFFFFFF))

        public static let nsPrimary = RL.nsDynamic(dark: BrandColor(0xF4F1EA),
                                                   light: BrandColor(0x14171C))
        public static let nsSecondary = RL.nsDynamic(dark: BrandColor(0xA8AEBA),
                                                     light: BrandColor(0x4E5462))
        public static let nsMuted = RL.nsDynamic(dark: BrandColor(0x848A96),
                                                 light: BrandColor(0x6B7280))
    }

    /// Borders and separators. A card is a fill plus one of these; that pairing is what
    /// carries elevation without shadow soup.
    enum Stroke {
        /// Separators inside a card.
        public static let hairline = RL.dynamic(dark: BrandColor(0x262A32),
                                                 light: BrandColor(0xDDD8CE))
        /// A card's own edge.
        public static let border = RL.dynamic(dark: BrandColor(0x323843),
                                               light: BrandColor(0xC7C2B7))
        /// A card under the pointer, or one carrying a selection.
        public static let borderStrong = RL.dynamic(dark: BrandColor(0x4A515F),
                                                     light: BrandColor(0xA9A398))
    }
}

// MARK: - State

public extension RL {
    /// Status colour is always paired with a shape and a word, so nothing here is the only
    /// carrier of meaning.
    enum State {
        public static let success = RL.dynamic(dark: BrandColor(0x32D74B),
                                                light: BrandColor(0x1E8E3E))
        public static let warning = RL.dynamic(dark: BrandColor(0xFF9F0A),
                                                light: BrandColor(0xA85C00))
        public static let error = RL.dynamic(dark: BrandColor(0xFF3B30),
                                              light: BrandColor(0xC9271D))
        /// A provider that is installed but not answering.
        public static let offline = RL.dynamic(dark: BrandColor(0x848A96),
                                                light: BrandColor(0x6B7280))
        /// Nothing has been checked, so nothing is claimed.
        public static let unknown = RL.dynamic(dark: BrandColor(0x6E7480),
                                                light: BrandColor(0x7A8090))

        public static let nsSuccess = RL.nsDynamic(dark: BrandColor(0x32D74B),
                                                    light: BrandColor(0x1E8E3E))
        public static let nsWarning = RL.nsDynamic(dark: BrandColor(0xFF9F0A),
                                                    light: BrandColor(0xA85C00))
        public static let nsError = RL.nsDynamic(dark: BrandColor(0xFF3B30),
                                                  light: BrandColor(0xC9271D))
        public static let nsOffline = RL.nsDynamic(dark: BrandColor(0x848A96),
                                                   light: BrandColor(0x6B7280))
        public static let nsUnknown = RL.nsDynamic(dark: BrandColor(0x6E7480),
                                                   light: BrandColor(0x7A8090))
    }

    /// RedLine's own identity. The red rule is the product's signature and stays the
    /// strongest colour on any screen; provider accents are quieter than it by construction.
    enum Brandmark {
        public static let signal = RL.dynamic(dark: BrandColor(0xFF3B30),
                                               light: BrandColor(0xC9271D))
        public static let nsSignal = RL.nsDynamic(dark: BrandColor(0xFF3B30),
                                                   light: BrandColor(0xC9271D))
        /// Money reads green by convention, tuned per appearance so it does not wash out.
        public static let money = RL.dynamic(dark: BrandColor(0x32D74B),
                                              light: BrandColor(0x1E8E3E))
    }
}

// MARK: - Metrics

public extension RL {
    /// A 2pt grid. Anything not on it is a one-off, which is what this file exists to stop.
    enum Space {
        public static let xxs: CGFloat = 2
        public static let xs: CGFloat = 4
        public static let sm: CGFloat = 6
        public static let md: CGFloat = 8
        public static let lg: CGFloat = 12
        public static let xl: CGFloat = 16
        public static let xxl: CGFloat = 22
        public static let xxxl: CGFloat = 32
    }

    enum Radius {
        public static let chip: CGFloat = 5
        public static let control: CGFloat = 7
        public static let card: CGFloat = 12
        public static let window: CGFloat = 16
    }

    /// One typographic scale. Numbers are monospaced everywhere so a column of them lines up
    /// and a changing digit does not shift the ones beside it.
    enum Typography {
        public static let display = Font.system(size: 25, weight: .semibold, design: .monospaced)
        public static let title = Font.system(size: 24, weight: .bold)
        public static let heading = Font.system(size: 16, weight: .semibold)
        public static let subheading = Font.system(size: 14, weight: .medium)
        public static let body = Font.system(size: 13)
        public static let caption = Font.system(size: 11)
        /// Section headers: small, tracked, monospaced, upper case.
        public static let label = Font.system(size: 12, weight: .medium, design: .monospaced)
        public static let mono = Font.system(size: 13, design: .monospaced)
        public static let monoSmall = Font.system(size: 11, design: .monospaced)
        public static let monoStrong = Font.system(size: 13, weight: .semibold,
                                                   design: .monospaced)
        /// The tracking that goes with `label`. Upper case without it reads as shouting.
        public static let labelTracking: CGFloat = 1.4
    }

    /// Named durations, so a transition can be tuned in one place and switched off wholesale
    /// when Reduce Motion is on.
    enum Motion {
        /// Hover and selection feedback: fast enough to feel like a direct response.
        public static let hover: Double = 0.12
        /// A rail or a number moving to a new value.
        public static let value: Double = 0.45
        /// Content appearing or being replaced.
        public static let content: Double = 0.22
    }
}

// MARK: - Provider identity

public extension RL {
    /// A provider's RedLine-owned accent. Used on the chip, dot, rail, border and chart
    /// series around a mark, never on the mark itself.
    ///
    /// Validated as a categorical set: one lightness band, separated by hue rather than by
    /// lightness, still separable under protanopia, deuteranopia and tritanopia, and each
    /// kept clear of the signal red so a provider colour can never be read as a warning.
    enum Accent {
        public static let codex = RL.dynamic(dark: BrandColor(0x45C4D4),
                                              light: BrandColor(0x0C6D7C))
        public static let anthropic = RL.dynamic(dark: BrandColor(0xD9A05B),
                                                  light: BrandColor(0x8A6118))
        public static let ollama = RL.dynamic(dark: BrandColor(0x9888D4),
                                               light: BrandColor(0x5F44A6))
        /// Every provider at once, or none named: the product's own neutral.
        public static let neutral = RL.dynamic(dark: BrandColor(0xA8AEBA),
                                                light: BrandColor(0x4E5462))

        public static let nsCodex = RL.nsDynamic(dark: BrandColor(0x45C4D4),
                                                  light: BrandColor(0x0C6D7C))
        public static let nsAnthropic = RL.nsDynamic(dark: BrandColor(0xD9A05B),
                                                      light: BrandColor(0x8A6118))
        public static let nsOllama = RL.nsDynamic(dark: BrandColor(0x9888D4),
                                                   light: BrandColor(0x5F44A6))
        public static let nsNeutral = RL.nsDynamic(dark: BrandColor(0xA8AEBA),
                                                    light: BrandColor(0x4E5462))
    }
}

/// Everything RedLine knows about how to present one provider, in one place: which mark
/// identifies it, which accent surrounds it, and what it is called.
public struct ProviderIdentity: Equatable, Sendable {
    public let name: String
    public let mark: ProviderMark
    /// What the provider reads on this Mac, in one line, for a card subtitle or a tooltip.
    public let blurb: String
    /// Whether usage runs on this machine or against a hosted endpoint.
    public let isLocal: Bool

    public var accent: Color {
        switch mark {
        case .codex:                return RL.Accent.codex
        case .anthropic, .claude:   return RL.Accent.anthropic
        case .ollama:               return RL.Accent.ollama
        }
    }

    public var nsAccent: NSColor {
        switch mark {
        case .codex:                return RL.Accent.nsCodex
        case .anthropic, .claude:   return RL.Accent.nsAnthropic
        case .ollama:               return RL.Accent.nsOllama
        }
    }

    /// Provider identity for a provider name, or nil when no provider is named. The mapping
    /// is deliberate: "Claude" is a provider track in RedLine's data, and the provider-level
    /// mark is Anthropic's, so the Claude sparkle is reserved for naming the product itself.
    public static func of(_ provider: String?) -> ProviderIdentity? {
        guard let provider else { return nil }
        switch provider.lowercased() {
        case "claude", "anthropic":
            return ProviderIdentity(
                name: provider.lowercased() == "anthropic" ? "Anthropic" : "Claude",
                mark: .anthropic,
                blurb: "Tokens and cost from transcripts on disk, plus rate-limit windows",
                isLocal: false)
        case "codex", "openai":
            return ProviderIdentity(name: "Codex", mark: .codex,
                                    blurb: "Limits and tokens, read entirely from disk",
                                    isLocal: false)
        case "ollama":
            return ProviderIdentity(name: "Ollama", mark: .ollama,
                                    blurb: "Local models, counted once tracking is set up",
                                    isLocal: true)
        default:
            return nil
        }
    }

    /// The accent for a provider name, falling back to the product's neutral. Kept as a
    /// function so a caller with only a string does not have to unwrap an identity.
    public static func accent(for provider: String?) -> Color {
        of(provider)?.accent ?? RL.Accent.neutral
    }

    public static func nsAccent(for provider: String?) -> NSColor {
        of(provider)?.nsAccent ?? RL.Accent.nsNeutral
    }
}
