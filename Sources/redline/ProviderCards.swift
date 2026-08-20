// The overview: what every provider is doing, as one card each, plus the warnings worth
// reading before the cards. Card content is derived in RedlineCore; this file draws it.
import RedlineCore
import SwiftUI

/// A trend as bars, small enough to sit inside a card. Charts answer shape questions, so this
/// answers only "when was the work", and the figures beside it answer the rest.
///
/// Drawn in a Canvas rather than as a stack of views: a 90 day range is 90 bars per card, and
/// three cards of those is a lot of view identity for a decoration.
struct MiniTrend: View {
    let points: [Int]
    let tint: Color
    var height: CGFloat = 26
    /// Names what the bars are, for the accessibility summary. Charts get a text summary.
    var label: String

    private var peak: Int { max(points.max() ?? 1, 1) }

    private var summary: String {
        guard !points.isEmpty, points.contains(where: { $0 > 0 }) else {
            return "\(label): no activity"
        }
        let total = points.reduce(0, +)
        let busiest = points.enumerated().max(by: { $0.element < $1.element })
        let ago = busiest.map { points.count - 1 - $0.offset } ?? 0
        let when = ago == 0 ? "today" : ago == 1 ? "yesterday" : "\(ago) days ago"
        return "\(label): \(fmtTokens(total)) over \(points.count) days, busiest \(when) at "
            + "\(fmtTokens(busiest?.element ?? 0))"
    }

    var body: some View {
        Canvas { context, size in
            guard !points.isEmpty else { return }
            // A minimum gap that disappears once the bars themselves are hairlines, so a long
            // range stays a readable shape instead of a grey wash
            let gap: CGFloat = points.count > 45 ? 0.5 : 1.5
            let slot = size.width / CGFloat(points.count)
            let width = max(1, slot - gap)
            for (index, value) in points.enumerated() {
                let fraction = Double(value) / Double(peak)
                // A real but tiny day keeps a visible foot rather than rounding to nothing
                let barHeight = value > 0 ? max(2, size.height * fraction) : 1
                let rect = CGRect(x: CGFloat(index) * slot,
                                  y: size.height - barHeight,
                                  width: width, height: barHeight)
                context.fill(Path(roundedRect: rect, cornerRadius: min(1.5, width / 2)),
                             with: .color(value > 0 ? tint : RL.Ink.muted.opacity(0.25)))
            }
        }
        .frame(height: height)
        .accessibilityElement()
        .accessibilityLabel(summary)
    }
}

/// A card that is also a control: the whole surface opens the provider's detail. Focus is
/// drawn explicitly, because a plain button style otherwise leaves keyboard focus invisible.
private struct CardButton<Content: View>: View {
    let accent: Color
    let selected: Bool
    let help: String
    let accessibilityLabel: String
    let action: () -> Void
    @ViewBuilder var content: Content

    @FocusState private var focused: Bool

    var body: some View {
        Button(action: action) {
            RLCard(accent: accent, interactive: true, selected: selected,
                   padding: RL.Space.lg) {
                // Cards in one grid row are stretched to the tallest, so content is pinned to
                // the top rather than floating in the middle of a row a sibling made taller
                VStack(alignment: .leading, spacing: 0) {
                    content
                    Spacer(minLength: 0)
                }
            }
            .frame(maxHeight: .infinity)
        }
        .buttonStyle(.plain)
        .focused($focused)
        .overlay(
            RoundedRectangle(cornerRadius: RL.Radius.card, style: .continuous)
                .strokeBorder(Color.accentColor, lineWidth: focused ? 2.5 : 0)
                .padding(-1)
        )
        .help(help)
        .accessibilityLabel(accessibilityLabel)
        .accessibilityHint("Opens this provider's detail")
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }
}

/// One provider's overview card. Cards are visually related by construction, since they share
/// this one body, and distinguishable by the accent rule and the mark at the top.
struct ProviderCardView: View {
    let card: ProviderCard
    let yellow: Double
    let red: Double
    /// The window the figures cover, named on the card so a number is never read against the
    /// wrong period.
    let periodLabel: String
    let scannedAt: Date?
    let selected: Bool
    let onOpen: () -> Void

    private var accent: Color { card.identity?.accent ?? RL.Accent.neutral }
    private var status: RLStatus { card.status(approaching: yellow, atLimit: red) }

    var body: some View {
        CardButton(accent: accent, selected: selected,
                   help: help, accessibilityLabel: accessibilityLabel, action: onOpen) {
            VStack(alignment: .leading, spacing: RL.Space.md) {
                // The provider's own accent as a rule across the top: the cards are siblings,
                // and this is what makes each one recognisable before it is read
                Capsule()
                    .fill(accent.opacity(card.connection == .notInstalled ? 0.3 : 0.9))
                    .frame(height: 2.5)
                header
                Divider().overlay(RL.Stroke.hairline)
                if card.connection.hasFigures {
                    figures
                } else {
                    RLStateBlock(placeholder, hint: hint)
                        .frame(minHeight: 62, alignment: .top)
                }
                footer
            }
        }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: RL.Space.md) {
            ProviderBadge.forProvider(card.provider, size: 14)
            Spacer(minLength: RL.Space.xs)
            // Status is a shape plus a word, never the colour on its own
            RLStatusIndicator(status, size: 12, showsLabel: false)
            Text(statusWord)
                .font(RL.Typography.caption)
                .foregroundStyle(card.isStale ? RL.State.warning : RL.Ink.secondary)
                .lineLimit(1)
        }
    }

    /// The short form of whatever the header glyph is reporting. It has to describe the same
    /// thing the glyph does: a red octagon beside the word "live" reads as a contradiction,
    /// and "live" was describing the connection while the glyph described the limit.
    private var statusWord: String {
        if card.isStale, let asOf = card.asOf {
            return "as of \(asOf.formatted(date: .omitted, time: .shortened))"
        }
        // With a limit in view the glyph reports the limit, so the word does too. This is also
        // what keeps the severity off colour alone inside the card.
        if card.worstWindow != nil {
            switch status.kind {
            case .atLimit:     return "at limit"
            case .approaching: return "approaching"
            default:           return "healthy"
            }
        }
        switch card.connection {
        case .active:       return "live"
        case .idle:         return "idle"
        case .unreachable:  return "stopped"
        case .notRead:      return "off"
        case .notInstalled: return "absent"
        }
    }

    @ViewBuilder
    private var figures: some View {
        VStack(alignment: .leading, spacing: RL.Space.md) {
            if let utilization = card.utilization, let window = card.worstWindow {
                VStack(alignment: .leading, spacing: RL.Space.sm) {
                    HStack(alignment: .firstTextBaseline, spacing: RL.Space.sm) {
                        Text("\(Int(utilization.rounded()))%")
                            .font(.system(size: 27, weight: .semibold, design: .monospaced))
                            .foregroundStyle(status.color)
                            .contentTransition(.numericText())
                        Text("used")
                            .font(RL.Typography.caption)
                            .foregroundStyle(RL.Ink.muted)
                        Spacer(minLength: 0)
                        if let remaining = card.remainingPercent {
                            Text("\(Int(remaining.rounded()))% left")
                                .font(RL.Typography.monoSmall)
                                .foregroundStyle(RL.Ink.secondary)
                                .help("Capacity remaining in \(window.displayName)")
                        }
                    }
                    RLUsageRail(utilization: utilization, status: status, height: 7,
                                elapsed: card.pace?.elapsedFraction)
                    Text(windowLine(window))
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                        .lineLimit(1)
                }
            } else {
                // No limit to draw: say why, and let the volume figures carry the card
                VStack(alignment: .leading, spacing: RL.Space.xs) {
                    Text(fmtTokens(card.tokens))
                        .font(.system(size: 27, weight: .semibold, design: .monospaced))
                        .foregroundStyle(RL.Ink.primary)
                        .contentTransition(.numericText())
                    Text("tokens \(periodLabel)")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                    if let note = card.limitNote {
                        Text(note)
                            .font(RL.Typography.caption)
                            .foregroundStyle(RL.Ink.muted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            trendRow
        }
    }

    /// The window, and when it rolls over. The pace joins it only when the cap arrives before
    /// the reset does, which is the one case worth the width; otherwise the line truncated
    /// mid-phrase and said less than nothing.
    private func windowLine(_ window: LimitWindow) -> String {
        var parts = [window.displayName]
        if let resets = window.resetsAt {
            parts.append("resets \(resets.formatted(date: .omitted, time: .shortened))")
        }
        if let pace = card.pace, pace.hitsLimitBeforeReset,
           let summary = pace.compact() {
            parts.append(summary)
        }
        return parts.joined(separator: " · ")
    }

    @ViewBuilder
    private var trendRow: some View {
        if card.trend.contains(where: { $0 > 0 }) {
            VStack(alignment: .leading, spacing: RL.Space.xs) {
                MiniTrend(points: card.trend, tint: accent.opacity(0.8), height: 24,
                          label: "\(card.provider) tokens per day")
                HStack(spacing: RL.Space.sm) {
                    Text(fmtTokens(card.tokens))
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.secondary)
                    Text("·")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                    Text(fmtCost(card.cost) + (card.hasUnpriced ? "+" : "") + " est")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Brandmark.money)
                        .help(card.hasUnpriced
                              ? "Some models in this window have no pricing entry, so they are "
                                + "counted in tokens only and the total is marked with a plus"
                              : "Estimated from your pricing table, never a bill")
                    Spacer(minLength: 0)
                    Text(periodLabel)
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                }
            }
        }
    }

    private var footer: some View {
        HStack(spacing: RL.Space.sm) {
            if let tone = card.serviceTone, let phrase = card.servicePhrase {
                RLStatusIndicator(RLStatus.forTone(tone, phrase: phrase), size: 10)
                Text(phrase)
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
                    .lineLimit(1)
            } else if let at = scannedAt {
                Text("updated \(at.formatted(date: .omitted, time: .standard))")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
            }
            Spacer(minLength: 0)
            Image(systemName: "chevron.right")
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(RL.Ink.muted)
                .accessibilityHidden(true)
        }
    }

    private var placeholder: RLStateBlock.Kind {
        switch card.connection {
        case .notInstalled: return .unavailable(card.connection.phrase)
        case .notRead:      return .unavailable(card.connection.phrase)
        case .unreachable:  return .error(card.connection.phrase)
        case .idle:         return .empty(card.connection.phrase)
        case .active:       return .empty(card.connection.phrase)
        }
    }

    private var hint: String? {
        switch card.connection {
        case .notInstalled:
            return card.identity?.blurb
        case .notRead:
            return "Switch it on in Settings, under Providers"
        case .unreachable:
            return card.identity?.isLocal == true ? "Start it with: ollama serve" : nil
        case .idle:
            return card.limitNote
        case .active:
            return nil
        }
    }

    private var help: String {
        var parts = ["\(card.provider): \(status.phrase)"]
        if let identity = card.identity { parts.append(identity.blurb) }
        return parts.joined(separator: ". ")
    }

    private var accessibilityLabel: String {
        var parts = [card.provider, status.phrase]
        if let utilization = card.utilization, let window = card.worstWindow {
            parts.append("\(Int(utilization.rounded())) percent of \(window.displayName) used")
            if let resets = window.resetsAt {
                parts.append("resets \(resets.formatted(date: .omitted, time: .shortened))")
            }
        }
        if card.connection.hasFigures {
            parts.append("\(fmtTokens(card.tokens)) tokens \(periodLabel)")
        }
        return parts.joined(separator: ", ")
    }
}

/// The card grid.
///
/// Not `.adaptive(minimum:)`: that derives the column count from the minimum width, so a wide
/// window produced six columns for three cards and left them hugging the left edge at minimum
/// width. This picks the column count from the space available, capped at the number of cards,
/// so the cards always share the full width and wrap on a narrow window.
struct ProviderCardGrid<Card: View>: View {
    let count: Int
    /// The narrowest a card may get before a column is dropped. Not a screen size: it is the
    /// width at which this card's own content stops fitting.
    var minimumCardWidth: CGFloat = 268
    @ViewBuilder var card: (Int) -> Card

    @State private var available: CGFloat = 0

    private var columns: Int {
        guard available > 0 else { return min(count, 3) }
        let fits = Int((available + RL.Space.lg) / (minimumCardWidth + RL.Space.lg))
        return max(1, min(count, fits))
    }

    var body: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: RL.Space.lg,
                                                    // Top, so a taller sibling does not push
                                                    // the others into the middle of the row
                                                    alignment: .top),
                                count: columns),
                  spacing: RL.Space.lg) {
            ForEach(0..<count, id: \.self) { index in
                card(index)
            }
        }
        .background(
            GeometryReader { geo in
                Color.clear.preference(key: AvailableWidthKey.self, value: geo.size.width)
            }
        )
        .onPreferenceChange(AvailableWidthKey.self) { width in
            available = width
        }
    }
}

private struct AvailableWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

/// The warnings that earn a place above the cards. Nothing is shown when nothing is true,
/// because a banner that is always lit says nothing.
struct OverviewWarnings: View {
    let warnings: [ProviderOverview.Warning]
    let onOpen: (String) -> Void
    /// How many fit above the cards before the overview stops being a summary. The rest are
    /// counted rather than dropped: the Limits panel below lists every one of them.
    private let shown = 3

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.md) {
            ForEach(warnings.prefix(shown)) { warning in
                let status = warning.kind.status
                Button { onOpen(warning.provider) } label: {
                    HStack(alignment: .center, spacing: RL.Space.md) {
                        RLStatusIndicator(status, size: 14)
                        Text(warning.text)
                            .font(RL.Typography.body)
                            .foregroundStyle(RL.Ink.primary)
                            .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: RL.Space.md)
                        if let resets = warning.window.resetsAt {
                            Text("resets \(resets.formatted(date: .omitted, time: .shortened))")
                                .font(RL.Typography.monoSmall)
                                .foregroundStyle(RL.Ink.secondary)
                        }
                    }
                    .padding(.horizontal, RL.Space.lg)
                    .padding(.vertical, RL.Space.md)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(status.color.opacity(0.1),
                                in: RoundedRectangle(cornerRadius: RL.Radius.control,
                                                     style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: RL.Radius.control, style: .continuous)
                            .strokeBorder(status.color.opacity(0.4), lineWidth: 1)
                    )
                }
                .buttonStyle(.plain)
                .help("Opens \(warning.provider)")
                .accessibilityLabel("\(status.phrase). \(warning.text)")
            }
            if warnings.count > shown {
                Text("\(warnings.count - shown) more in Limits below")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.muted)
                    .padding(.horizontal, RL.Space.lg)
            }
        }
    }
}
