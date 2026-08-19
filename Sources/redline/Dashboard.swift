// Dashboard window: charts over the same data the menu bar summarises. Scanning is done off
// the main thread because a 30 day range can touch a lot of transcript files.
import Charts
import RedlineCore
import SwiftUI

enum Brandkit {
    // Dashboard surface and ink tokens resolve per appearance: the brand's dark world by
    // default, and honest light equivalents when the window is light. The status trio
    // stays fixed. Every other painted surface in the app forces dark, so only the
    // dashboard actually exercises the light side.
    private static func dynamic(dark: BrandColor, light: BrandColor) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let c = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(red: c.red, green: c.green, blue: c.blue, alpha: 1)
        })
    }
    private static func dynamic(dark: (Double, Double, Double),
                                light: (Double, Double, Double)) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let c = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(red: c.0, green: c.1, blue: c.2, alpha: 1)
        })
    }

    static let carbon = dynamic(dark: Brand.carbon, light: Brand.chalk)          // ground
    static let graphite = dynamic(dark: Brand.graphite,
                                  light: BrandColor(0xFFFFFF))                   // panels
    static let chalk = dynamic(dark: Brand.chalk, light: Brand.carbon)           // ink
    static let steel = dynamic(dark: Brand.steel, light: BrandColor(0x5C6270))   // quiet ink
    static let signal = BrandUI.signal
    static let amber = BrandUI.amber
    static let clear = BrandUI.clear
    /// Money reads green by convention. Distinct from `clear` so the light side can run
    /// darker: the status green is tuned for dark panels and washes out on white.
    static let money = dynamic(dark: Brand.clear, light: BrandColor(0x1E8E3E))

    /// The status trio, chosen by tone rather than by reading an indicator string twice
    static func tone(_ tone: ServiceGlyph.Tone) -> Color {
        switch tone {
        case .healthy:  return clear
        case .warning:  return amber
        case .critical: return signal
        case .unknown:  return steel
        }
    }

    static func nsTone(_ tone: ServiceGlyph.Tone) -> NSColor {
        switch tone {
        case .healthy:  return NSColor(Brand.clear)
        case .warning:  return NSColor(Brand.amber)
        case .critical: return NSColor(Brand.signal)
        case .unknown:  return NSColor(Brand.steel)
        }
    }

    // Menus follow the system theme and cannot be painted, so these two resolve per
    // appearance: chalk on dark menus, carbon on light ones. Fixed chalk was invisible on
    // light Macs. Every other surface paints Carbon and keeps the fixed brand tones.
    static let menuPrimary = NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(Brand.chalk) : NSColor(Brand.carbon)
    }
    static let menuSecondary = NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(white: 0.72, alpha: 1) : NSColor(white: 0.35, alpha: 1)
    }

    // AppKit counterpart for the menu, from the same tokens. Claude's chalk flips with the
    // theme like the primary text; the saturated tones read on both.
    static func nsColor(for provider: String) -> NSColor {
        switch provider {
        case UsageStore.provider:  return menuPrimary
        case CodexStore.provider:  return NSColor(Brand.steel)
        case OllamaStore.provider: return NSColor(Brand.clear)
        default:                   return NSColor(Brand.amber)
        }
    }

    static func color(for provider: String) -> Color {
        BrandUI.color(forProvider: provider)
    }

    // Chart series colors, distinct from the track identity tones on purpose: chalk and
    // steel are near-neutrals that read as one gray mass in a chart. This triple passes
    // the categorical checks (lightness band, chroma floor, CVD separation, contrast) on
    // Carbon; the legend ties names to colors so identity is never color alone.
    // Each mode's steps validated separately against its own surface: dark on Carbon,
    // light on chalk paper (the light steps sit darker to clear 3:1)
    static func chartColor(for provider: String) -> Color {
        switch provider {
        case UsageStore.provider:  // B9822A / A6741F
            return dynamic(dark: (0.725, 0.510, 0.165), light: (0.651, 0.455, 0.122))
        case CodexStore.provider:  // 5B8DE8 / 3E6FC9
            return dynamic(dark: (0.357, 0.553, 0.910), light: (0.243, 0.435, 0.788))
        case OllamaStore.provider: // 23A63C / 1E8F33
            return dynamic(dark: (0.137, 0.651, 0.235), light: (0.118, 0.561, 0.200))
        default:                   return steel
        }
    }

    /// Vertical fade for bar and area fills: full color at the data end, quieter at the
    /// baseline, so stacks stay separable without extra strokes
    static func chartFill(for provider: String) -> LinearGradient {
        let c = chartColor(for: provider)
        return LinearGradient(colors: [c, c.opacity(0.55)],
                              startPoint: .top, endPoint: .bottom)
    }
}

// One provider's health as its operator reports it: a drawn glyph instead of words, with
// the words and the check time waiting in the hover tooltip
struct ServiceStatusRow: View {
    let provider: String
    let indicator: String
    let phrase: String
    let detail: String
    let checkedAt: Date?

    private var symbol: String { ServiceGlyph.symbol(for: indicator) }

    private var color: Color { Brandkit.tone(ServiceGlyph.tone(for: indicator)) }

    private var tooltip: String {
        let when = checkedAt.map {
            "last checked " + DateFormatter.localizedString(
                from: $0, dateStyle: .none, timeStyle: .short)
        } ?? "not checked yet"
        return "\(phrase) · \(when)"
    }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(color)
            TrackBadge(provider: provider, size: 16)
            Text(provider)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Brandkit.chalk)
            Spacer()
            Text(detail)
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Brandkit.steel)
                .lineLimit(1)
        }
        .help(tooltip)
    }
}

struct DashboardData {
    var range = 14
    var availability = ProviderAvailability.detect()
    var focus = Config.autoProvider
    var trends: [ProviderTrend] = []
    var hourly: [ProviderTrend] = []
    var models: [ModelShare] = []
    var limits: [LimitWindow] = []
    /// Burn rate and projection per window, computed by the app from stored readings
    var paces: [Pace] = []
    /// Setup findings, refreshed in the background rather than on every open
    var findings: FindingsReport?
    /// True while a findings scan is running. The button's own state is the feedback: a
    /// rescan that finishes in a second used to look like a button that did nothing.
    var findingsScanning = false
    /// What the local warehouse holds. Separate from the charts on purpose: the charts are
    /// what the transcripts still say, this is what was recorded before they were pruned.
    var history: HistorySummary?

    struct HistorySummary {
        var days = 0
        var earliest: String?
        var latest: String?
        var tokens = 0
        var cost = 0.0
        var complete = true
        var sizeBytes: Int64 = 0
    }
    var services: [Snapshot.Service] = []
    var servicesCheckedAt: Date?
    var theme = Config.load().dashboardTheme
    /// Why Claude's rails may be missing (rate limited, no token); shown so an empty
    /// panel never reads as silently broken
    var limitsNote: String?
    /// When Claude's windows were last true. The statusline feed only writes while Claude
    /// Code runs, so this can trail the rest of the dashboard; old rails drain to steel.
    var claudeLimitsAsOf: Date?
    var visibleServices: [Snapshot.Service] { services.filter { matches($0.provider) } }
    var today = Agg()
    /// Totals over the selected range, not a fixed week. Named for what it is so a future
    /// edit cannot read it as seven days again.
    var ranged = Agg()
    var loading = true
    var scannedAt: Date?
    var ollamaReachableHint = false

    var focusingAll: Bool { focus == Config.autoProvider }

    func matches(_ provider: String) -> Bool {
        focusingAll || provider.caseInsensitiveCompare(focus) == .orderedSame
    }

    /// Totals narrowed to the focused provider. Global figures while focused on one track
    /// read as that track's usage, which is how Ollama appeared to have spent thousands.
    struct Slice {
        var io = 0
        var cost = 0.0
        var cacheRead = 0
        var hasUnpriced = false
    }

    func slice(_ agg: Agg) -> Slice {
        guard !focusingAll else {
            return Slice(io: agg.io, cost: agg.cost, cacheRead: agg.cacheRead,
                         hasUnpriced: agg.hasUnpriced)
        }
        guard let usage = agg.providers.first(where: {
            $0.key.caseInsensitiveCompare(focus) == .orderedSame
        })?.value else { return Slice() }
        return Slice(io: usage.io, cost: usage.cost, cacheRead: usage.cacheRead,
                     hasUnpriced: usage.models.values.contains { !$0.priced })
    }

    var todaySlice: Slice { slice(today) }
    var rangedSlice: Slice { slice(ranged) }

    /// "7 days", "14 days", "30 days". One source for every label that names the window.
    var rangeLabel: String { "\(range) days" }

    var visibleTrends: [ProviderTrend] { trends.filter { matches($0.provider) } }
    var visibleHourly: [ProviderTrend] { hourly.filter { matches($0.provider) } }
    var visibleModels: [ModelShare] { models.filter { matches($0.provider) } }
    // Unnamed windows at zero are dropped here too, matching the menu
    var visibleLimits: [LimitWindow] {
        limits.filter { !$0.isUninformative && matches($0.provider) }
    }

    func pace(for window: LimitWindow) -> Pace? {
        paces.first { $0.provider == window.provider && $0.key == window.key }
    }
}

// Not @MainActor: AppDelegate drives it from its own non-isolated methods. Published
// changes are dispatched to main explicitly instead, which SwiftUI requires.
final class DashboardModel: ObservableObject {
    @Published var data = DashboardData()
    /// Set by the app: forces a status re-fetch past the 15 minute throttle
    var onStatusRefresh: (() -> Void)?
    /// Installs the Claude usage feed from the dashboard's empty Limits state, so the fix
    /// lives where the gap is noticed rather than only in the menu.
    var onSetupClaudeTracking: (() -> Void)?
    /// Set by the app: repaints the window for a theme choice. SwiftUI's preferredColorScheme
    /// only reached the window on the next state change, so following the OS again left the
    /// content on the old theme until the window lost focus.
    var onThemeChange: ((String) -> Void)?
    /// Set by the app: runs the findings checks again on demand
    var onRescanFindings: (() -> Void)?
    private let claude = UsageStore()
    private let codex = CodexStore()
    private let ollama = OllamaStore()
    private let queue = DispatchQueue(label: "dashboard-scan", qos: .userInitiated)
    /// Bumped on every load. A scan the user has already moved past must not publish
    /// over the newer one, or picking 30 days shows 14 days until the next reload.
    private var generation = 0

    /// Read on the scan thread with everything else, since it is another file walk.
    static func historySummary() -> DashboardData.HistorySummary? {
        let warehouse = Warehouse()
        let records = warehouse.load()
        guard !records.isEmpty else { return nil }
        let byDay = Warehouse.byDay(records)
        return DashboardData.HistorySummary(
            days: byDay.count,
            earliest: byDay.first?.day,
            latest: byDay.last?.day,
            tokens: byDay.reduce(0) { $0 + $1.io },
            cost: byDay.reduce(0) { $0 + $1.cost },
            complete: records.allSatisfy(\.priced),
            sizeBytes: warehouse.sizeBytes)
    }

    func setFocus(_ provider: String) {
        data.focus = provider
    }

    func setTheme(_ theme: String) {
        guard Config.write(["dashboardTheme": theme]) else { return }
        // Window first, then the published value: the tokens resolve against the window's
        // appearance at draw time, so a redraw ordered the other way paints the old theme
        onThemeChange?(theme)
        data.theme = theme
    }

    func load(range: Int, limits: [LimitWindow]) {
        generation += 1
        let gen = generation
        data.range = range
        data.loading = true
        data.limits = limits
        let cfg = Config.load()
        let days = range
        let detected = ProviderAvailability.detect(
            ollamaReachable: data.ollamaReachableHint)
        data.availability = detected
        // With one track there is nothing to aggregate across, so focus it automatically
        if !detected.hasChoice, let only = detected.installed.first {
            data.focus = only
        }
        queue.async { [weak self] in
            guard let self else { return }
            var entries: [Entry] = []
            if cfg.wants(UsageStore.provider) {
                entries += self.claude.scan(lookbackDays: days)
            }
            if cfg.wants(CodexStore.provider) {
                entries += self.codex.scan(lookbackDays: days).entries
            }
            if cfg.wants(OllamaStore.provider) {
                entries += self.ollama.scan(lookbackDays: days)
            }
            let now = Date()
            // One cutoff drives every ranged figure. Separate hardcoded windows are how the
            // tiles and the model mix stayed on 7 days while the charts moved to 14 or 30.
            let since = now.addingTimeInterval(-Double(days) * 86400)
            let trends = Trends.trend(entries, by: .day, count: days, now: now, config: cfg)
            let hourly = Trends.trend(entries, by: .hour, count: 24, now: now, config: cfg)
            let models = Trends.byModel(entries, since: since, config: cfg)
            let today = aggregate(entries, since: Calendar.current.startOfDay(for: now),
                                 config: cfg)
            let ranged = aggregate(entries, since: since, config: cfg)
            let history = cfg.recordHistory ? Self.historySummary() : nil
            DispatchQueue.main.async {
                guard gen == self.generation else { return }
                self.data.trends = trends
                self.data.hourly = hourly
                self.data.models = models
                self.data.today = today
                self.data.ranged = ranged
                self.data.scannedAt = now
                self.data.history = history
                self.data.loading = false
            }
        }
    }
}

// MARK: - Building blocks

private struct Panel<Content: View>: View {
    let title: String
    var note: String? = nil
    var badge: String? = nil
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center) {
                if let badge { TrackBadge(provider: badge, size: 19) }
                Text(title.uppercased())
                    .font(.system(size: 13, weight: .medium, design: .monospaced))
                    .tracking(1.6)
                    .foregroundStyle(Brandkit.steel)
                Spacer()
                if let note {
                    Text(note)
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                }
            }
            content
        }
        .padding(16)
        .background(Brandkit.graphite, in: RoundedRectangle(cornerRadius: 12))
    }
}

private struct LimitRailRow: View {
    let window: LimitWindow
    let yellow: Double
    let red: Double
    /// Burn rate and projection, when there is enough to say something. Nil is common and
    /// means the row simply says less rather than guessing.
    var pace: Pace? = nil
    /// When this window was last true; nil means live. Stale rails drain to steel and carry
    /// their timestamp in amber, so an old reading can never impersonate a current one.
    var asOf: Date? = nil

    private var stale: Bool {
        guard let asOf else { return false }
        return Date().timeIntervalSince(asOf) > 900
    }

    private var status: Brand.Status {
        Brand.status(for: window.utilization, approachingPct: yellow, atLimitPct: red)
    }

    private var valueColor: Color {
        stale ? Brandkit.steel : BrandUI.color(forStatus: status)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 7) {
                TrackBadge(provider: window.provider, size: 19)
                Text("\(window.provider) · \(window.displayName)")
                    .font(.system(size: 14))
                    .foregroundStyle(Brandkit.chalk)
                Spacer()
                if stale, let asOf {
                    Text("as of \(asOf.formatted(date: .omitted, time: .shortened))")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(BrandUI.amber)
                }
                Text("\(Int(window.utilization.rounded()))% used")
                    .font(.system(size: 14, design: .monospaced))
                    .foregroundStyle(valueColor)
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Brandkit.carbon)
                    Capsule()
                        .fill(valueColor)
                        .frame(width: max(2, geo.size.width * window.utilization / 100))
                    // The limit itself, always at the end of the rail
                    Rectangle()
                        .fill(Brandkit.signal)
                        .frame(width: 2)
                        .offset(x: geo.size.width - 2)
                    // Where the clock has got to. Level with the fill means the window is
                    // being spent at exactly the rate it refills; ahead of it means it runs
                    // out early. No arithmetic required to see which.
                    if !stale, let elapsed = pace?.elapsedFraction, elapsed > 0, elapsed < 1 {
                        Rectangle()
                            .fill(Brandkit.chalk.opacity(0.65))
                            .frame(width: 1)
                            .offset(x: geo.size.width * elapsed)
                            .help("where the clock is: \(Int((elapsed * 100).rounded()))% "
                                  + "of this window has passed")
                    }
                }
            }
            .frame(height: 8)
            HStack(spacing: 8) {
                if let r = window.resetsAt {
                    Text("Resets \(r.formatted(date: .abbreviated, time: .shortened))")
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                }
                if !stale, let pace, let summary = pace.summary() {
                    Text("· " + summary)
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(pace.hitsLimitBeforeReset ? BrandUI.amber
                                                                   : Brandkit.steel)
                        .help(pace.basisNote)
                }
                Spacer()
            }
        }
    }
}

/// One finding: a labelled header line, a sentence of prose held to a readable measure, and
/// the evidence as two columns so names read down one edge and numbers down the other.
/// A finding without a figure shows no figure; inventing one to fill the space is the
/// failure this panel exists to avoid.
private struct FindingRow: View {
    let finding: Finding
    /// Prose and evidence stop here rather than running the width of the window. Past about
    /// this, a line is measured in inches and read in guesses.
    private let measure: CGFloat = 760

    private var kindColor: Color {
        switch finding.kind {
        case .fixNow: return BrandUI.amber
        case .habit:  return BrandUI.clear
        case .fyi:    return Brandkit.steel
        }
    }

    private var visible: [Finding.Evidence] { Array(finding.evidence.prefix(6)) }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            // The kind, as a rule down the side rather than another word to read
            Capsule()
                .fill(kindColor.opacity(0.55))
                .frame(width: 3)
            VStack(alignment: .leading, spacing: 8) {
                header
                Text(finding.detail)
                    .font(.system(size: 12))
                    .foregroundStyle(Brandkit.steel)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: measure, alignment: .leading)
                if !visible.isEmpty { evidence }
                if let fix = finding.fix {
                    HStack(alignment: .top, spacing: 6) {
                        Image(systemName: "arrow.turn.down.right")
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundStyle(kindColor.opacity(0.8))
                            .padding(.top, 2)
                        Text(fix)
                            .font(.system(size: 12))
                            .foregroundStyle(Brandkit.chalk.opacity(0.9))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxWidth: measure, alignment: .leading)
                }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(finding.kind.label.uppercased())
                .font(.system(size: 9, weight: .bold, design: .monospaced))
                .tracking(1.0)
                .foregroundStyle(kindColor)
                .padding(.horizontal, 6)
                .padding(.vertical, 2)
                .background(kindColor.opacity(0.14), in: Capsule())
            Text(finding.title)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(Brandkit.chalk)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 12)
            // The label rides with the number, so a figure is never read without its basis
            Text(finding.basis == .measured ? "counted" : "estimated")
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(Brandkit.steel)
                .help(finding.basis == .measured
                      ? "counted from your transcripts"
                      : "estimated; the assumptions are in the sentence below")
            if let usd = finding.estimatedUSD {
                Text("~" + fmtCost(usd))
                    .font(.system(size: 13, weight: .medium, design: .monospaced))
                    .foregroundStyle(Brandkit.money)
                    .help("estimated over measured counts, never a bill")
            }
        }
    }

    private var evidence: some View {
        VStack(alignment: .leading, spacing: 3) {
            ForEach(visible) { row in
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text(row.label)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(Brandkit.chalk.opacity(0.8))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer(minLength: 8)
                    if let value = row.value {
                        Text(value)
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                            .lineLimit(1)
                    }
                }
            }
            if finding.evidence.count > visible.count {
                Text("+\(finding.evidence.count - visible.count) more")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(Brandkit.steel.opacity(0.8))
            }
        }
        .frame(maxWidth: measure, alignment: .leading)
        .padding(.vertical, 5)
        .padding(.horizontal, 10)
        .background(Brandkit.carbon.opacity(0.45), in: RoundedRectangle(cornerRadius: 7))
    }
}

private struct StatTile: View {
    let label: String
    let value: String
    var sub: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(label.uppercased())
                .font(.system(size: 12, weight: .medium, design: .monospaced))
                .tracking(1.4)
                .foregroundStyle(Brandkit.steel)
            Text(value)
                .font(.system(size: 25, weight: .semibold, design: .monospaced))
                .foregroundStyle(Brandkit.chalk)
            if let sub {
                Text(sub)
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Charts

// Both daily charts share one x-axis cadence, or the same 30 day range reads as two
// different spans stacked on top of each other.
private enum DailyStride {
    static func days(for range: Int) -> Int {
        switch range {
        case ...7:   return 1
        case ...14:  return 2
        default:     return 5
        }
    }
}

/// What a hovered bucket actually held, drawn where the pointer is. Charts answer shape
/// questions well and value questions badly; this is the value question.
private struct ChartReadout: View {
    let title: String
    let rows: [(provider: String, value: String)]
    let total: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 11, weight: .medium, design: .monospaced))
                .foregroundStyle(Brandkit.chalk)
            ForEach(rows, id: \.provider) { row in
                HStack(spacing: 8) {
                    Circle()
                        .fill(Brandkit.chartColor(for: row.provider))
                        .frame(width: 6, height: 6)
                    Text(row.provider)
                        .font(.system(size: 11))
                        .foregroundStyle(Brandkit.steel)
                    Spacer(minLength: 10)
                    Text(row.value)
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(Brandkit.chalk)
                }
            }
            if let total, rows.count > 1 {
                Divider().overlay(Brandkit.steel.opacity(0.25))
                HStack(spacing: 8) {
                    Text("total")
                        .font(.system(size: 11))
                        .foregroundStyle(Brandkit.steel)
                    Spacer(minLength: 10)
                    Text(total)
                        .font(.system(size: 11, weight: .medium, design: .monospaced))
                        .foregroundStyle(Brandkit.chalk)
                }
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(minWidth: 150, alignment: .leading)
        .background(Brandkit.graphite, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8)
            .stroke(Brandkit.steel.opacity(0.25), lineWidth: 1))
        .shadow(color: .black.opacity(0.35), radius: 8, y: 3)
    }
}

/// Snaps the pointer to the nearest bucket and reports it back. Snapping rather than reading
/// the raw x position is what makes a one pixel wide gap between two bars answerable.
private struct ChartHover: ViewModifier {
    let starts: [Date]
    @Binding var hover: Date?

    func body(content: Content) -> some View {
        content.chartOverlay { proxy in
            GeometryReader { geo in
                Rectangle()
                    .fill(.clear)
                    .contentShape(Rectangle())
                    .onContinuousHover { phase in
                        switch phase {
                        case .active(let point):
                            guard let plot = proxy.plotFrame else { return }
                            let x = point.x - geo[plot].origin.x
                            guard let date: Date = proxy.value(atX: x) else { return }
                            hover = starts.min {
                                abs($0.timeIntervalSince(date))
                                    < abs($1.timeIntervalSince(date))
                            }
                        case .ended:
                            hover = nil
                        }
                    }
            }
        }
    }
}

private extension View {
    func chartHover(over starts: [Date], hover: Binding<Date?>) -> some View {
        modifier(ChartHover(starts: starts, hover: hover))
    }
}

/// One bucket's rows, in the order the legend lists them, skipping providers that did
/// nothing in it: a row of zeros is noise in a readout that exists to answer "how much".
private func readoutRows(_ trends: [ProviderTrend], at start: Date,
                         value: (UsagePoint) -> Double,
                         format: (Double) -> String) -> [(provider: String, value: String)] {
    trends.compactMap { trend in
        guard let point = trend.points.first(where: { $0.start == start }) else { return nil }
        let amount = value(point)
        guard amount > 0 else { return nil }
        return (provider: trend.provider, value: format(amount))
    }
}

private struct DailyTokensChart: View {
    let trends: [ProviderTrend]
    let range: Int
    @State private var hover: Date?

    private struct Row: Identifiable {
        let id = UUID()
        let provider: String
        let start: Date
        let io: Int
    }

    private var rows: [Row] {
        trends.flatMap { t in t.points.map { Row(provider: t.provider, start: $0.start, io: $0.io) } }
    }

    private var starts: [Date] { trends.first?.points.map(\.start) ?? [] }

    var body: some View {
        Chart {
            ForEach(rows) { r in
                BarMark(x: .value("Day", r.start, unit: .day),
                        y: .value("Tokens", r.io))
                    .foregroundStyle(by: .value("Provider", r.provider))
                    .cornerRadius(4)
            }
            if let hover {
                RuleMark(x: .value("Day", hover, unit: .day))
                    .foregroundStyle(Brandkit.chalk.opacity(0.25))
                    .lineStyle(StrokeStyle(lineWidth: 1))
                    .annotation(position: .top, spacing: 6,
                                overflowResolution: .init(x: .fit(to: .chart), y: .fit(to: .chart))) {
                        ChartReadout(
                            title: hover.formatted(.dateTime.weekday(.abbreviated)
                                .month(.abbreviated).day()),
                            rows: readoutRows(trends, at: hover, value: { Double($0.io) },
                                              format: { fmtTokens(Int($0)) }),
                            total: fmtTokens(total(at: hover)))
                    }
            }
        }
        .chartForegroundStyleScale(domain: trends.map(\.provider),
                                   range: trends.map { Brandkit.chartFill(for: $0.provider) })
        .chartLegend(position: .top, alignment: .leading, spacing: 8)
        .chartYAxis {
            AxisMarks(position: .leading) { value in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.15))
                AxisValueLabel {
                    if let v = value.as(Int.self) {
                        Text(fmtTokens(v))
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                }
            }
        }
        .chartXAxis {
            AxisMarks(values: .stride(by: .day, count: DailyStride.days(for: range))) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day(),
                               centered: false)
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .chartHover(over: starts, hover: $hover)
        .frame(height: 215)
    }

    private func total(at start: Date) -> Int {
        trends.reduce(0) { $0 + ($1.points.first { $0.start == start }?.io ?? 0) }
    }
}

private struct DailyCostChart: View {
    let trends: [ProviderTrend]
    let range: Int
    @State private var hover: Date?

    private struct Row: Identifiable {
        let id = UUID()
        let provider: String
        let start: Date
        let cost: Double
    }

    private var rows: [Row] {
        trends.flatMap { t in
            t.points.map { Row(provider: t.provider, start: $0.start, cost: $0.cost) }
        }
    }

    private var starts: [Date] { trends.first?.points.map(\.start) ?? [] }

    var body: some View {
        Chart {
            ForEach(rows) { r in
                // The fill has to carry the series too. Styled with a flat colour it had no
                // series identity at all, so Charts treated every provider's points as one
                // series and closed the shape from the last Claude day back to the first
                // Codex day: a diagonal band across the panel that looked like data and was
                // not. Unstacked to match the lines drawn over it.
                AreaMark(x: .value("Day", r.start, unit: .day),
                         y: .value("Estimated cost", r.cost),
                         stacking: .unstacked)
                    .foregroundStyle(by: .value("Provider", r.provider))
                    .opacity(0.22)
                    .interpolationMethod(.monotone)
                LineMark(x: .value("Day", r.start, unit: .day),
                         y: .value("Estimated cost", r.cost))
                    .foregroundStyle(by: .value("Provider", r.provider))
                    .lineStyle(StrokeStyle(lineWidth: 2))
                    .interpolationMethod(.monotone)
            }
            if let hover {
                RuleMark(x: .value("Day", hover, unit: .day))
                    .foregroundStyle(Brandkit.chalk.opacity(0.25))
                    .lineStyle(StrokeStyle(lineWidth: 1))
                    .annotation(position: .top, spacing: 6,
                                overflowResolution: .init(x: .fit(to: .chart), y: .fit(to: .chart))) {
                        ChartReadout(
                            title: hover.formatted(.dateTime.weekday(.abbreviated)
                                .month(.abbreviated).day()),
                            rows: readoutRows(trends, at: hover, value: { $0.cost },
                                              format: { fmtCost($0) }),
                            total: fmtCost(total(at: hover)))
                    }
            }
        }
        .chartForegroundStyleScale(domain: trends.map(\.provider),
                                   range: trends.map { Brandkit.chartColor(for: $0.provider) })
        .chartLegend(position: .top, alignment: .leading, spacing: 8)
        .chartYAxis {
            AxisMarks(position: .leading) { value in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.15))
                AxisValueLabel {
                    if let v = value.as(Double.self) {
                        Text(fmtCost(v))
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                }
            }
        }
        .chartXAxis {
            AxisMarks(values: .stride(by: .day, count: DailyStride.days(for: range))) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day())
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .chartHover(over: starts, hover: $hover)
        .frame(height: 175)
    }

    private func total(at start: Date) -> Double {
        trends.reduce(0) { $0 + ($1.points.first { $0.start == start }?.cost ?? 0) }
    }
}

private struct HourlyChart: View {
    let trends: [ProviderTrend]
    @State private var hover: Date?

    private struct Row: Identifiable {
        let id = UUID()
        let provider: String
        let start: Date
        let io: Int
    }

    private var rows: [Row] {
        trends.flatMap { t in
            t.points.map { Row(provider: t.provider, start: $0.start, io: $0.io) }
        }
    }

    private var starts: [Date] { trends.first?.points.map(\.start) ?? [] }

    var body: some View {
        Chart {
            ForEach(rows) { r in
                BarMark(x: .value("Hour", r.start, unit: .hour),
                        y: .value("Tokens", r.io))
                    .foregroundStyle(by: .value("Provider", r.provider))
                    .cornerRadius(3)
            }
            if let hover {
                RuleMark(x: .value("Hour", hover, unit: .hour))
                    .foregroundStyle(Brandkit.chalk.opacity(0.25))
                    .lineStyle(StrokeStyle(lineWidth: 1))
                    .annotation(position: .top, spacing: 6,
                                overflowResolution: .init(x: .fit(to: .chart), y: .fit(to: .chart))) {
                        ChartReadout(
                            title: hover.formatted(.dateTime.hour().minute()),
                            rows: readoutRows(trends, at: hover, value: { Double($0.io) },
                                              format: { fmtTokens(Int($0)) }),
                            total: fmtTokens(total(at: hover)))
                    }
            }
        }
        .chartForegroundStyleScale(domain: trends.map(\.provider),
                                   range: trends.map { Brandkit.chartFill(for: $0.provider) })
        .chartLegend(position: .top, alignment: .leading, spacing: 8)
        .chartYAxis {
            AxisMarks(position: .leading) { value in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.15))
                AxisValueLabel {
                    if let v = value.as(Int.self) {
                        Text(fmtTokens(v))
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                }
            }
        }
        .chartXAxis {
            AxisMarks(values: .stride(by: .hour, count: 4)) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.hour())
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .chartHover(over: starts, hover: $hover)
        .frame(height: 155)
    }

    private func total(at start: Date) -> Int {
        trends.reduce(0) { $0 + ($1.points.first { $0.start == start }?.io ?? 0) }
    }
}

private struct ModelMix: View {
    let models: [ModelShare]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(models.prefix(8), id: \.model) { m in
                HStack(spacing: 8) {
                    TrackBadge(provider: m.provider, size: 18)
                    Text(OllamaLocality.marked(m.model))
                        .font(.system(size: 14, design: .monospaced))
                        .foregroundStyle(Brandkit.chalk)
                        .lineLimit(1)
                        .frame(width: 205, alignment: .leading)
                    GeometryReader { geo in
                        let maxIO = max(models.first?.io ?? 1, 1)
                        Capsule()
                            .fill(Brandkit.chartColor(for: m.provider))
                            .frame(width: max(2, geo.size.width * Double(m.io) / Double(maxIO)))
                    }
                    .frame(height: 8)
                    Text(fmtTokens(m.io))
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                        .frame(width: 68, alignment: .trailing)
                    // An unpriced model shows a dash, never a zero that reads as free
                    Text(m.priced ? fmtCost(m.cost) : "—")
                        .font(.system(size: 13, design: .monospaced))
                        .foregroundStyle(m.priced ? Brandkit.money : Brandkit.steel)
                        .lineLimit(1)
                        .frame(width: 96, alignment: .trailing)
                }
            }
            // The list answers "which model"; this row answers "how much altogether"
            Divider().overlay(Brandkit.steel.opacity(0.25))
            HStack(spacing: 8) {
                Text("Total")
                    .font(.system(size: 14, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Brandkit.chalk)
                    .frame(width: 231, alignment: .leading)
                Spacer()
                Text(fmtTokens(models.reduce(0) { $0 + $1.io }))
                    .font(.system(size: 13, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Brandkit.chalk)
                    .frame(width: 68, alignment: .trailing)
                // Wide enough for "$24,320.91+" on one line; the width once wrapped the
                // unpriced marker onto its own row, which read as a broken figure
                Text(fmtCost(models.filter(\.priced).reduce(0) { $0 + $1.cost })
                     + (models.contains { !$0.priced } ? "+" : ""))
                    .font(.system(size: 13, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Brandkit.money)
                    .lineLimit(1)
                    .frame(width: 96, alignment: .trailing)
            }
            if models.contains(where: { !$0.priced }) {
                Text("— means no pricing entry, so it is counted in tokens only")
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
    }
}

private struct OllamaPanel: View {
    @ObservedObject var service: OllamaService

    private var runningNames: Set<String> { Set(service.state.running.map(\.name)) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Panel(title: "Ollama", note: service.hostDescription,
                  badge: OllamaStore.provider) {
                if service.state.reachable {
                    HStack(spacing: 8) {
                        Circle().fill(Brandkit.clear).frame(width: 7, height: 7)
                        Text("Running\(service.state.version.map { " · v\($0)" } ?? "")")
                            .font(.system(size: 14))
                            .foregroundStyle(Brandkit.chalk)
                        Spacer()
                        Text("\(service.state.running.count) loaded · \(service.state.models.count) downloaded")
                            .font(.system(size: 13, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                } else {
                    HStack(spacing: 8) {
                        Circle().fill(Brandkit.steel).frame(width: 7, height: 7)
                        // Brand voice: state the fact, do not scold
                        Text(service.state.error ?? "Ollama is not running")
                            .font(.system(size: 14))
                            .foregroundStyle(Brandkit.chalk)
                        Spacer()
                        Text("start it with: ollama serve")
                            .font(.system(size: 13, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                }
            }

            if !service.state.running.isEmpty {
                Panel(title: "Loaded now", note: "in memory") {
                    VStack(spacing: 10) {
                        ForEach(service.state.running) { m in
                            HStack(spacing: 10) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(m.name)
                                        .font(.system(size: 14, design: .monospaced))
                                        .foregroundStyle(Brandkit.chalk)
                                    Text(vramNote(m))
                                        .font(.system(size: 13, design: .monospaced))
                                        .foregroundStyle(Brandkit.steel)
                                }
                                Spacer()
                                Button("Stop") {
                                    Task { await service.stop(m.name) }
                                }
                                .disabled(service.state.busy.contains(m.name))
                                .help("Unloads from memory. The download is kept.")
                            }
                        }
                    }
                }
            }

            Panel(title: "Downloaded", note: "\(service.state.models.count) models") {
                if service.state.models.isEmpty {
                    Text(service.state.reachable ? "No models downloaded"
                                                 : "Unavailable while Ollama is stopped")
                        .font(.system(size: 14, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                } else {
                    VStack(spacing: 8) {
                        ForEach(service.state.models) { m in
                            HStack(spacing: 10) {
                                Circle()
                                    .fill(runningNames.contains(m.name) ? Brandkit.clear
                                                                        : Brandkit.steel.opacity(0.4))
                                    .frame(width: 6, height: 6)
                                Text(m.name)
                                    .font(.system(size: 14, design: .monospaced))
                                    .foregroundStyle(Brandkit.chalk)
                                    .frame(width: 230, alignment: .leading)
                                    .lineLimit(1)
                                Text(detail(m))
                                    .font(.system(size: 13, design: .monospaced))
                                    .foregroundStyle(Brandkit.steel)
                                Spacer()
                                if runningNames.contains(m.name) {
                                    Button("Stop") { Task { await service.stop(m.name) } }
                                        .disabled(service.state.busy.contains(m.name))
                                } else {
                                    Button("Start") { Task { await service.start(m.name) } }
                                        .disabled(!service.state.reachable
                                                  || service.state.busy.contains(m.name))
                                        .help("Loads the model into memory, ready to answer")
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private func vramNote(_ m: OllamaRunningModel) -> String {
        let share = Int((m.vramShare * 100).rounded())
        let expiry = m.expiresAt.map {
            " · unloads \($0.formatted(date: .omitted, time: .shortened))"
        } ?? ""
        return "\(fmtBytes(m.sizeBytes)) · \(share)% on GPU\(expiry)"
    }

    private func detail(_ m: OllamaModel) -> String {
        [m.parameterSize, m.quantization, fmtBytes(m.sizeBytes)]
            .compactMap { $0 }.joined(separator: " · ")
    }
}

// MARK: - Window content

struct DashboardView: View {
    @ObservedObject var model: DashboardModel
    @ObservedObject var ollama: OllamaService
    let onReload: (Int) -> Void
    let onFocus: (String) -> Void
    private let ranges = [7, 14, 30]

    private var data: DashboardData { model.data }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                header

                if data.loading {
                    Panel(title: "Reading transcripts") {
                        HStack(spacing: 10) {
                            ProgressView().controlSize(.small)
                            Text("Scanning \(data.range) days of usage")
                                .font(.system(size: 14, design: .monospaced))
                                .foregroundStyle(Brandkit.steel)
                        }
                    }
                } else {
                    tiles
                    // Limits before service status: the limit rails are the product's
                    // headline, and the menu already leads with them.
                    limitsPanel
                    if !data.visibleServices.isEmpty {
                        Panel(title: "Service status", note: "as reported by each operator") {
                            VStack(alignment: .leading, spacing: 10) {
                                ForEach(data.visibleServices, id: \.provider) { s in
                                    ServiceStatusRow(provider: s.provider,
                                                     indicator: s.indicator,
                                                     phrase: s.phrase,
                                                     detail: s.description,
                                                     checkedAt: data.servicesCheckedAt)
                                }
                                HStack(spacing: 8) {
                                    Button {
                                        model.onStatusRefresh?()
                                    } label: {
                                        Label("Check now", systemImage: "arrow.clockwise")
                                            .font(.system(size: 11))
                                    }
                                    .buttonStyle(.plain)
                                    .foregroundStyle(Brandkit.steel)
                                    .help("Re-check every status immediately")
                                    if let at = data.servicesCheckedAt {
                                        Text("last checked " + DateFormatter.localizedString(
                                            from: at, dateStyle: .none, timeStyle: .short))
                                            .font(.system(size: 11, design: .monospaced))
                                            .foregroundStyle(Brandkit.steel)
                                    }
                                    Spacer()
                                }
                            }
                        }
                    }
                    findingsPanel
                    if data.focus == OllamaStore.provider {
                        OllamaPanel(service: ollama)
                    }
                    Panel(title: "Tokens per day", note: data.rangeLabel) {
                        if data.visibleTrends.isEmpty { empty } else {
                            DailyTokensChart(trends: data.visibleTrends, range: data.range)
                        }
                    }
                    Panel(title: "Estimated cost per day",
                          note: "\(data.rangeLabel) · configured pricing") {
                        if data.visibleTrends.isEmpty { empty } else {
                            DailyCostChart(trends: data.visibleTrends, range: data.range)
                        }
                    }
                    Panel(title: "Last 24 hours") {
                        if data.visibleHourly.isEmpty { empty } else {
                            HourlyChart(trends: data.visibleHourly)
                        }
                    }
                    Panel(title: "Models", note: "last \(data.rangeLabel)") {
                        if data.visibleModels.isEmpty { empty } else {
                            ModelMix(models: data.visibleModels)
                        }
                    }
                    historyPanel
                }
            }
            .padding(16)
        }
        .background(Brandkit.carbon)
        // The theme is applied to the window itself, in AppDelegate. preferredColorScheme
        // here would fight it and reintroduce the lag on returning to "follow the OS".
    }

    /// The rails, or a way to get them. An empty panel with only an error string was a dead
    /// end exactly where a new user needs a door; the setup button is the door.
    @ViewBuilder
    private var limitsPanel: some View {
        if !data.visibleLimits.isEmpty || data.limitsNote != nil {
            Panel(title: "Limits", note: "the red line marks the limit") {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(data.visibleLimits) { window in
                        LimitRailRow(window: window, yellow: 60, red: 85,
                                     pace: data.pace(for: window),
                                     asOf: window.provider == "Claude"
                                         ? data.claudeLimitsAsOf : nil)
                    }
                    if let note = data.limitsNote {
                        Text(note)
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Brandkit.steel)
                    }
                    // No Claude rails and no feed installed: offer the recommended fix
                    // right here rather than sending the user hunting through the menu
                    if data.matches("Claude"),
                       !data.visibleLimits.contains(where: { $0.provider == "Claude" }),
                       !StatuslineInstaller.isInstalled(),
                       data.availability.has("Claude") {
                        Button {
                            model.onSetupClaudeTracking?()
                        } label: {
                            Label("Set Up Claude Tracking…", systemImage: "waveform.path")
                                .font(.system(size: 12))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(Brandkit.chalk)
                        .help("Reads the windows Claude Code hands its statusline. "
                              + "No sign-in, no Keychain, no network.")
                    }
                }
            }
        }
    }

    /// Findings are about Claude Code's setup, so they are shown when Claude is in view.
    /// An empty report still gets a panel: "nothing found" is an answer, and hiding the
    /// panel would leave a user unsure whether it had ever run.
    @ViewBuilder
    private var findingsPanel: some View {
        if data.matches("Claude"), let report = data.findings {
            Panel(title: "Findings",
                  note: "\(report.sessionsScanned) sessions · \(report.windowDays) days") {
                VStack(alignment: .leading, spacing: 0) {
                    if report.isEmpty {
                        Text("Nothing worth changing in this window.")
                            .font(.system(size: 13))
                            .foregroundStyle(Brandkit.steel)
                            .padding(.bottom, 14)
                    } else {
                        ForEach(Array(report.findings.enumerated()), id: \.element.id) {
                            index, finding in
                            if index > 0 {
                                Divider()
                                    .overlay(Brandkit.steel.opacity(0.18))
                                    .padding(.vertical, 14)
                            }
                            FindingRow(finding: finding)
                        }
                        Divider()
                            .overlay(Brandkit.steel.opacity(0.18))
                            .padding(.vertical, 14)
                    }
                    footer(report)
                }
            }
        }
    }

    /// The one caveat that applies to every row, said once, next to the control that
    /// regenerates them.
    private func footer(_ report: FindingsReport) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("Estimates over measured counts, never a bill. "
                 + "A finding with nothing honest to put on it carries no figure.")
                .font(.system(size: 11))
                .foregroundStyle(Brandkit.steel.opacity(0.85))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 16)
            if data.findingsScanning {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small).scaleEffect(0.7)
                    Text("Scanning")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                }
            } else {
                Button {
                    model.onRescanFindings?()
                } label: {
                    Label("Scan again", systemImage: "arrow.clockwise")
                        .font(.system(size: 11))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Brandkit.chalk.opacity(0.85))
                .help("Reads the transcripts again now, rather than waiting for the "
                      + "background pass")
            }
            Text(report.generatedAt.formatted(date: .omitted, time: .standard))
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(Brandkit.steel)
                .help("when this report was generated")
        }
    }

    /// What has been recorded, as opposed to what can still be read. The two diverge the
    /// moment Claude Code prunes a transcript, and this panel is the only place that says so.
    @ViewBuilder
    private var historyPanel: some View {
        if let history = data.history, history.days > 0 {
            Panel(title: "Recorded history", note: "kept locally, UTC days") {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 22) {
                        StatTile(label: "Days", value: "\(history.days)",
                                 sub: [history.earliest, history.latest]
                                    .compactMap { $0 }.joined(separator: " to "))
                        StatTile(label: "Tokens", value: fmtTokens(history.tokens))
                        StatTile(label: "Estimated cost",
                                 value: fmtCost(history.cost) + (history.complete ? "" : "+"),
                                 sub: history.complete ? nil : "some models have no price")
                        Spacer()
                    }
                    Text("Recorded as RedLine polls, so it survives Claude Code's own "
                         + "transcript cleanup. \(ByteCountFormatter.string(fromByteCount: history.sizeBytes, countStyle: .file)) on disk.")
                        .font(.system(size: 11))
                        .foregroundStyle(Brandkit.steel)
                }
            }
        }
    }

    @ViewBuilder
    private var empty: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("No usage recorded in this range")
                .font(.system(size: 14, design: .monospaced))
                .foregroundStyle(Brandkit.steel)
            // Ollama keeps no history of its own, so say how to start collecting it
            if data.focus == OllamaStore.provider {
                Text("Use Set Up Ollama Tracking in the menu to record calls")
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel.opacity(0.8))
            }
        }
        .frame(height: 60, alignment: .topLeading)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                RedlineMark(size: 31)
                VStack(alignment: .leading, spacing: 1) {
                    // Wordmark specs follow brand/logo/redline-wordmark-dark.svg
                    Text("RedLine")
                        .font(.system(size: 24, weight: .bold))
                        .tracking(-0.7)
                        .foregroundStyle(Brandkit.chalk)
                    Text("Know your limit.")
                        .font(.system(size: 13, design: .monospaced))
                        .tracking(0.8)
                        .foregroundStyle(Brandkit.steel)
                }
                Spacer()
                Text(data.scannedAt.map {
                    "Updated \($0.formatted(date: .omitted, time: .standard))"
                } ?? "Reading…")
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
                Button {
                    onReload(data.range)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .foregroundStyle(Brandkit.steel)
                .help("Rescan now")
            }
            // The red rule under the wordmark, as in the supplied lockup
            Rectangle()
                .fill(Brandkit.signal)
                .frame(width: 132, height: 3)
                .clipShape(Capsule())

            HStack(spacing: 10) {
                HStack(spacing: 7) {
                    TrackBadge(provider: data.focusingAll ? nil : data.focus, size: 23)
                    if data.availability.hasChoice {
                        Picker("", selection: Binding(
                            get: { data.focus },
                            set: { onFocus($0) }
                        )) {
                            ForEach(data.availability.trackChoices, id: \.self) { choice in
                                Text(choice == Config.autoProvider ? "All providers" : choice)
                                    .tag(choice)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 180)
                        .help("Show one provider, or all of them")
                    } else if let only = data.availability.installed.first {
                        // Nothing to choose between, so state the track rather than offer it
                        Text(only)
                            .font(.system(size: 14, weight: .medium))
                            .foregroundStyle(Brandkit.chalk)
                    }
                }

                Spacer()

                // Theme: auto follows the OS, light and dark force the window. The choice
                // persists in the config like every other preference.
                HStack(spacing: 4) {
                    ForEach([("auto", "circle.lefthalf.filled"),
                             ("light", "sun.max.fill"),
                             ("dark", "moon.fill")], id: \.0) { value, icon in
                        Button {
                            model.setTheme(value)
                        } label: {
                            Image(systemName: icon)
                                .font(.system(size: 12, weight: .medium))
                                .frame(width: 32, height: 25)
                                .background(value == data.theme
                                            ? Brandkit.signal.opacity(0.22)
                                            : Brandkit.graphite)
                                .foregroundStyle(value == data.theme ? Brandkit.chalk
                                                                     : Brandkit.steel)
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                        }
                        .buttonStyle(.plain)
                        .help("Appearance: \(value)")
                    }
                }
                .padding(.trailing, 10)

                // Segmented controls take the system accent, so drive the range with plain
                // buttons that can carry the brand tint instead
                HStack(spacing: 4) {
                    ForEach(ranges, id: \.self) { r in
                        Button {
                            onReload(r)
                        } label: {
                            Text("\(r)d")
                                .font(.system(size: 14, weight: .medium, design: .monospaced))
                                .frame(width: 40, height: 25)
                                .background(r == data.range ? Brandkit.signal.opacity(0.22)
                                                            : Brandkit.graphite)
                                .foregroundStyle(r == data.range ? Brandkit.chalk
                                                                 : Brandkit.steel)
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private var tiles: some View {
        // Labels name the track so a focused figure cannot be read as the global one
        let scope = data.focusingAll ? "" : " · \(data.focus)"
        let today = data.todaySlice
        let ranged = data.rangedSlice
        let peak = data.visibleTrends.compactMap(\.peak).map(\.io).max()
        return HStack(spacing: 12) {
            StatTile(label: "Today\(scope)", value: fmtTokens(today.io),
                     sub: "\(fmtCost(today.cost))\(today.hasUnpriced ? "+" : "") est")
            StatTile(label: "Last \(data.rangeLabel)\(scope)", value: fmtTokens(ranged.io),
                     sub: "\(fmtCost(ranged.cost))\(ranged.hasUnpriced ? "+" : "") est")
            StatTile(label: "Cache read", value: fmtTokens(ranged.cacheRead),
                     sub: data.rangeLabel)
            StatTile(label: "Busiest day",
                     value: peak.map(fmtTokens) ?? "—", sub: "in range")
        }
        .padding(16)
        .background(Brandkit.graphite, in: RoundedRectangle(cornerRadius: 12))
    }
}
