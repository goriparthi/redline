// Dashboard window: charts over the same data the menu bar summarises. Scanning is done off
// the main thread because a 30 day range can touch a lot of transcript files.
import Charts
import RedlineCore
import RedlineUI
import SwiftUI

/// The dashboard's bridge to the design system.
///
/// Every colour here forwards to `RL` in RedlineCore, which is the single definition. This
/// layer exists for two things the tokens cannot do on their own: hand AppKit an NSColor for
/// the menu and the status item, and name the chart series.
enum Brandkit {
    // Surfaces and ink, straight through to the tokens
    static var carbon: Color { RL.Surface.ground }
    static var graphite: Color { RL.Surface.raised }
    static var well: Color { RL.Surface.sunken }
    static var chalk: Color { RL.Ink.primary }
    static var steel: Color { RL.Ink.muted }
    static var hairline: Color { RL.Stroke.hairline }
    static var signal: Color { RL.Brandmark.signal }
    static var amber: Color { RL.State.warning }
    static var clear: Color { RL.State.success }
    /// Money reads green by convention, and runs darker on the light side so it does not
    /// wash out against paper.
    static var money: Color { RL.Brandmark.money }

    /// The status trio, chosen by tone rather than by reading an indicator string twice
    static func tone(_ tone: ServiceGlyph.Tone) -> Color {
        RLStatus.forTone(tone).color
    }

    static func nsTone(_ tone: ServiceGlyph.Tone) -> NSColor {
        switch tone {
        case .healthy:  return RL.State.nsSuccess
        case .warning:  return RL.State.nsWarning
        case .critical: return RL.State.nsError
        case .unknown:  return RL.State.nsUnknown
        }
    }

    // Menus follow the system theme and cannot be painted, so these resolve per appearance:
    // light ink on dark menus, dark ink on light ones. Fixed chalk was invisible on light Macs.
    static var menuPrimary: NSColor { RL.Ink.nsPrimary }
    static var menuSecondary: NSColor { RL.Ink.nsSecondary }

    /// AppKit counterpart of a provider's accent, for the menu and the status item.
    static func nsColor(for provider: String) -> NSColor {
        ProviderIdentity.nsAccent(for: provider)
    }

    static func color(for provider: String) -> Color {
        ProviderIdentity.accent(for: provider)
    }

    /// Chart series take the provider's own accent, so a series and a card carry one identity.
    /// The accents are validated as a categorical set: one lightness band, separated by hue,
    /// still separable under protanopia, deuteranopia and tritanopia, and each kept clear of
    /// the signal red. The legend ties names to colours, so identity is never colour alone.
    static func chartColor(for provider: String) -> Color {
        ProviderIdentity.accent(for: provider)
    }

    /// Vertical fade for bar and area fills: full colour at the data end, quieter at the
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

    private var status: RLStatus {
        RLStatus.forTone(ServiceGlyph.tone(for: indicator), phrase: phrase)
    }

    private var tooltip: String {
        let when = checkedAt.map {
            "last checked " + DateFormatter.localizedString(
                from: $0, dateStyle: .none, timeStyle: .short)
        } ?? "not checked yet"
        return "\(phrase) · \(when)"
    }

    var body: some View {
        HStack(spacing: RL.Space.lg) {
            RLStatusIndicator(status, size: 14)
            ProviderTile(provider: provider, size: 18)
            Text(provider)
                .font(RL.Typography.subheading)
                .foregroundStyle(RL.Ink.primary)
            Text(phrase)
                .font(RL.Typography.caption)
                .foregroundStyle(RL.Ink.secondary)
            Spacer(minLength: RL.Space.md)
            Text(detail)
                .font(RL.Typography.monoSmall)
                .foregroundStyle(RL.Ink.muted)
                .lineLimit(1)
        }
        .help(tooltip)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(provider): \(phrase)")
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
    /// How the work is spread out: hour of day, the current run, consecutive days. Present
    /// only when the setting is on, because it is the one panel about your day rather than
    /// about an account.
    var cadence: CadenceSummary?
    /// Cues raised by the last poll, kept so the panel can show what was said.
    var cues: [CadenceCue] = []

    struct CadenceSummary {
        /// Tokens by local hour, 24 buckets starting at midnight
        var hours: [Int] = []
        var currentStretch: TimeInterval?
        var longestStretch: TimeInterval?
        var streak = 0
        var days = 0
    }

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
    /// The session window, so the Session (5h) rail has a volume beside its percentage.
    /// The rail says how much is gone; this says what it bought.
    var block5h = Agg()
    /// Rolling 24 hours, which is the window the hourly chart draws. Shown in that panel's
    /// header rather than as another tile, because the panel already names the window.
    var day24 = Agg()
    /// Totals over the selected range, not a fixed week. Named for what it is so a future
    /// edit cannot read it as seven days again.
    var ranged = Agg()
    var loading = true
    var scannedAt: Date?
    var ollamaReachableHint = false
    /// How often the app rescans, so the header can state the monitoring cadence rather
    /// than leaving the user to guess whether anything is still running.
    var pollSeconds: Double = 300
    /// Which providers the config actually reads. A provider that is installed but switched
    /// off has a card saying so, which is different from having no card at all.
    var readProviders: [String] = Config.knownProviders
    /// The thresholds from the config, so the rails, the cards and the notifications all
    /// agree on what "approaching" means. These were hardcoded at 60 and 85 in the rails.
    var yellowPct: Double = 60
    var redPct: Double = 85

    var focusingAll: Bool { focus == Config.autoProvider }

    /// Providers whose percentages are older than the staleness threshold. Only Claude can
    /// go stale: Codex's windows are read from disk with the rest of its scan.
    var staleProviders: Set<String> {
        guard let asOf = claudeLimitsAsOf,
              Date().timeIntervalSince(asOf) > ProviderOverview.stalenessThreshold else {
            return []
        }
        return ["Claude"]
    }

    /// One card per provider RedLine knows about, whether or not it is installed: a card
    /// saying "not found on this Mac" answers a question that a missing card leaves open.
    var providerCards: [ProviderCard] {
        Config.knownProviders.map { provider in
            let reads = readProviders.contains {
                $0.caseInsensitiveCompare(provider) == .orderedSame
            }
            let usage = ranged.providers.first {
                $0.key.caseInsensitiveCompare(provider) == .orderedSame
            }?.value
            let service = services.first {
                $0.provider.caseInsensitiveCompare(provider) == .orderedSame
            }
            let trend = trends.first {
                $0.provider.caseInsensitiveCompare(provider) == .orderedSame
            }?.points.map(\.io) ?? []
            return ProviderOverview.card(
                provider: provider,
                installed: availability.has(provider),
                read: reads,
                // Only a local provider can be unreachable; a hosted one's transcripts are
                // on disk whether or not its endpoint is up
                reachable: ProviderIdentity.of(provider)?.isLocal == true
                    ? ollamaReachableHint : nil,
                usage: usage,
                hasUnpriced: usage?.models.values.contains { !$0.priced } ?? false,
                windows: limits,
                paces: paces,
                asOf: provider == UsageStore.provider ? claudeLimitsAsOf : nil,
                serviceTone: service.map { ServiceGlyph.tone(for: $0.indicator) },
                servicePhrase: service?.phrase,
                trend: trend,
                limitsNote: provider == UsageStore.provider ? limitsNote : nil)
        }
    }

    /// What is worth reading before the cards. Empty is the ordinary case.
    var warnings: [ProviderOverview.Warning] {
        ProviderOverview.warnings(windows: limits, paces: paces,
                                  approaching: yellowPct, atLimit: redPct,
                                  staleProviders: staleProviders)
    }

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
    var block5hSlice: Slice { slice(block5h) }
    var day24Slice: Slice { slice(day24) }
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
    /// Set by the app: hides one finding for findingsSnoozeDays
    var onDismissFinding: ((String) -> Void)?
    /// Set by the app: brings every snoozed finding back now
    var onRestoreFindings: (() -> Void)?
    private let claude = UsageStore()
    private let codex = CodexStore()
    private let ollama = OllamaStore()
    private let warehouse = Warehouse()
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

    /// The shape of the range: when the work happened, the longest unbroken run in it, and
    /// how many consecutive days reach up to today.
    /// Deliberately every provider, even while one track is focused, and the panel labels
    /// itself so. A run is measured from the gaps between records, so filtering to one
    /// provider inserts gaps that were never idle time: alternating Claude and Codex for three
    /// hours would report several short runs instead of one long one. Narrowing the question
    /// would not make the answer smaller, it would make it wrong.
    static func cadenceSummary(_ entries: [Entry],
                               now: Date) -> DashboardData.CadenceSummary? {
        guard !entries.isEmpty else { return nil }
        let stretches = Cadence.stretches(entries)
        return DashboardData.CadenceSummary(
            hours: Cadence.byHourOfDay(entries),
            currentStretch: Cadence.current(entries, now: now)?.length,
            longestStretch: stretches.map(\.length).max(),
            streak: Cadence.streak(entries, endingOn: now),
            days: Cadence.activeDays(entries).count)
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
        // Read here rather than at each use, so a rail, a card and an alert cannot disagree
        // about where the thresholds are
        data.readProviders = cfg.providers
        data.yellowPct = cfg.limitYellowPct
        data.pollSeconds = cfg.pollIntervalSeconds
        data.redPct = cfg.limitRedPct
        let detected = ProviderAvailability.detect(
            ollamaReachable: data.ollamaReachableHint)
        data.availability = detected
        // With one track there is nothing to aggregate across, so focus it automatically
        if !detected.hasChoice, let only = detected.installed.first {
            data.focus = only
        }
        queue.async { [weak self] in
            guard let self else { return }
            let now = Date()
            var entries: [Entry] = []
            if cfg.recordHistory {
                // Ask the store, not the transcripts. Opening this window used to reparse
                // every byte on disk for the chosen range, and the range it could answer
                // was capped at whatever Claude Code had not pruned yet.
                entries = self.warehouse
                    .entries(since: now.addingTimeInterval(-Double(days) * 86400))
                    .filter { cfg.wants($0.provider) }
            } else {
                if cfg.wants(UsageStore.provider) {
                    entries += self.claude.scan(lookbackDays: days)
                }
                if cfg.wants(CodexStore.provider) {
                    entries += self.codex.scan(lookbackDays: days).entries
                }
                if cfg.wants(OllamaStore.provider) {
                    entries += self.ollama.scan(lookbackDays: days)
                }
            }
            // One cutoff drives every ranged figure. Separate hardcoded windows are how the
            // tiles and the model mix stayed on 7 days while the charts moved to 14 or 30.
            let since = now.addingTimeInterval(-Double(days) * 86400)
            let trends = Trends.trend(entries, by: .day, count: days, now: now, config: cfg)
            let hourly = Trends.trend(entries, by: .hour, count: 24, now: now, config: cfg)
            let models = Trends.byModel(entries, since: since, config: cfg)
            let today = aggregate(entries, since: Calendar.current.startOfDay(for: now),
                                 config: cfg)
            // Both are inside any range the picker offers, so they come from the same read
            let block5h = aggregate(entries, since: now.addingTimeInterval(-5 * 3600),
                                    config: cfg)
            let day24 = aggregate(entries, since: now.addingTimeInterval(-24 * 3600),
                                  config: cfg)
            let ranged = aggregate(entries, since: since, config: cfg)
            let history = cfg.recordHistory ? Self.historySummary() : nil
            let cadence = cfg.mindfulCues ? Self.cadenceSummary(entries, now: now) : nil
            DispatchQueue.main.async {
                guard gen == self.generation else { return }
                self.data.trends = trends
                self.data.hourly = hourly
                self.data.models = models
                self.data.today = today
                self.data.block5h = block5h
                self.data.day24 = day24
                self.data.ranged = ranged
                self.data.scannedAt = now
                self.data.history = history
                self.data.cadence = cadence
                self.data.loading = false
            }
        }
    }
}

// MARK: - Building blocks

/// A titled section of the dashboard. One card, one header, one vocabulary, so no two panels
/// announce themselves differently. `accessory` is for a control that belongs to the section
/// rather than to its content, such as a rescan button.
private struct Panel<Content: View, Accessory: View>: View {
    let title: String
    var note: String? = nil
    var badge: String? = nil
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    var body: some View {
        RLCard {
            VStack(alignment: .leading, spacing: RL.Space.lg) {
                HStack(spacing: RL.Space.md) {
                    // A provider-scoped panel is marked with that provider's own glyph; the
                    // title beside it is what names the provider in words.
                    if let badge { ProviderTile(provider: badge, size: 18) }
                    RLSectionHeader(title, note: note) {
                        accessory
                    }
                }
                content
            }
        }
    }
}

extension Panel where Accessory == EmptyView {
    init(title: String, note: String? = nil, badge: String? = nil,
         @ViewBuilder content: () -> Content) {
        self.title = title
        self.note = note
        self.badge = badge
        self.accessory = EmptyView()
        self.content = content()
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
        return Date().timeIntervalSince(asOf) > ProviderOverview.stalenessThreshold
    }

    private var status: RLStatus {
        RLStatus.forUtilization(window.utilization, approaching: yellow, atLimit: red,
                                stale: stale)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.sm) {
            HStack(spacing: RL.Space.md) {
                ProviderTile(provider: window.provider, size: 19)
                Text("\(window.provider) · \(window.displayName)")
                    .font(RL.Typography.subheading)
                    .foregroundStyle(RL.Ink.primary)
                // The status as a shape as well as a colour, so the reading survives
                // greyscale and colour blindness
                RLStatusIndicator(status, size: 11)
                Spacer(minLength: RL.Space.md)
                if stale, let asOf {
                    Text("as of \(asOf.formatted(date: .omitted, time: .shortened))")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.State.warning)
                        .help("The last reading RedLine has. Claude Code only feeds the "
                              + "usage feed while it runs.")
                }
                Text("\(Int(window.utilization.rounded()))% used")
                    .font(RL.Typography.mono)
                    .foregroundStyle(status.color)
                    .contentTransition(.numericText())
            }
            RLUsageRail(utilization: window.utilization, status: status, height: 8,
                        elapsed: stale ? nil : pace?.elapsedFraction)
            HStack(spacing: RL.Space.md) {
                if let r = window.resetsAt {
                    Text("Resets \(r.formatted(date: .abbreviated, time: .shortened))")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                }
                if !stale, let pace, let summary = pace.summary() {
                    Text("· " + summary)
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(pace.hitsLimitBeforeReset ? RL.State.warning
                                                                   : RL.Ink.muted)
                        .help(pace.basisNote)
                }
                Spacer()
                // Where a percentage came from, said in a word: three sources can produce
                // the same number, and one that cannot name its origin cannot be trusted.
                RLPill(window.source.label, tint: RL.Ink.muted, help: window.source.note)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(window.provider) \(window.displayName)")
        .accessibilityValue("\(Int(window.utilization.rounded())) percent used, "
                            + status.phrase)
    }
}

/// One finding: a labelled header line, a sentence of prose held to a readable measure, and
/// the evidence as two columns so names read down one edge and numbers down the other.
/// A finding without a figure shows no figure; inventing one to fill the space is the
/// failure this panel exists to avoid.
private struct FindingRow: View {
    let finding: Finding
    /// Nil where there is nothing to dismiss into, so the row simply has no control
    let onDismiss: (() -> Void)?
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
            if let onDismiss {
                Button(action: onDismiss) {
                    Image(systemName: "checkmark.circle")
                        .font(.system(size: 12, weight: .semibold))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Brandkit.steel)
                .help("Read it. Hides this finding for a while; it returns if it is still "
                      + "true then.")
                .accessibilityLabel("Mark as read")
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

/// The dashboard's tile, forwarding to the shared metric tile so the dashboard, the overview
/// and the detail view cannot drift on how a number is presented.
private struct StatTile: View {
    let label: String
    let value: String
    var sub: String? = nil
    var tint: Color? = nil
    var help: String? = nil

    var body: some View {
        RLMetricTile(label: label, value: value, note: sub, tint: tint, help: help)
    }
}

// MARK: - Charts

// Both daily charts share one x-axis cadence, or the same 30 day range reads as two
// different spans stacked on top of each other.
// The axis cadence rule lives in RedlineCore as DailyAxis, so it is tested rather than being
// eight lines of switch nobody can reach.

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
        .background(RL.Surface.overlay, in: RoundedRectangle(cornerRadius: RL.Radius.control,
                                                             style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: RL.Radius.control, style: .continuous)
            .strokeBorder(RL.Stroke.border, lineWidth: 1))
        .shadow(color: .black.opacity(0.28), radius: 10, y: 3)
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

/// A chart's contents in words. A Charts view is opaque to assistive technology, so every
/// chart in the dashboard carries one of these as its accessibility label.
private func chartSummary(_ what: String, trends: [ProviderTrend],
                          value: (UsagePoint) -> Double,
                          format: (Double) -> String) -> String {
    let named = trends.map { trend -> String in
        let total = trend.points.reduce(0.0) { $0 + value($1) }
        return "\(trend.provider) \(format(total))"
    }
    guard !named.isEmpty else { return "\(what): no data" }
    let overall = trends.reduce(0.0) { sum, trend in
        sum + trend.points.reduce(0.0) { $0 + value($1) }
    }
    let peak = trends.flatMap(\.points).max(by: { value($0) < value($1) })
    var line = "\(what). Total \(format(overall)), by provider: \(named.joined(separator: ", "))."
    if let peak, value(peak) > 0 {
        line += " Busiest bucket \(peak.start.formatted(date: .abbreviated, time: .shortened))"
            + " at \(format(value(peak)))."
    }
    return line
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
            AxisMarks(values: .stride(by: .day, count: DailyAxis.strideDays(for: range))) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day(),
                               centered: false)
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .chartHover(over: starts, hover: $hover)
        .frame(height: 215)
        .accessibilityElement()
        .accessibilityLabel(chartSummary("Tokens per day over \(range) days",
                                         trends: trends, value: { Double($0.io) },
                                         format: { fmtTokens(Int($0)) }))
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
            AxisMarks(values: .stride(by: .day, count: DailyAxis.strideDays(for: range))) { _ in
                AxisGridLine().foregroundStyle(Brandkit.steel.opacity(0.10))
                AxisValueLabel(format: .dateTime.month(.abbreviated).day())
                    .font(.system(size: 12, design: .monospaced))
                    .foregroundStyle(Brandkit.steel)
            }
        }
        .chartHover(over: starts, hover: $hover)
        .frame(height: 175)
        .accessibilityElement()
        .accessibilityLabel(chartSummary("Estimated cost per day over \(range) days",
                                         trends: trends, value: { $0.cost },
                                         format: { fmtCost($0) }))
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
        .accessibilityElement()
        .accessibilityLabel(chartSummary("Tokens per hour over the last 24 hours",
                                         trends: trends, value: { Double($0.io) },
                                         format: { fmtTokens(Int($0)) }))
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
                    // An unpriced model reads "n/a", never a zero that reads as free
                    Text(m.priced ? fmtCost(m.cost) : "n/a")
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
                Text("n/a means no pricing entry, so it is counted in tokens only")
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

/// The shape of the day: when the work happens, how long the runs are, how many days in a
/// row. Every figure here is counted from timestamps; none of it is a claim about the person
/// at the keyboard, and none of it is advice.
private struct CadencePanel: View {
    let cadence: DashboardData.CadenceSummary
    let cues: [CadenceCue]
    /// True when one track is focused. This panel still counts every provider, so it says so;
    /// see cadenceSummary for why it is not scoped like the tiles are.
    let focused: Bool

    private var peak: Int { max(1, cadence.hours.max() ?? 1) }

    /// Names the scope before the detail, because every other panel narrows to the focused
    /// track and this one does not. Left off when nothing is focused, where it would be noise.
    private var note: String {
        let parts = [focused ? "all providers" : nil, busiest.map { "busiest at \($0)" }]
        return parts.compactMap { $0 }.joined(separator: " · ")
    }

    /// The busiest hour, named the way a person says it rather than as an index
    private var busiest: String? {
        guard let top = cadence.hours.enumerated().max(by: { $0.element < $1.element }),
              top.element > 0 else { return nil }
        return String(format: "%02d:00", top.offset)
    }

    var body: some View {
        Panel(title: "Cadence", note: note) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 22) {
                    StatTile(label: "Current run",
                             value: cadence.currentStretch.map { Pace.short($0) } ?? "none",
                             sub: cadence.currentStretch == nil ? "no activity just now" : nil)
                    StatTile(label: "Longest run",
                             value: cadence.longestStretch.map { Pace.short($0) } ?? "none",
                             sub: "in this range")
                    StatTile(label: "Days running", value: "\(cadence.streak)",
                             sub: "\(cadence.days) active in range")
                    Spacer()
                }
                hours
                if let latest = cues.last {
                    Text("\(latest.title). \(latest.body)")
                        .font(.system(size: 11))
                        .foregroundStyle(Brandkit.steel)
                }
            }
        }
    }

    /// One bar per local hour. Deliberately unlabelled except at the quarters: this answers
    /// "when do I work", which is a shape question, and the readout above answers the rest.
    private var hours: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .bottom, spacing: 2) {
                ForEach(Array(cadence.hours.enumerated()), id: \.offset) { hour, value in
                    RoundedRectangle(cornerRadius: 2)
                        .fill(value > 0 ? Brandkit.signal.opacity(0.35 + 0.65 * Double(value) / Double(peak))
                                        : Brandkit.steel.opacity(0.18))
                        .frame(height: max(3, 44 * Double(value) / Double(peak)))
                        .help("\(String(format: "%02d:00", hour)) · \(fmtTokens(value)) tokens")
                }
            }
            .frame(height: 44, alignment: .bottom)
            HStack {
                ForEach([0, 6, 12, 18], id: \.self) { hour in
                    Text(String(format: "%02d", hour))
                        .font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(Brandkit.steel)
                    if hour != 18 { Spacer() }
                }
            }
        }
    }
}

/// The window's scroll container, kept separate from what it scrolls.
///
/// The split is not decorative: `ImageRenderer` draws nothing inside a `ScrollView`, so every
/// rendered state of this screen would have been an empty rectangle. The content is a view in
/// its own right, so it can be rendered, previewed and measured.
struct DashboardView: View {
    @ObservedObject var model: DashboardModel
    @ObservedObject var ollama: OllamaService
    let onReload: (Int) -> Void
    let onFocus: (String) -> Void
    /// Opens the settings window. Nil in a preview, where there is no app to open it.
    var onOpenSettings: (() -> Void)? = nil

    var body: some View {
        ScrollView {
            DashboardContent(model: model, ollama: ollama, onReload: onReload,
                             onFocus: onFocus, onOpenSettings: onOpenSettings)
        }
        .background(RL.Surface.ground)
        // The theme is applied to the window itself, in AppDelegate. preferredColorScheme
        // here would fight it and reintroduce the lag on returning to "follow the OS".
    }
}

struct DashboardContent: View {
    @ObservedObject var model: DashboardModel
    @ObservedObject var ollama: OllamaService
    let onReload: (Int) -> Void
    let onFocus: (String) -> Void
    var onOpenSettings: (() -> Void)? = nil
    // Entries are kept a year, so the long ranges answer from the store rather than from
    // whatever Claude Code has not pruned yet. That is the whole reason the store exists.
    private let ranges = [7, 14, 30, 60, 90]

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var data: DashboardData { model.data }

    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.lg) {
                header

                if data.loading {
                    Panel(title: "Reading transcripts") {
                        RLStateBlock(.loading("Scanning \(data.range) days of usage"),
                                     hint: "Transcripts are read off the main thread, so the "
                                         + "window stays responsive while this runs")
                    }
                } else {
                    // The overview answers "is anything about to stop me" before any chart
                    // is read. Focused on one provider, its detail takes the same slot.
                    if data.focusingAll { overview } else { providerDetail }
                    tiles
                    // Limits before service status: the limit rails are the product's
                    // headline, and the menu already leads with them.
                    limitsPanel
                    servicePanel
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
                    Panel(title: "Last 24 hours", note: hourlyNote) {
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
        .padding(RL.Space.xl)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RL.Surface.ground)
        // Content changing under a live monitor must not jump, and must not animate at
        // all when the user has asked for less motion
        .animation(reduceMotion ? nil : .easeInOut(duration: RL.Motion.content),
                   value: data.loading)
        .animation(reduceMotion ? nil : .easeInOut(duration: RL.Motion.content),
                   value: data.focus)
    }

    // MARK: - Header

    private var header: some View {
        VStack(alignment: .leading, spacing: RL.Space.lg) {
            HStack(alignment: .top, spacing: RL.Space.lg) {
                identity
                Spacer(minLength: RL.Space.lg)
                monitoringStatus
            }
            // The red rule under the wordmark, as in the supplied lockup
            Rectangle()
                .fill(RL.Brandmark.signal)
                .frame(width: 132, height: 3)
                .clipShape(Capsule())
            scopeBar
        }
    }

    private var identity: some View {
        HStack(spacing: RL.Space.lg) {
            RedlineMarkAdaptive(size: 31)
            VStack(alignment: .leading, spacing: 1) {
                // Wordmark specs follow brand/logo/redline-wordmark-dark.svg
                Text("RedLine")
                    .font(RL.Typography.title)
                    .tracking(-0.7)
                    .foregroundStyle(RL.Ink.primary)
                Text("Know your limit.")
                    .font(RL.Typography.mono)
                    .tracking(0.8)
                    .foregroundStyle(RL.Ink.muted)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("RedLine. Know your limit.")
    }

    /// Whether monitoring is actually running, and when it last did something. A monitor that
    /// cannot say this is indistinguishable from one that has quietly stopped.
    private var monitoringStatus: some View {
        VStack(alignment: .trailing, spacing: RL.Space.xs) {
            HStack(spacing: RL.Space.sm) {
                RLStatusIndicator(RLStatus(.healthy, phrase: "Monitoring"), size: 11)
                Text("Monitoring \(readProvidersPhrase)")
                    .font(RL.Typography.caption)
                    .foregroundStyle(RL.Ink.secondary)
                    .lineLimit(1)
            }
            HStack(spacing: RL.Space.sm) {
                Text(data.scannedAt.map {
                    "Updated \($0.formatted(date: .omitted, time: .standard))"
                } ?? "Reading…")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
                    .contentTransition(.numericText())
                Text("· every \(cadencePhrase)")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
                    .help("How often RedLine rescans. Claude's windows also update the moment "
                          + "Claude Code writes them, without waiting for this.")
                Button { onReload(data.range) } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .foregroundStyle(RL.Ink.secondary)
                .help("Rescan now")
                .accessibilityLabel("Rescan now")
                if let onOpenSettings {
                    Button(action: onOpenSettings) {
                        Image(systemName: "gearshape")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(RL.Ink.secondary)
                    .help("Open settings")
                    .accessibilityLabel("Open settings")
                }
            }
        }
    }

    /// "Claude, Codex and Ollama", or "nothing yet" when every provider is switched off.
    private var readProvidersPhrase: String {
        let names = Config.knownProviders.filter { provider in
            data.availability.has(provider)
                && data.readProviders.contains {
                    $0.caseInsensitiveCompare(provider) == .orderedSame
                }
        }
        switch names.count {
        case 0:  return "nothing yet"
        case 1:  return names[0]
        case 2:  return names.joined(separator: " and ")
        default: return names.dropLast().joined(separator: ", ") + " and " + names[names.count - 1]
        }
    }

    /// Human units, not developer ones: "every 300s" made users ask what was wrong.
    private var cadencePhrase: String {
        let seconds = data.pollSeconds
        if seconds.truncatingRemainder(dividingBy: 60) == 0 {
            return "\(Int(seconds / 60))m"
        }
        return "\(Int(seconds))s"
    }

    /// Which provider is in view, the appearance, and the range. The provider control carries
    /// each provider's own mark, so the scope is recognisable before it is read.
    ///
    /// Two rows when one will not fit. Squeezing the row instead truncated the provider names
    /// away and left three bare third-party marks acting as controls, which is exactly what a
    /// mark on its own must not do.
    private var scopeBar: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: RL.Space.lg) {
                providerScope
                Spacer(minLength: RL.Space.lg)
                themeControl
                rangeControl
            }
            VStack(alignment: .leading, spacing: RL.Space.md) {
                providerScope
                HStack(spacing: RL.Space.lg) {
                    themeControl
                    Spacer(minLength: 0)
                    rangeControl
                }
            }
        }
    }

    private var rangeControl: some View {
        RLSegmented(options: ranges.map {
                        (value: $0, label: "\($0)d",
                         help: "Show the last \($0) days")
                    },
                    selection: data.range,
                    onSelect: onReload)
    }

    @ViewBuilder
    private var providerScope: some View {
        if data.availability.hasChoice {
            HStack(spacing: RL.Space.xs) {
                ForEach(data.availability.trackChoices, id: \.self) { choice in
                    let active = choice.caseInsensitiveCompare(data.focus) == .orderedSame
                    let all = choice == Config.autoProvider
                    let accent = all ? RL.Brandmark.signal
                                     : ProviderIdentity.accent(for: choice)
                    Button { onFocus(choice) } label: {
                        HStack(spacing: RL.Space.sm) {
                            if all {
                                RedlineMarkAdaptive(size: 13)
                            } else if let mark = ProviderIdentity.of(choice)?.mark {
                                ProviderGlyph(mark, size: 13)
                                    .foregroundStyle(active ? accent : RL.Ink.muted)
                            }
                            Text(all ? "All" : choice)
                                .font(.system(size: 12, weight: .medium))
                                // The name is what makes the mark an identification rather
                                // than a decoration, so it is never what gets truncated
                                .fixedSize()
                        }
                        .padding(.horizontal, RL.Space.lg)
                        .frame(height: 26)
                        .foregroundStyle(active ? RL.Ink.primary : RL.Ink.muted)
                        .background(active ? accent.opacity(0.16) : RL.Surface.sunken,
                                    in: RoundedRectangle(cornerRadius: RL.Radius.chip,
                                                         style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: RL.Radius.chip, style: .continuous)
                                .strokeBorder(active ? accent.opacity(0.5) : .clear,
                                              lineWidth: 1)
                        )
                    }
                    .buttonStyle(.plain)
                    .help(all ? "Show every provider together"
                              : "Show only \(choice)")
                    .accessibilityLabel(all ? "All providers" : choice)
                    .accessibilityAddTraits(active ? [.isButton, .isSelected] : .isButton)
                }
            }
        } else if let only = data.availability.installed.first {
            // Nothing to choose between, so state the track rather than offer it
            ProviderBadge.forProvider(only, size: 14)
        }
    }

    /// Appearance: auto follows the OS, light and dark force the window. The choice persists
    /// in the config like every other preference.
    private var themeControl: some View {
        RLSegmented(options: [("auto", "Auto", "Follow the system appearance"),
                              ("light", "Light", "Always use the light appearance"),
                              ("dark", "Dark", "Always use the dark appearance")]
                        .map { (value: $0.0, label: $0.1, help: $0.2) },
                    selection: data.theme,
                    width: 46,
                    onSelect: { model.setTheme($0) })
    }

    // MARK: - Overview

    /// Every provider at a glance: what is about to stop you, the totals over the range, and
    /// one card per provider.
    private var overview: some View {
        VStack(alignment: .leading, spacing: RL.Space.lg) {
            if !data.warnings.isEmpty {
                OverviewWarnings(warnings: data.warnings, onOpen: onFocus)
            }
            summaryCard
            let cards = data.providerCards
            ProviderCardGrid(count: cards.count) { index in
                let card = cards[index]
                ProviderCardView(card: card, yellow: data.yellowPct, red: data.redPct,
                                 periodLabel: "last \(data.rangeLabel)",
                                 scannedAt: data.scannedAt,
                                 selected: false,
                                 onOpen: { onFocus(card.provider) })
            }
        }
    }

    /// The single number that answers "how close am I", with the window it came from named
    /// beside it. Nothing here is invented: with no limits anywhere, it says so.
    ///
    /// Stacks when the row will not fit. Side by side at a narrow width, the tile labels were
    /// the first thing to truncate, and "ESTIMATED CO…" over a figure is worse than no label.
    private var summaryCard: some View {
        let worst = data.visibleLimits.max(by: { $0.utilization < $1.utilization })
        let stale = worst.map { data.staleProviders.contains($0.provider) } ?? false
        return RLCard {
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: RL.Space.xxl) {
                    nearestLimit(worst, stale: stale)
                    Divider().frame(height: 84).overlay(RL.Stroke.hairline)
                    rangeTotals
                }
                VStack(alignment: .leading, spacing: RL.Space.lg) {
                    nearestLimit(worst, stale: stale)
                    Divider().overlay(RL.Stroke.hairline)
                    rangeTotals
                }
            }
        }
    }

    @ViewBuilder
    private func nearestLimit(_ worst: LimitWindow?, stale: Bool) -> some View {
        VStack(alignment: .leading, spacing: RL.Space.md) {
            RLSectionHeader("Nearest limit",
                            note: worst.map { "\($0.provider) · \($0.displayName)" }
                                ?? "across every provider")
            if let worst {
                let status = RLStatus.forUtilization(worst.utilization,
                                                    approaching: data.yellowPct,
                                                    atLimit: data.redPct, stale: stale)
                HStack(alignment: .firstTextBaseline, spacing: RL.Space.md) {
                    Text("\(Int(worst.utilization.rounded()))%")
                        .font(.system(size: 34, weight: .semibold, design: .monospaced))
                        .foregroundStyle(status.color)
                        .contentTransition(.numericText())
                    RLStatusIndicator(status, size: 13, showsLabel: true)
                }
                RLUsageRail(utilization: worst.utilization, status: status, height: 8,
                            elapsed: stale ? nil : data.pace(for: worst)?.elapsedFraction)
                Text(worst.resetsAt.map {
                    "Resets \($0.formatted(date: .abbreviated, time: .shortened))"
                } ?? "No reset time reported")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
            } else {
                RLStateBlock(.unavailable("No rate limit is being reported"),
                             hint: "Cost and token counts below are unaffected")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var rangeTotals: some View {
        let ranged = data.rangedSlice
        return HStack(alignment: .top, spacing: RL.Space.xxl) {
            RLMetricTile(label: "Tokens · \(data.rangeLabel)",
                         value: fmtTokens(ranged.io),
                         note: "in + out")
            RLMetricTile(label: "Estimated cost",
                         value: fmtCost(ranged.cost) + (ranged.hasUnpriced ? "+" : ""),
                         note: ranged.hasUnpriced ? "some models unpriced"
                                                  : "configured pricing",
                         tint: RL.Brandmark.money,
                         help: "Estimated from your pricing table over measured counts. "
                             + "Never a bill.")
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - Provider detail

    /// One provider in full: what it has spent, what is left, when it resets, where the
    /// figures came from, and what cannot be answered here and why.
    @ViewBuilder
    private var providerDetail: some View {
        if let card = data.providerCards.first(where: {
            $0.provider.caseInsensitiveCompare(data.focus) == .orderedSame
        }) {
            let status = card.status(approaching: data.yellowPct, atLimit: data.redPct)
            let accent = card.identity?.accent ?? RL.Accent.neutral
            RLCard(accent: accent) {
                VStack(alignment: .leading, spacing: RL.Space.lg) {
                    HStack(alignment: .center, spacing: RL.Space.lg) {
                        ProviderBadge.forProvider(card.provider, size: 17)
                        RLStatusIndicator(status, size: 14, showsLabel: true)
                        Spacer(minLength: RL.Space.md)
                        RLInlineButton("All providers", systemImage: "square.grid.2x2",
                                       help: "Back to the overview") {
                            onFocus(Config.autoProvider)
                        }
                    }
                    if let blurb = card.identity?.blurb {
                        Text(blurb)
                            .font(RL.Typography.body)
                            .foregroundStyle(RL.Ink.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Divider().overlay(RL.Stroke.hairline)
                    detailMetrics(card)
                    if let window = card.worstWindow {
                        RLUsageRail(utilization: window.utilization, status: status, height: 8,
                                    elapsed: card.pace?.elapsedFraction)
                    }
                    if let note = card.limitNote {
                        RLStateBlock(.unavailable(note),
                                     hint: card.identity?.isLocal == true
                                         ? "Token counts and cost are still recorded"
                                         : nil)
                    }
                    provenanceRow(card)
                }
            }
        }
    }

    private func detailMetrics(_ card: ProviderCard) -> some View {
        let ranged = data.rangedSlice
        let today = data.todaySlice
        return HStack(alignment: .top, spacing: RL.Space.xxl) {
            RLMetricTile(label: "This period",
                         value: card.worstWindow.map {
                             "\(Int($0.utilization.rounded()))%"
                         } ?? fmtTokens(today.io),
                         note: card.worstWindow?.displayName ?? "tokens today",
                         help: card.worstWindow == nil
                             ? "This provider reports no limit window, so the figure is volume"
                             : "Share of the window that has been consumed")
            RLMetricTile(label: "Remaining",
                         value: card.remainingPercent.map {
                             "\(Int($0.rounded()))%"
                         } ?? "not reported",
                         note: card.remainingPercent == nil ? "no limit to have capacity in"
                                                            : "of this window",
                         help: card.remainingPercent == nil
                             ? "Nothing is inferred here: with no limit reported there is no "
                                 + "capacity figure to give"
                             : nil)
            RLMetricTile(label: "Resets",
                         value: card.worstWindow?.resetsAt.map {
                             $0.formatted(date: .omitted, time: .shortened)
                         } ?? "not reported",
                         note: card.worstWindow?.resetsAt.map {
                             $0.formatted(.relative(presentation: .named))
                         } ?? nil)
            RLMetricTile(label: "Tokens · \(data.rangeLabel)", value: fmtTokens(ranged.io),
                         note: fmtCost(ranged.cost) + (ranged.hasUnpriced ? "+" : "") + " est",
                         help: ranged.hasUnpriced
                             ? "Some models here have no pricing entry, so they are counted "
                                 + "in tokens only and the total carries a plus"
                             : nil)
        }
    }

    /// Where the figures came from and how fresh they are. Three sources can produce the same
    /// percentage, so a reading that cannot name its origin cannot be argued with or fixed.
    private func provenanceRow(_ card: ProviderCard) -> some View {
        HStack(spacing: RL.Space.md) {
            if let window = card.worstWindow {
                RLPill(window.source.label, tint: RL.Ink.muted, help: window.source.note)
            }
            if card.identity?.isLocal == true {
                RLPill("local", tint: card.identity?.accent ?? RL.Accent.neutral,
                       help: "Probed directly on this Mac; nothing leaves the machine")
            }
            if let asOf = card.asOf {
                Text("windows as of \(asOf.formatted(date: .omitted, time: .standard))")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(card.isStale ? RL.State.warning : RL.Ink.muted)
            }
            Spacer(minLength: 0)
            if let at = data.scannedAt {
                Text("transcripts read \(at.formatted(date: .omitted, time: .standard))")
                    .font(RL.Typography.monoSmall)
                    .foregroundStyle(RL.Ink.muted)
            }
        }
    }

    // MARK: - Panels

    /// The rails, or a way to get them. An empty panel with only an error string was a dead
    /// end exactly where a new user needs a door; the setup button is the door.
    @ViewBuilder
    private var limitsPanel: some View {
        if !data.visibleLimits.isEmpty || data.limitsNote != nil {
            Panel(title: "Limits", note: "the red line marks the limit") {
                VStack(alignment: .leading, spacing: RL.Space.lg) {
                    ForEach(data.visibleLimits) { window in
                        LimitRailRow(window: window, yellow: data.yellowPct, red: data.redPct,
                                     pace: data.pace(for: window),
                                     asOf: window.provider == "Claude"
                                         ? data.claudeLimitsAsOf : nil)
                    }
                    if let note = data.limitsNote {
                        RLStateBlock(.unavailable(note))
                    }
                    // No Claude rails and no feed installed: offer the recommended fix
                    // right here rather than sending the user hunting through the menu
                    if data.matches("Claude"),
                       !data.visibleLimits.contains(where: { $0.provider == "Claude" }),
                       !StatuslineInstaller.isInstalled(),
                       data.availability.has("Claude") {
                        RLInlineButton("Set Up Claude Tracking…", systemImage: "waveform.path",
                                       help: "Reads the windows Claude Code hands its "
                                           + "statusline. No sign-in, no Keychain, no "
                                           + "network.") {
                            model.onSetupClaudeTracking?()
                        }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var servicePanel: some View {
        if !data.visibleServices.isEmpty {
            Panel(title: "Service status", note: "as reported by each operator",
                  accessory: {
                      RLInlineButton("Check now", systemImage: "arrow.clockwise",
                                     help: "Re-check every status immediately") {
                          model.onStatusRefresh?()
                      }
                  },
                  content: {
                      VStack(alignment: .leading, spacing: RL.Space.lg) {
                          ForEach(data.visibleServices, id: \.provider) { s in
                              ServiceStatusRow(provider: s.provider,
                                               indicator: s.indicator,
                                               phrase: s.phrase,
                                               detail: s.description,
                                               checkedAt: data.servicesCheckedAt)
                          }
                          if let at = data.servicesCheckedAt {
                              Text("last checked " + DateFormatter.localizedString(
                                  from: at, dateStyle: .none, timeStyle: .short))
                                  .font(RL.Typography.monoSmall)
                                  .foregroundStyle(RL.Ink.muted)
                          }
                      }
                  })
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
                        RLStateBlock(.empty(report.hidden > 0
                            ? "Nothing left in this window; \(report.hidden) marked as read."
                            : "Nothing worth changing in this window."))
                            .padding(.bottom, RL.Space.lg)
                    } else {
                        ForEach(Array(report.findings.enumerated()), id: \.element.id) {
                            index, finding in
                            if index > 0 {
                                Divider()
                                    .overlay(RL.Stroke.hairline)
                                    .padding(.vertical, RL.Space.lg)
                            }
                            FindingRow(finding: finding) {
                                model.onDismissFinding?(finding.id)
                            }
                        }
                        Divider()
                            .overlay(RL.Stroke.hairline)
                            .padding(.vertical, RL.Space.lg)
                    }
                    footer(report)
                }
            }
        }
    }

    /// The one caveat that applies to every row, said once, next to the control that
    /// regenerates them.
    private func footer(_ report: FindingsReport) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: RL.Space.lg) {
            Text("Estimates over measured counts, never a bill. "
                 + "A finding with nothing honest to put on it carries no figure.")
                .font(RL.Typography.caption)
                .foregroundStyle(RL.Ink.muted)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: RL.Space.xl)
            // Said wherever findings are counted, so a hidden one is never mistaken for one
            // that stopped being true
            if report.hidden > 0 {
                RLInlineButton("\(report.hidden) read",
                               help: "Marked as read and hidden. Click to show them again "
                                   + "now.") {
                    model.onRestoreFindings?()
                }
            }
            if data.findingsScanning {
                HStack(spacing: RL.Space.sm) {
                    ProgressView().controlSize(.small).scaleEffect(0.7)
                    Text("Scanning")
                        .font(RL.Typography.monoSmall)
                        .foregroundStyle(RL.Ink.muted)
                }
            } else {
                RLInlineButton("Scan again", systemImage: "arrow.clockwise",
                               help: "Reads the transcripts again now, rather than waiting "
                                   + "for the background pass") {
                    model.onRescanFindings?()
                }
            }
            Text(report.generatedAt.formatted(date: .omitted, time: .standard))
                .font(RL.Typography.monoSmall)
                .foregroundStyle(RL.Ink.muted)
                .help("when this report was generated")
        }
    }

    /// What has been recorded, as opposed to what can still be read. The two diverge the
    /// moment Claude Code prunes a transcript, and this panel is the only place that says so.
    @ViewBuilder
    private var historyPanel: some View {
        if let history = data.history, history.days > 0 {
            if let cadence = data.cadence {
                CadencePanel(cadence: cadence, cues: data.cues,
                             focused: !data.focusingAll)
            }
            Panel(title: "Recorded history", note: "kept locally, UTC days") {
                VStack(alignment: .leading, spacing: RL.Space.md) {
                    HStack(spacing: RL.Space.xxl) {
                        StatTile(label: "Days", value: "\(history.days)",
                                 sub: [history.earliest, history.latest]
                                    .compactMap { $0 }.joined(separator: " to "))
                        StatTile(label: "Tokens", value: fmtTokens(history.tokens))
                        StatTile(label: "Estimated cost",
                                 value: fmtCost(history.cost) + (history.complete ? "" : "+"),
                                 sub: history.complete ? nil : "some models have no price",
                                 tint: RL.Brandmark.money)
                        Spacer()
                    }
                    Text("Recorded as RedLine polls, so it survives Claude Code's own "
                         + "transcript cleanup. \(ByteCountFormatter.string(fromByteCount: history.sizeBytes, countStyle: .file)) on disk.")
                        .font(RL.Typography.caption)
                        .foregroundStyle(RL.Ink.muted)
                }
            }
        }
    }

    @ViewBuilder
    private var empty: some View {
        RLStateBlock(.empty("No usage recorded in this range"),
                     // Ollama keeps no history of its own, so say how to start collecting it
                     hint: data.focus == OllamaStore.provider
                         ? "Use Set Up Ollama Tracking in Settings to record calls"
                         : nil)
            .frame(height: 60, alignment: .topLeading)
    }

    /// The rolling day's own total, so the shape above it can be read against a figure
    private var hourlyNote: String {
        let d = data.day24Slice
        guard d.io > 0 else { return "no usage" }
        return "\(fmtTokens(d.io)) · \(fmtCost(d.cost))\(d.hasUnpriced ? "+" : "") est"
    }

    private var tiles: some View {
        // Labels name the track so a focused figure cannot be read as the global one
        let scope = data.focusingAll ? "" : " · \(data.focus)"
        let today = data.todaySlice
        let block5h = data.block5hSlice
        let ranged = data.rangedSlice
        let peak = data.visibleTrends.compactMap(\.peak).map(\.io).max()
        return RLCard {
            HStack(spacing: RL.Space.lg) {
                StatTile(label: "Today\(scope)", value: fmtTokens(today.io),
                         sub: "\(fmtCost(today.cost))\(today.hasUnpriced ? "+" : "") est")
                StatTile(label: "Last 5 hours\(scope)", value: fmtTokens(block5h.io),
                         sub: "\(fmtCost(block5h.cost))\(block5h.hasUnpriced ? "+" : "") est")
                StatTile(label: "Last \(data.rangeLabel)\(scope)", value: fmtTokens(ranged.io),
                         sub: "\(fmtCost(ranged.cost))\(ranged.hasUnpriced ? "+" : "") est")
                StatTile(label: "Cache read", value: fmtTokens(ranged.cacheRead),
                         sub: data.rangeLabel,
                         help: "Cache reads are billed at a fraction of the input rate, so "
                             + "they are counted separately from in+out")
                StatTile(label: "Busiest day",
                         value: peak.map(fmtTokens) ?? "no data", sub: "in range")
            }
        }
    }
}
