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
    var services: [Snapshot.Service] = []
    var servicesCheckedAt: Date?
    var theme = Config.load().dashboardTheme
    /// Why Claude's rails may be missing (rate limited, no token); shown so an empty
    /// panel never reads as silently broken
    var limitsNote: String?
    var visibleServices: [Snapshot.Service] { services.filter { matches($0.provider) } }
    var today = Agg()
    var week = Agg()
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
    var weekSlice: Slice { slice(week) }

    var visibleTrends: [ProviderTrend] { trends.filter { matches($0.provider) } }
    var visibleHourly: [ProviderTrend] { hourly.filter { matches($0.provider) } }
    var visibleModels: [ModelShare] { models.filter { matches($0.provider) } }
    // Unnamed windows at zero are dropped here too, matching the menu
    var visibleLimits: [LimitWindow] {
        limits.filter { !$0.isUninformative && matches($0.provider) }
    }
}

// Not @MainActor: AppDelegate drives it from its own non-isolated methods. Published
// changes are dispatched to main explicitly instead, which SwiftUI requires.
final class DashboardModel: ObservableObject {
    @Published var data = DashboardData()
    /// Set by the app: forces a status re-fetch past the 15 minute throttle
    var onStatusRefresh: (() -> Void)?
    private let claude = UsageStore()
    private let codex = CodexStore()
    private let ollama = OllamaStore()
    private let queue = DispatchQueue(label: "dashboard-scan", qos: .userInitiated)

    func setFocus(_ provider: String) {
        data.focus = provider
    }

    func setTheme(_ theme: String) {
        guard Config.write(["dashboardTheme": theme]) else { return }
        data.theme = theme
    }

    func load(range: Int, limits: [LimitWindow]) {
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
            let trends = Trends.trend(entries, by: .day, count: days, now: now, config: cfg)
            let hourly = Trends.trend(entries, by: .hour, count: 24, now: now, config: cfg)
            let models = Trends.byModel(entries,
                                        since: now.addingTimeInterval(-7 * 86400), config: cfg)
            let today = aggregate(entries, since: Calendar.current.startOfDay(for: now),
                                 config: cfg)
            let week = aggregate(entries, since: now.addingTimeInterval(-7 * 86400),
                                 config: cfg)
            DispatchQueue.main.async {
                self.data.trends = trends
                self.data.hourly = hourly
                self.data.models = models
                self.data.today = today
                self.data.week = week
                self.data.scannedAt = now
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

    private var status: Brand.Status {
        Brand.status(for: window.utilization, approachingPct: yellow, atLimitPct: red)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 7) {
                TrackBadge(provider: window.provider, size: 19)
                Text("\(window.provider) · \(window.displayName)")
                    .font(.system(size: 14))
                    .foregroundStyle(Brandkit.chalk)
                Spacer()
                Text("\(Int(window.utilization.rounded()))% used")
                    .font(.system(size: 14, design: .monospaced))
                    .foregroundStyle(BrandUI.color(forStatus: status))
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Brandkit.carbon)
                    Capsule()
                        .fill(BrandUI.color(forStatus: status))
                        .frame(width: max(2, geo.size.width * window.utilization / 100))
                    // The limit itself, always at the end of the rail
                    Rectangle()
                        .fill(Brandkit.signal)
                        .frame(width: 2)
                        .offset(x: geo.size.width - 2)
                }
            }
            .frame(height: 8)
            if let r = window.resetsAt {
                Text("Resets \(r.formatted(date: .abbreviated, time: .shortened))")
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
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

private struct DailyTokensChart: View {
    let trends: [ProviderTrend]
    let range: Int

    private struct Row: Identifiable {
        let id = UUID()
        let provider: String
        let start: Date
        let io: Int
    }

    private var rows: [Row] {
        trends.flatMap { t in t.points.map { Row(provider: t.provider, start: $0.start, io: $0.io) } }
    }

    var body: some View {
        Chart(rows) { r in
            BarMark(x: .value("Day", r.start, unit: .day),
                    y: .value("Tokens", r.io))
                .foregroundStyle(by: .value("Provider", r.provider))
                .cornerRadius(4)
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
            AxisMarks(values: .stride(by: .day, count: range > 14 ? 5 : 2)) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day(),
                               centered: false)
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .frame(height: 215)
    }
}

private struct DailyCostChart: View {
    let trends: [ProviderTrend]

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

    var body: some View {
        Chart(rows) { r in
            AreaMark(x: .value("Day", r.start, unit: .day),
                     y: .value("Estimated cost", r.cost))
                .foregroundStyle(Brandkit.chartColor(for: r.provider).opacity(0.22))
                .interpolationMethod(.monotone)
            LineMark(x: .value("Day", r.start, unit: .day),
                     y: .value("Estimated cost", r.cost))
                .foregroundStyle(by: .value("Provider", r.provider))
                .lineStyle(StrokeStyle(lineWidth: 2))
                .interpolationMethod(.monotone)
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
            AxisMarks(values: .stride(by: .day, count: 3)) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day())
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .frame(height: 175)
    }
}

private struct HourlyChart: View {
    let trends: [ProviderTrend]

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

    var body: some View {
        Chart(rows) { r in
            BarMark(x: .value("Hour", r.start, unit: .hour),
                    y: .value("Tokens", r.io))
                .foregroundStyle(by: .value("Provider", r.provider))
                .cornerRadius(3)
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
        .frame(height: 155)
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
                        .foregroundStyle(m.priced ? Brandkit.chalk : Brandkit.steel)
                        .frame(width: 78, alignment: .trailing)
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
                Text(fmtCost(models.filter(\.priced).reduce(0) { $0 + $1.cost })
                     + (models.contains { !$0.priced } ? "+" : ""))
                    .font(.system(size: 13, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Brandkit.chalk)
                    .frame(width: 78, alignment: .trailing)
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
                    if !data.visibleLimits.isEmpty || data.limitsNote != nil {
                        Panel(title: "Limits", note: "limit at the line") {
                            VStack(alignment: .leading, spacing: 12) {
                                ForEach(data.visibleLimits) {
                                    LimitRailRow(window: $0, yellow: 60, red: 85)
                                }
                                if let note = data.limitsNote {
                                    Text(note)
                                        .font(.system(size: 12, design: .monospaced))
                                        .foregroundStyle(Brandkit.steel)
                                }
                            }
                        }
                    }
                    if data.focus == OllamaStore.provider {
                        OllamaPanel(service: ollama)
                    }
                    Panel(title: "Tokens per day", note: "\(data.range) days") {
                        if data.visibleTrends.isEmpty { empty } else {
                            DailyTokensChart(trends: data.visibleTrends, range: data.range)
                        }
                    }
                    Panel(title: "Estimated cost per day", note: "configured pricing") {
                        if data.visibleTrends.isEmpty { empty } else {
                            DailyCostChart(trends: data.visibleTrends)
                        }
                    }
                    Panel(title: "Last 24 hours") {
                        if data.visibleHourly.isEmpty { empty } else {
                            HourlyChart(trends: data.visibleHourly)
                        }
                    }
                    Panel(title: "Models", note: "last 7 days") {
                        if data.visibleModels.isEmpty { empty } else {
                            ModelMix(models: data.visibleModels)
                        }
                    }
                }
            }
            .padding(16)
        }
        .background(Brandkit.carbon)
        // nil follows the OS; a forced scheme re-resolves every dynamic token in the window
        .preferredColorScheme(data.theme == "light" ? .light
                            : data.theme == "dark" ? .dark : nil)
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
        let week = data.weekSlice
        let peak = data.visibleTrends.compactMap(\.peak).map(\.io).max()
        return HStack(spacing: 12) {
            StatTile(label: "Today\(scope)", value: fmtTokens(today.io),
                     sub: "\(fmtCost(today.cost))\(today.hasUnpriced ? "+" : "") est")
            StatTile(label: "Last 7 days\(scope)", value: fmtTokens(week.io),
                     sub: "\(fmtCost(week.cost))\(week.hasUnpriced ? "+" : "") est")
            StatTile(label: "Cache read", value: fmtTokens(week.cacheRead),
                     sub: "7 days")
            StatTile(label: "Busiest day",
                     value: peak.map(fmtTokens) ?? "—", sub: "in range")
        }
        .padding(16)
        .background(Brandkit.graphite, in: RoundedRectangle(cornerRadius: 12))
    }
}
