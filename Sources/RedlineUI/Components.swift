// The shared component set. Every card, chip, tile, rail, dot and placeholder in the app
// and the widget comes from here, so styling is defined once rather than at each call site.
import SwiftUI
import RedlineCore

// MARK: - Status presentation

/// How a status looks. Split from the status itself so RedlineCore carries no colour and no
/// SF Symbol name; a non-AppKit shell maps the same kinds to its own iconography.
public extension RLStatus {
    var color: Color {
        switch kind {
        case .healthy:     return RL.State.success
        case .approaching: return RL.State.warning
        case .atLimit:     return RL.State.error
        case .offline:     return RL.State.offline
        case .unknown:     return RL.State.unknown
        case .stale:       return RL.State.offline
        }
    }

    /// A distinct shape per state, so the difference survives greyscale and colour blindness.
    var symbol: String {
        switch kind {
        case .healthy:     return "checkmark.circle.fill"
        case .approaching: return "exclamationmark.triangle.fill"
        case .atLimit:     return "exclamationmark.octagon.fill"
        case .offline:     return "bolt.slash.circle.fill"
        case .unknown:     return "questionmark.circle.fill"
        case .stale:       return "clock.badge.exclamationmark.fill"
        }
    }

}

/// A status as a glyph, optionally with its words beside it. The label is what keeps status
/// off colour alone; hiding it is only allowed where the words sit adjacent already.
public struct RLStatusIndicator: View {
    private let status: RLStatus
    private let size: CGFloat
    private let showsLabel: Bool

    public init(_ status: RLStatus, size: CGFloat = 13, showsLabel: Bool = false) {
        self.status = status
        self.size = size
        self.showsLabel = showsLabel
    }

    public var body: some View {
        HStack(spacing: RL.Space.sm) {
            Image(systemName: status.symbol)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(status.color)
            if showsLabel {
                Text(status.phrase)
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.secondary)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(status.phrase)
    }
}

// MARK: - Cards

/// The one card in the system: a raised fill, a hairline edge, and a hover step. Depth is
/// carried by the border rather than by a shadow, which keeps a wall of cards calm.
public struct RLCard<Content: View>: View {
    private let accent: Color?
    private let interactive: Bool
    private let selected: Bool
    private let padding: CGFloat
    private let content: Content

    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// - Parameters:
    ///   - accent: tints the edge and the top rule. A provider's accent goes here; the mark
    ///     itself stays monochrome.
    ///   - interactive: adds the hover step. Only for a card that actually does something.
    public init(accent: Color? = nil, interactive: Bool = false, selected: Bool = false,
                padding: CGFloat = RL.Space.xl, @ViewBuilder content: () -> Content) {
        self.accent = accent
        self.interactive = interactive
        self.selected = selected
        self.padding = padding
        self.content = content()
    }

    private var edge: Color {
        if selected { return accent ?? RL.Stroke.borderStrong }
        if hovering && interactive { return RL.Stroke.borderStrong }
        return RL.Stroke.hairline
    }

    private var fill: Color {
        hovering && interactive ? RL.Surface.raisedHover : RL.Surface.raised
    }

    public var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(fill, in: RoundedRectangle(cornerRadius: RL.Radius.card,
                                                   style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: RL.Radius.card, style: .continuous)
                    .strokeBorder(edge, lineWidth: selected ? 1.5 : 1)
            )
            .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.hover),
                       value: hovering)
            .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.hover),
                       value: selected)
            .onHover { hovering = $0 && interactive }
    }
}

/// A section header: small, tracked, quiet, with room for a note on the right. Used for every
/// panel in the app so no two sections announce themselves differently.
public struct RLSectionHeader<Trailing: View>: View {
    private let title: String
    private let note: String?
    private let trailing: Trailing

    public init(_ title: String, note: String? = nil,
                @ViewBuilder trailing: () -> Trailing = { EmptyView() }) {
        self.title = title
        self.note = note
        self.trailing = trailing()
    }

    public var body: some View {
        HStack(alignment: .center, spacing: RL.Space.md) {
            Text(title.uppercased())
                .font(RL.Typography.label)
                .tracking(RL.Typography.labelTracking)
                .foregroundStyle(RL.Ink.muted)
            if let note {
                Text(note)
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
                    .lineLimit(1)
            }
            Spacer(minLength: RL.Space.md)
            trailing
        }
        .accessibilityAddTraits(.isHeader)
    }
}

// MARK: - Provider badge

/// A provider mark inside a RedLine-owned chip, with the provider's name beside it. This is
/// the only sanctioned way to show a mark where the provider is not otherwise obvious: the
/// tint is on the chip, the mark stays monochrome, and the name is always present.
public struct ProviderBadge: View {
    private let identity: ProviderIdentity
    private let size: CGFloat
    private let showsLabel: Bool

    public init(_ identity: ProviderIdentity, size: CGFloat = 15, showsLabel: Bool = true) {
        self.identity = identity
        self.size = size
        self.showsLabel = showsLabel
    }

    /// Nil-safe form for callers holding only a provider name; falls back to the RedLine mark
    /// when the name is not a provider RedLine knows.
    @ViewBuilder
    public static func forProvider(_ provider: String?, size: CGFloat = 15,
                                   showsLabel: Bool = true) -> some View {
        if let identity = ProviderIdentity.of(provider) {
            ProviderBadge(identity, size: size, showsLabel: showsLabel)
        } else {
            ProviderTile(provider: provider, size: size + 7)
        }
    }

    public var body: some View {
        HStack(spacing: RL.Space.sm) {
            ProviderGlyph(identity.mark, size: size, decorative: showsLabel)
                .foregroundStyle(identity.accent)
            if showsLabel {
                Text(identity.name)
                    .font(.system(size: size * 0.82, weight: .medium))
                    .foregroundStyle(RL.Ink.primary)
            }
        }
        .padding(.horizontal, RL.Space.md)
        .padding(.vertical, RL.Space.xs)
        .background(identity.accent.opacity(0.13),
                    in: Capsule())
        .overlay(Capsule().strokeBorder(identity.accent.opacity(0.35), lineWidth: 1))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(identity.name)
    }
}

/// A provider mark in a small tinted tile, for a row that has no room for a full chip. The
/// name always sits beside it in the row itself, which is what keeps the mark identifying
/// rather than decorating.
public struct ProviderTile: View {
    private let identity: ProviderIdentity?
    private let size: CGFloat

    public init(provider: String?, size: CGFloat = 22) {
        self.identity = ProviderIdentity.of(provider)
        self.size = size
    }

    private var tint: Color { identity?.accent ?? RL.Accent.neutral }
    private var inset: CGFloat { size * 0.26 }

    public var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .fill(tint.opacity(0.15))
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .strokeBorder(tint.opacity(0.4), lineWidth: max(1, size * 0.045))
            if let identity {
                ProviderGlyph(identity.mark, size: size - inset * 2)
                    .foregroundStyle(tint)
            } else {
                // No provider named means all of them, which is RedLine's own mark
                RedlineMark(size: size - inset * 2)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

// MARK: - Metric tile

/// One number with its label and, where there is something honest to add, a note under it.
/// A tile never invents a figure: an absent value is drawn as an em-free dash.
public struct RLMetricTile: View {
    private let label: String
    private let value: String
    private let note: String?
    private let tint: Color?
    private let help: String?

    public init(label: String, value: String, note: String? = nil, tint: Color? = nil,
                help: String? = nil) {
        self.label = label
        self.value = value
        self.note = note
        self.tint = tint
        self.help = help
    }

    public var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xxs) {
            Text(label.uppercased())
                .font(RL.Typography.label)
                .tracking(RL.Typography.labelTracking)
                .foregroundStyle(RL.Ink.muted)
                .lineLimit(1)
            Text(value)
                .font(RL.Typography.display)
                .foregroundStyle(tint ?? RL.Ink.primary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            if let note {
                Text(note)
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
                    .lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .modifier(OptionalHelp(help))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(label): \(value)" + (note.map { ", \($0)" } ?? ""))
    }
}

/// `.help` only when there is something to say, so an empty tooltip never appears.
struct OptionalHelp: ViewModifier {
    let text: String?
    init(_ text: String?) { self.text = text }

    func body(content: Content) -> some View {
        if let text { content.help(text) } else { content }
    }
}

// MARK: - Rails

/// A usage rail that ends at its limit: the red line at the right edge is the product's
/// signature, and the fill's colour is the status.
///
/// The fill animates to a new value unless Reduce Motion is on, because a rail that jumps is
/// hard to read while it is being watched.
public struct RLUsageRail: View {
    private let utilization: Double
    private let height: CGFloat
    private let status: RLStatus
    private let showsLimit: Bool
    /// Where the window's own clock has got to, 0 to 1. Level with the fill means the window
    /// is being spent at exactly the rate it refills.
    private let elapsed: Double?
    private let tint: Color?

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(utilization: Double, status: RLStatus, height: CGFloat = 8,
                showsLimit: Bool = true, elapsed: Double? = nil, tint: Color? = nil) {
        self.utilization = utilization
        self.status = status
        self.height = height
        self.showsLimit = showsLimit
        self.elapsed = elapsed
        self.tint = tint
    }

    private var clamped: Double { min(max(utilization, 0), 100) }

    public var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(RL.Surface.sunken)
                Capsule()
                    .fill(tint ?? status.color)
                    // Always a sliver for a real but small share, never nothing
                    .frame(width: max(height * 0.6, geo.size.width * clamped / 100))
                if showsLimit {
                    Rectangle()
                        .fill(RL.Brandmark.signal)
                        .frame(width: 2)
                        .offset(x: geo.size.width - 2)
                }
                if let elapsed, elapsed > 0, elapsed < 1 {
                    Rectangle()
                        .fill(RL.Ink.primary.opacity(0.6))
                        .frame(width: 1)
                        .offset(x: geo.size.width * elapsed)
                        .help("where the clock is: \(Int((elapsed * 100).rounded()))% of "
                              + "this window has passed")
                }
            }
            .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.value),
                       value: clamped)
        }
        .frame(height: height)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(Int(clamped.rounded())) percent used, \(status.phrase)")
    }
}

// MARK: - Placeholder states

/// The four things a panel can say instead of showing data. One component so "nothing yet",
/// "still reading" and "cannot read" never look like the same thing.
public struct RLStateBlock: View {
    public enum Kind: Equatable, Sendable {
        case loading(String)
        /// Nothing to show, and that is a real answer rather than a fault.
        case empty(String)
        /// Something went wrong, said plainly.
        case error(String)
        /// The metric cannot exist here, with the reason. Never a fabricated zero.
        case unavailable(String)
    }

    private let kind: Kind
    private let hint: String?

    public init(_ kind: Kind, hint: String? = nil) {
        self.kind = kind
        self.hint = hint
    }

    private var symbol: String? {
        switch kind {
        case .loading:     return nil
        case .empty:       return "tray"
        case .error:       return "exclamationmark.triangle"
        case .unavailable: return "minus.circle"
        }
    }

    private var text: String {
        switch kind {
        case .loading(let s), .empty(let s), .error(let s), .unavailable(let s): return s
        }
    }

    private var tint: Color {
        switch kind {
        case .error: return RL.State.warning
        default:     return RL.Ink.muted
        }
    }

    public var body: some View {
        HStack(alignment: .top, spacing: RL.Space.md) {
            if case .loading = kind {
                ProgressView().controlSize(.small)
            } else if let symbol {
                Image(systemName: symbol)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(tint)
                    .padding(.top, 1)
            }
            VStack(alignment: .leading, spacing: RL.Space.xxs) {
                Text(text)
                    .font(RL.Typography.body)
                    .foregroundStyle(RL.Ink.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let hint {
                    Text(hint)
                        .font(RL.Typography.caption)
                        .foregroundStyle(RL.Ink.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Small parts

/// A tracked upper-case tag: a finding's kind, a provenance label, a data source.
public struct RLPill: View {
    private let text: String
    private let tint: Color
    private let help: String?

    public init(_ text: String, tint: Color = RL.Ink.muted, help: String? = nil) {
        self.text = text
        self.tint = tint
        self.help = help
    }

    public var body: some View {
        Text(text.uppercased())
            .font(.system(size: 9, weight: .bold, design: .monospaced))
            .tracking(1.0)
            .foregroundStyle(tint)
            .padding(.horizontal, RL.Space.sm)
            .padding(.vertical, RL.Space.xxs)
            .background(tint.opacity(0.14), in: Capsule())
            .modifier(OptionalHelp(help))
    }
}

/// A segmented choice drawn with plain buttons, because a system segmented control takes the
/// system accent and this app's selection colour is its own.
public struct RLSegmented<Value: Hashable>: View {
    private let options: [(value: Value, label: String, help: String?)]
    private let selection: Value
    private let width: CGFloat
    private let onSelect: (Value) -> Void

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(options: [(value: Value, label: String, help: String?)], selection: Value,
                width: CGFloat = 40, onSelect: @escaping (Value) -> Void) {
        self.options = options
        self.selection = selection
        self.width = width
        self.onSelect = onSelect
    }

    public var body: some View {
        HStack(spacing: RL.Space.xs) {
            ForEach(options, id: \.value) { option in
                let active = option.value == selection
                Button { onSelect(option.value) } label: {
                    Text(option.label)
                        .font(.system(size: 12, weight: .medium, design: .monospaced))
                        .frame(width: width, height: 24)
                        .background(active ? RL.Brandmark.signal.opacity(0.2)
                                           : RL.Surface.sunken)
                        .foregroundStyle(active ? RL.Ink.primary : RL.Ink.muted)
                        .clipShape(RoundedRectangle(cornerRadius: RL.Radius.chip,
                                                    style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: RL.Radius.chip, style: .continuous)
                                .strokeBorder(active ? RL.Brandmark.signal.opacity(0.55)
                                                     : .clear, lineWidth: 1)
                        )
                }
                .buttonStyle(.plain)
                .modifier(OptionalHelp(option.help))
                .accessibilityLabel(option.help ?? option.label)
                .accessibilityAddTraits(active ? [.isButton, .isSelected] : .isButton)
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.hover), value: selection)
    }
}

/// A borderless action that still reads as a control: an icon, a word, and a hover tint.
public struct RLInlineButton: View {
    private let title: String
    private let systemImage: String?
    private let help: String?
    private let action: () -> Void

    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    public init(_ title: String, systemImage: String? = nil, help: String? = nil,
                action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.help = help
        self.action = action
    }

    public var body: some View {
        Button(action: action) {
            HStack(spacing: RL.Space.xs) {
                if let systemImage {
                    Image(systemName: systemImage).font(.system(size: 10, weight: .semibold))
                }
                Text(title).font(RL.Typography.caption)
            }
            .foregroundStyle(hovering ? RL.Ink.primary : RL.Ink.secondary)
            .padding(.horizontal, RL.Space.md)
            .padding(.vertical, RL.Space.xs)
            .background(hovering ? RL.Surface.sunken : .clear,
                        in: RoundedRectangle(cornerRadius: RL.Radius.chip, style: .continuous))
        }
        .buttonStyle(.plain)
        .modifier(OptionalHelp(help))
        .animation(reduceMotion ? nil : .easeOut(duration: RL.Motion.hover), value: hovering)
        .onHover { hovering = $0 }
    }
}
