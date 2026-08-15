// Desktop and Notification Centre widgets. They render the snapshot the menu bar app
// publishes and never read a transcript or open a socket: a widget process has a hard time
// budget, and staying offline means it needs no network entitlement.
import AppIntents
import RedlineCore
import SwiftUI
import WidgetKit

// MARK: - Configuration

enum TrackChoice: String, AppEnum {
    case all, claude, codex, ollama

    static var typeDisplayRepresentation = TypeDisplayRepresentation(name: "Track")

    static var caseDisplayRepresentations: [TrackChoice: DisplayRepresentation] = [
        .all:    DisplayRepresentation(title: "All providers"),
        .claude: DisplayRepresentation(title: "Claude"),
        .codex:  DisplayRepresentation(title: "Codex"),
        .ollama: DisplayRepresentation(title: "Ollama"),
    ]

    /// nil means every provider at once
    var provider: String? {
        switch self {
        case .all:    return nil
        case .claude: return "Claude"
        case .codex:  return "Codex"
        case .ollama: return "Ollama"
        }
    }
}

struct SelectTrackIntent: WidgetConfigurationIntent {
    static var title: LocalizedStringResource = "Choose a track"
    static var description = IntentDescription(
        "Show every provider, or focus one. Add the widget more than once to watch several.")

    @Parameter(title: "Track", default: .all)
    var track: TrackChoice

    init() {}
    init(track: TrackChoice) { self.track = track }
}

// MARK: - Timeline

struct Entry: TimelineEntry {
    let date: Date
    let snapshot: Snapshot?
    let track: TrackChoice
}

struct Provider: AppIntentTimelineProvider {
    // Read real data even for the placeholder: returning nil renders as redacted grey bars,
    // which looks broken rather than like a preview.
    func placeholder(in context: Context) -> Entry {
        Entry(date: Date(), snapshot: SnapshotStore.readAny(), track: .all)
    }

    func snapshot(for configuration: SelectTrackIntent, in context: Context) async -> Entry {
        Entry(date: Date(), snapshot: SnapshotStore.readAny(), track: configuration.track)
    }

    // WidgetKit decides when to reload, so ask for a modest cadence and let the app nudge us
    // with reloadTimelines after each refresh.
    func timeline(for configuration: SelectTrackIntent, in context: Context) async
        -> Timeline<Entry> {
        let now = Date()
        return Timeline(entries: [Entry(date: now, snapshot: SnapshotStore.readAny(),
                                        track: configuration.track)],
                        policy: .after(now.addingTimeInterval(300)))
    }
}

// MARK: - Pieces

/// One type scale for the whole widget, growing with the family. A widget is read at a
/// glance from a few feet away, so the number carries the row and everything else supports it.
private struct Metrics {
    let title: CGFloat
    let hero: CGFloat
    let label: CGFloat
    let detail: CGFloat
    let totals: CGFloat
    let rail: CGFloat
    let markSize: CGFloat
    let spacing: CGFloat

    static func of(_ family: WidgetFamily) -> Metrics {
        switch family {
        case .systemSmall:
            return Metrics(title: 13, hero: 42, label: 13, detail: 11, totals: 12,
                           rail: 8, markSize: 16, spacing: 4)
        case .systemLarge:
            return Metrics(title: 17, hero: 52, label: 15, detail: 13, totals: 15,
                           rail: 11, markSize: 21, spacing: 7)
        default:
            return Metrics(title: 15, hero: 46, label: 14, detail: 12, totals: 13,
                           rail: 9, markSize: 18, spacing: 5)
        }
    }
}

private func resetText(_ d: Date?) -> String {
    guard let d else { return "" }
    let f = DateFormatter()
    f.amSymbol = "a"
    f.pmSymbol = "p"
    f.dateFormat = Calendar.current.isDateInToday(d) ? "h:mma" : "EEE h:mma"
    return f.string(from: d)
}

private struct Header: View {
    let title: String
    let m: Metrics
    /// nil shows the app mark, a provider shows that track's badge
    var provider: String? = nil
    var trailing: String? = nil
    /// Service health as a dot beside the title: present at every size, never words
    var statusColor: Color? = nil

    var body: some View {
        HStack(spacing: 7) {
            // The track badge leads, since which track this is matters most at a glance
            TrackBadge(provider: provider, size: m.markSize + 8)
            Text(title)
                .font(.system(size: m.title, weight: .semibold))
                .foregroundStyle(BrandUI.chalk)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            if let statusColor {
                Circle().fill(statusColor).frame(width: 6, height: 6)
            }
            Spacer(minLength: 2)
            if let trailing {
                Text(trailing)
                    .font(.system(size: m.detail, design: .monospaced))
                    .foregroundStyle(BrandUI.steel)
                    .lineLimit(1)
            }
            // RedLine's own mark stays, quietly, as the signature on the panel
            RedlineMark(size: m.markSize - 2)
                .opacity(0.55)
        }
    }
}

/// The hero reading: label, a large percentage, its rail, and when it resets.
private struct WindowBlock: View {
    let label: String
    let window: Snapshot.Window?
    let m: Metrics
    var showsReset = true
    var heroSize: CGFloat? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.system(size: m.label, weight: .medium))
                .foregroundStyle(BrandUI.steel)
                .lineLimit(1)
            if let w = window {
                Text("\(Int(w.utilization.rounded()))%")
                    .font(.system(size: heroSize ?? m.hero, weight: .bold, design: .rounded))
                    .foregroundStyle(BrandUI.statusColor(w.utilization))
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            } else {
                // A dash, never a zero that would read as plenty left
                Text("—")
                    .font(.system(size: heroSize ?? m.hero, weight: .bold, design: .rounded))
                    .foregroundStyle(BrandUI.steel)
            }
            LimitRail(utilization: window?.utilization ?? 0, height: m.rail,
                      showsLimit: window != nil)
            if showsReset, let r = window?.resetsAt {
                Text("resets \(resetText(r))")
                    .font(.system(size: m.detail, design: .monospaced))
                    .foregroundStyle(BrandUI.steel)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
        }
    }
}

/// A single compact line, for the second window on the small size where a full block will
/// not fit without shrinking the hero number.
private struct WindowLine: View {
    let label: String
    let window: Snapshot.Window?
    let m: Metrics

    var body: some View {
        HStack(spacing: 6) {
            Text(label)
                .font(.system(size: m.label, weight: .medium))
                .foregroundStyle(BrandUI.steel)
            Spacer(minLength: 4)
            if let w = window {
                Text("\(Int(w.utilization.rounded()))%")
                    .font(.system(size: m.label + 5, weight: .bold, design: .rounded))
                    .foregroundStyle(BrandUI.statusColor(w.utilization))
            } else {
                Text("—")
                    .font(.system(size: m.label + 5, weight: .bold))
                    .foregroundStyle(BrandUI.steel)
            }
        }
    }
}

private struct TotalsRow: View {
    let label: String
    let totals: Snapshot.Totals
    let m: Metrics

    var body: some View {
        HStack(spacing: 6) {
            Text(label)
                .font(.system(size: m.totals))
                .foregroundStyle(BrandUI.steel)
            Spacer(minLength: 4)
            Text(fmtTokens(totals.io))
                .font(.system(size: m.totals, weight: .medium, design: .monospaced))
                .foregroundStyle(BrandUI.chalk)
            Text("\(fmtCost(totals.cost))\(totals.hasUnpriced ? "+" : "")")
                .font(.system(size: m.totals, design: .monospaced))
                .foregroundStyle(BrandUI.steel)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
    }
}

private struct StaleNote: View {
    let snapshot: Snapshot
    let m: Metrics

    var body: some View {
        if snapshot.isStale() {
            Text("Last updated \(resetText(snapshot.updatedAt))")
                .font(.system(size: m.detail, design: .monospaced))
                .foregroundStyle(BrandUI.amber)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
    }
}

// The provider's own reported health, one quiet line. Shown only when the app publishes
// status reports (the status checks are opt-in) and colored by what the operator says.
private struct ServiceLine: View {
    let snapshot: Snapshot
    let provider: String?
    let m: Metrics

    private var report: Snapshot.Service? {
        guard let services = snapshot.services, !services.isEmpty else { return nil }
        if let provider { return services.first { $0.provider == provider } }
        // The all-providers track reports the worst news anyone has
        return services.min { rank($0) > rank($1) }
    }

    private func rank(_ s: Snapshot.Service) -> Int {
        switch ServiceGlyph.tone(for: s.indicator) {
        case .critical: return 2
        case .warning:  return 1
        case .healthy, .unknown: return 0
        }
    }

    private func color(_ s: Snapshot.Service) -> Color {
        switch ServiceGlyph.tone(for: s.indicator) {
        case .healthy:  return BrandUI.clear
        case .warning:  return BrandUI.amber
        case .critical: return BrandUI.signal
        case .unknown:  return BrandUI.steel
        }
    }

    var body: some View {
        if let r = report {
            HStack(spacing: 4) {
                // The same glyph the dropdown and the dashboard use for this state
                Image(systemName: ServiceGlyph.symbol(for: r.indicator))
                    .font(.system(size: m.detail, weight: .semibold))
                    .foregroundStyle(color(r))
                Text(r.phrase)
                    .font(.system(size: m.detail, design: .monospaced))
                    .foregroundStyle(BrandUI.steel)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
        }
    }
}

private struct Unavailable: View {
    let m: Metrics

    var body: some View {
        VStack(spacing: 7) {
            RedlineMark(size: m.markSize + 10)
            Text("Usage unavailable")
                .font(.system(size: m.title + 1, weight: .semibold))
                .foregroundStyle(BrandUI.chalk)
            Text("Open RedLine to refresh")
                .font(.system(size: m.label))
                .foregroundStyle(BrandUI.steel)
                .multilineTextAlignment(.center)
        }
    }
}

// MARK: - Ollama

private struct OllamaBody: View {
    let snapshot: Snapshot
    let family: WidgetFamily

    private var o: Snapshot.Ollama? { snapshot.ollama }
    private var m: Metrics { Metrics.of(family) }

    var body: some View {
        ViewThatFits(in: .vertical) {
            ForEach(Detail.allCases, id: \.self) { layout($0) }
        }
    }

    private func modelLimit(_ detail: Detail) -> Int {
        switch (family, detail) {
        case (.systemSmall, _):   return detail == .full ? 1 : 0
        case (.systemLarge, .full): return 3
        case (_, .lean):          return 0
        default:                  return 2
        }
    }

    @ViewBuilder
    private func layout(_ detail: Detail) -> some View {
        VStack(alignment: .leading, spacing: m.spacing) {
            Header(title: "Ollama", m: m, provider: "Ollama",
                   trailing: detail == .full ? o?.version.map { "v\($0)" } : nil,
                   statusColor: (o?.reachable ?? false) ? BrandUI.clear : BrandUI.steel)

            if let o, o.reachable {
                // The counts are the glanceable part, so they carry the type weight.
                // Local and cloud are separate counts on purpose: which machine a model
                // runs on is a fact, not a detail.
                HStack(alignment: .firstTextBaseline, spacing: 14) {
                    CountBlock(value: "\(o.running.count)", label: "loaded", m: m,
                               tint: o.running.isEmpty ? BrandUI.steel : BrandUI.clear)
                    if family == .systemSmall || (o.cloudCount ?? 0) == 0 {
                        CountBlock(value: "\(o.downloadedCount)", label: "on disk", m: m,
                                   tint: BrandUI.chalk)
                    } else {
                        CountBlock(value: "\(o.localCount)", label: "local", m: m,
                                   tint: BrandUI.chalk)
                        CountBlock(value: "\(o.cloudCount ?? 0)", label: "☁ cloud", m: m,
                                   tint: BrandUI.steel)
                    }
                    if family != .systemSmall {
                        CountBlock(value: fmtBytes(o.downloadedBytes), label: "size", m: m,
                                   tint: BrandUI.chalk, scale: 0.5)
                    }
                    Spacer(minLength: 0)
                }

                let shown = modelLimit(detail)
                if o.running.isEmpty {
                    if detail != .lean {
                        Text("No model in memory")
                            .font(.system(size: m.label))
                            .foregroundStyle(BrandUI.steel)
                    }
                } else if shown > 0 {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(o.running.prefix(shown), id: \.name) { model in
                            VStack(alignment: .leading, spacing: 3) {
                                HStack(spacing: 6) {
                                    TrackBadge(provider: "Ollama", size: m.label + 4)
                                    Text(OllamaLocality.marked(model.name))
                                        .font(.system(size: m.label, weight: .medium,
                                                      design: .monospaced))
                                        .foregroundStyle(BrandUI.chalk)
                                        .lineLimit(1)
                                        .minimumScaleFactor(0.7)
                                    Spacer(minLength: 3)
                                    Text("\(Int((model.vramShare * 100).rounded()))% GPU")
                                        .font(.system(size: m.detail, design: .monospaced))
                                        .foregroundStyle(BrandUI.steel)
                                }
                                // Weights resident on the GPU, not a usage limit
                                LimitRail(utilization: model.vramShare * 100,
                                          height: max(4, m.rail - 3), showsLimit: false)
                            }
                        }
                        if o.running.count > shown {
                            Text("+\(o.running.count - shown) more loaded")
                                .font(.system(size: m.detail, design: .monospaced))
                                .foregroundStyle(BrandUI.steel)
                        }
                    }
                }

                if family == .systemLarge, detail == .full {
                    Divider().overlay(BrandUI.steel.opacity(0.25))
                    TotalsRow(label: "Tokens today",
                              totals: snapshot.today(for: "Ollama"), m: m)
                }
            } else {
                Spacer(minLength: 0)
                Text("Ollama is not running")
                    .font(.system(size: m.title, weight: .semibold))
                    .foregroundStyle(BrandUI.chalk)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                if detail != .lean {
                    Text("start it with: ollama serve")
                        .font(.system(size: m.detail, design: .monospaced))
                        .foregroundStyle(BrandUI.steel)
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                }
                Spacer(minLength: 0)
            }
            Spacer(minLength: 0)
            // Same rule as the usage cards: the health line outlives the detail around it
            if detail != .lean {
                HStack(spacing: 8) {
                    ServiceLine(snapshot: snapshot, provider: "Ollama", m: m)
                    StaleNote(snapshot: snapshot, m: m)
                }
            }
        }
    }
}

private struct CountBlock: View {
    let value: String
    let label: String
    let m: Metrics
    let tint: Color
    var scale: CGFloat = 0.78

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(value)
                .font(.system(size: m.hero * scale, weight: .bold, design: .rounded))
                .foregroundStyle(tint)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(label)
                .font(.system(size: m.label))
                .foregroundStyle(BrandUI.steel)
        }
    }
}

// MARK: - Usage

/// How much detail a layout attempt includes. ViewThatFits walks these in order and renders
/// the first that fits, so nothing is ever clipped no matter how the type scale changes.
private enum Detail: CaseIterable {
    case full, trimmed, lean
}

private struct UsageBody: View {
    let snapshot: Snapshot
    let track: TrackChoice
    let family: WidgetFamily

    private var provider: String? { track.provider }
    private var title: String { provider ?? "Usage" }
    private var m: Metrics { Metrics.of(family) }

    private var session: Snapshot.Window? {
        snapshot.worst(prefix: "five_hour", provider: provider)
    }
    private var week: Snapshot.Window? {
        snapshot.worst(prefix: "seven_day", provider: provider)
    }
    private var windows: [Snapshot.Window] {
        provider.map { snapshot.windows(for: $0) } ?? snapshot.limits
    }

    // The title dot: this provider's report, or for the all track the worst anyone has.
    // nil when status was never published, so nothing is claimed that was not checked.
    private var headerStatusColor: Color? {
        guard let services = snapshot.services, !services.isEmpty else { return nil }
        let relevant = provider.map { p in services.filter { $0.provider == p } } ?? services
        guard !relevant.isEmpty else { return nil }
        if relevant.contains(where: { ["major", "critical"].contains($0.indicator) }) {
            return BrandUI.signal
        }
        if relevant.contains(where: { ["minor", "local-down"].contains($0.indicator) }) {
            return BrandUI.amber
        }
        return BrandUI.clear
    }

    var body: some View {
        // Measured by the system rather than guessed: the previous fixed layout clipped its
        // header and last row once the type grew.
        ViewThatFits(in: .vertical) {
            ForEach(Detail.allCases, id: \.self) { layout($0) }
        }
    }

    @ViewBuilder
    private func layout(_ detail: Detail) -> some View {
        VStack(alignment: .leading, spacing: m.spacing) {
            Header(title: title, m: m, provider: provider,
                   trailing: detail == .full && provider == nil && !snapshot.limits.isEmpty
                       ? "nearest" : nil,
                   statusColor: headerStatusColor)

            if session == nil && week == nil {
                Spacer(minLength: 0)
                Text(provider == nil ? "No limits reported"
                                     : "No limits reported for \(title)")
                    .font(.system(size: m.label))
                    .foregroundStyle(BrandUI.steel)
                Spacer(minLength: 0)
            } else if family == .systemSmall {
                WindowBlock(label: "Session · 5h", window: session, m: m,
                            showsReset: detail != .lean)
                if detail != .lean {
                    Spacer(minLength: 0)
                    WindowLine(label: "Week", window: week, m: m)
                }
            } else {
                // A provider with one window gets the full width for it; an empty
                // "Session" column beside Codex's week just looked broken
                HStack(alignment: .top, spacing: 16) {
                    if session != nil || week == nil {
                        WindowBlock(label: "Session · 5h", window: session, m: m,
                                    showsReset: detail != .lean)
                    }
                    if week != nil {
                        WindowBlock(label: "Week", window: week, m: m,
                                    showsReset: detail != .lean)
                    }
                }
            }

            if family != .systemSmall, detail != .lean {
                Divider().overlay(BrandUI.steel.opacity(0.25))
                TotalsRow(label: "Today", totals: snapshot.today(for: provider), m: m)
                if family == .systemLarge, detail == .full {
                    TotalsRow(label: "Last 7 days",
                              totals: snapshot.week(for: provider), m: m)
                }
            }

            // Only the all-providers track learns anything here: on a single provider the
            // rows repeat the gauges above, and the height they cost pushed the card down
            // to a leaner layout that dropped its totals and status line.
            if family == .systemLarge, detail == .full, provider == nil, windows.count > 1 {
                Divider().overlay(BrandUI.steel.opacity(0.25))
                VStack(alignment: .leading, spacing: 7) {
                    ForEach(windows) { w in
                        HStack(spacing: 7) {
                            TrackBadge(provider: w.provider, size: m.totals + 7)
                            Text("\(w.provider) · \(w.displayName)")
                                .font(.system(size: m.totals))
                                .foregroundStyle(BrandUI.chalk)
                                .lineLimit(1)
                                .minimumScaleFactor(0.75)
                            Spacer(minLength: 4)
                            Text("\(Int(w.utilization.rounded()))%")
                                .font(.system(size: m.totals + 2, weight: .bold,
                                              design: .rounded))
                                .foregroundStyle(BrandUI.statusColor(w.utilization))
                        }
                    }
                }
            }

            Spacer(minLength: 0)
            // Health and staleness are one short line, and they are the last thing worth
            // dropping: a card that stays quiet about an outage is worse than a cramped one.
            if detail != .lean {
                HStack(spacing: 8) {
                    ServiceLine(snapshot: snapshot, provider: provider, m: m)
                    StaleNote(snapshot: snapshot, m: m)
                }
            }
        }
    }
}

// MARK: - Entry view

struct RedlineWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: Entry

    var body: some View {
        Group {
            if let snap = entry.snapshot {
                if entry.track == .ollama {
                    OllamaBody(snapshot: snap, family: family)
                } else {
                    UsageBody(snapshot: snap, track: entry.track, family: family)
                }
            } else {
                Unavailable(m: Metrics.of(family))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }
}

// MARK: - Widgets

struct RedlineUsageWidget: Widget {
    let kind = "RedlineWidget"

    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: SelectTrackIntent.self,
                               provider: Provider()) { entry in
            RedlineWidgetView(entry: entry)
                // The widget paints Carbon whatever the OS theme, so dynamic brand colors
                // must resolve as if dark; without this a light-mode Mac gets ink on carbon
                .colorScheme(.dark)
                // Carbon base with a soft wash of the track's own colour, so widgets tell
                // themselves apart at a glance. The wash stays quiet on purpose: colour
                // identifies the track, it never shouts about it.
                .containerBackground(for: .widget) {
                    let tint = entry.track.provider.map { BrandUI.color(forProvider: $0) }
                        ?? BrandUI.steel
                    LinearGradient(
                        stops: [
                            .init(color: BrandUI.carbon, location: 0),
                            .init(color: tint.opacity(0.16), location: 1),
                        ],
                        startPoint: .bottomLeading, endPoint: .topTrailing)
                        .background(BrandUI.carbon)
                }
        }
        .configurationDisplayName("RedLine")
        .description("Claude, Codex, and Ollama usage at a glance. Add one per track.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

@main
struct RedlineWidgetBundle: WidgetBundle {
    var body: some Widget {
        RedlineUsageWidget()
    }
}
