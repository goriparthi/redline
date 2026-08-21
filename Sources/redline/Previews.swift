// Sample data and previews for every state the redesigned UI has to hold.
//
// The whole file is DEBUG only, so none of it is compiled into a release build. Nothing here
// touches the config, the transcripts or the network: every figure is invented on purpose and
// named as sample data, so a preview can never be mistaken for a reading.
#if DEBUG
import RedlineCore
import RedlineUI
import SwiftUI

/// Invented data, kept in one namespace so it is obvious at every use site that a figure came
/// from here rather than from a scan.
enum SampleData {
    /// The instant the sample data is built around. Real time, not a fixed date: staleness is
    /// measured against the clock, so a fixed past instant would draw every window as a
    /// last-known reading and the normal state would never be shown. The shape of the data is
    /// deterministic regardless, because the walk below is seeded rather than random.
    static let now = Date()

    /// The config previews aggregate against. The default pricing table, so the cost figures
    /// are arithmetic over the sample tokens rather than numbers typed in by hand.
    static let config = Config()

    static func window(_ provider: String, _ key: String, _ pct: Double,
                       resetsIn: TimeInterval = 2 * 3600,
                       source: Provenance = .official) -> LimitWindow {
        LimitWindow(provider: provider, key: key, utilization: pct,
                    resetsAt: now.addingTimeInterval(resetsIn), source: source)
    }

    /// Invented transcript records. Everything else is derived from these by the same
    /// aggregation the app runs, so a preview exercises the real code path rather than a
    /// parallel one that could drift from it.
    ///
    /// The walk is deterministic, not random: a preview that changes on every render cannot
    /// be compared against the last one.
    static func entries(days: Int = 14, providers: [(String, String, Int)] = [
        ("Claude", "claude-sonnet-4-6", 260_000),
        ("Claude", "claude-opus-4-6", 70_000),
        ("Codex", "gpt-5-codex", 92_000),
        // No pricing entry, so it is counted in tokens and left out of cost, which is what
        // puts the "+" on every total in these previews
        ("Ollama", "qwen3-coder:30b", 27_000),
    ]) -> [Entry] {
        var out: [Entry] = []
        var seed = 7
        func next() -> Double {
            seed = (seed * 1_103_515_245 + 12_345) & 0x7FFF_FFFF
            return Double(seed % 1000) / 1000
        }
        for (provider, model, perDay) in providers {
            for day in 0..<days {
                // One quiet stretch, so an empty bucket is visible in the charts
                let quiet = day == 4 || day == 5
                let scale = quiet ? 0.05 : 0.3 + next() * 0.9
                // A handful of records per day at spread hours, so the hourly chart and the
                // cadence panel both have a shape to draw
                for slot in 0..<4 {
                    // Always in the past, and spread across the day: records dated after now
                    // land inside every rolling window at once, which made "today" and "last
                    // 5 hours" show the same figure
                    let ts = now
                        .addingTimeInterval(Double(day - days + 1) * 86400)
                        .addingTimeInterval(-Double(slot * 4 + 1) * 3600)
                    let io = Int(Double(perDay) * scale / 4)
                    out.append(Entry(provider: provider, key: nil, ts: ts, model: model,
                                     input: io * 3 / 4, output: io / 4,
                                     cacheRead: io / 3, cache5m: io / 12, cache1h: 0))
                }
            }
        }
        return out
    }

    /// Everything reading normally: three providers, healthy windows, live figures.
    static var normal: DashboardData {
        let records = entries()
        var d = DashboardData()
        d.loading = false
        d.availability = ProviderAvailability(installed: ["Claude", "Codex", "Ollama"])
        d.readProviders = ["Claude", "Codex", "Ollama"]
        d.focus = Config.autoProvider
        d.range = 14
        d.scannedAt = now
        d.claudeLimitsAsOf = now
        d.ollamaReachableHint = true
        d.limits = [window("Claude", "five_hour", 34), window("Claude", "seven_day", 41),
                    window("Codex", "five_hour", 22), window("Codex", "seven_day", 58)]
        d.paces = PaceEstimator.paces(for: d.limits, now: now)
        d.trends = Trends.trend(records, by: .day, count: 14, now: now, config: config)
        d.hourly = Trends.trend(records, by: .hour, count: 24, now: now, config: config)
        d.models = Trends.byModel(records, since: now.addingTimeInterval(-14 * 86400),
                                  config: config)
        d.today = aggregate(records, since: Calendar.current.startOfDay(for: now),
                            config: config)
        d.block5h = aggregate(records, since: now.addingTimeInterval(-5 * 3600),
                              config: config)
        d.day24 = aggregate(records, since: now.addingTimeInterval(-24 * 3600), config: config)
        d.ranged = aggregate(records, since: now.addingTimeInterval(-14 * 86400),
                             config: config)
        d.services = [
            .init(provider: "Claude", indicator: "none",
                  description: "All Systems Operational"),
            .init(provider: "Codex", indicator: "none", description: "All Systems Operational"),
            .init(provider: "Ollama", indicator: "local",
                  description: "checked directly, no network leaves this Mac"),
        ]
        d.servicesCheckedAt = now
        return d
    }

    /// A provider up against its cap, with another one about to run out early. This is the
    /// state the warnings row exists for.
    static var nearLimit: DashboardData {
        var d = normal
        d.limits = [window("Claude", "five_hour", 93), window("Claude", "seven_day", 71),
                    window("Codex", "five_hour", 64), window("Codex", "seven_day", 88)]
        d.paces = PaceEstimator.paces(for: d.limits, now: now)
        d.services = [
            .init(provider: "Claude", indicator: "minor",
                  description: "Elevated error rates"),
            .init(provider: "Codex", indicator: "none", description: "All Systems Operational"),
        ]
        return d
    }

    /// Every way a provider can fail to answer, in one screen: Codex is not on this Mac,
    /// Ollama is installed and read but its server is not running, and Claude's windows are
    /// hours old so they are drawn as a last-known reading rather than a current one.
    static var degraded: DashboardData {
        var d = normal
        d.availability = ProviderAvailability(installed: ["Claude", "Ollama"])
        d.readProviders = ["Claude", "Ollama"]
        d.ollamaReachableHint = false
        d.claudeLimitsAsOf = now.addingTimeInterval(-4 * 3600)
        d.limits = [window("Claude", "five_hour", 47, source: .experimental)]
        d.paces = []
        d.limitsNote = "Rate limited by the usage endpoint; retrying"
        d.services = []
        d.servicesCheckedAt = nil
        return d
    }

    /// Mid-scan.
    static var loading: DashboardData {
        var d = normal
        d.loading = true
        d.scannedAt = nil
        return d
    }

    /// Installed, read, and nothing has happened.
    static var noData: DashboardData {
        var d = DashboardData()
        d.loading = false
        d.availability = ProviderAvailability(installed: ["Claude", "Codex", "Ollama"])
        d.readProviders = ["Claude", "Codex", "Ollama"]
        d.scannedAt = now
        d.ollamaReachableHint = true
        // The real bucketing over no records, so the empty state is the one the app draws
        d.trends = Trends.trend([], by: .day, count: 14, now: now, config: config)
        return d
    }

    /// One provider in view, so the detail pane is what is on screen.
    static var focused: DashboardData {
        var d = normal
        d.focus = "Codex"
        return d
    }

    static func model(_ data: DashboardData) -> DashboardModel {
        let model = DashboardModel()
        model.data = data
        return model
    }
}

/// The dashboard in each state, in both appearances. Named so the preview list reads as a
/// checklist of the states the redesign has to hold.
struct DashboardPreviews: View {
    let data: DashboardData

    var body: some View {
        DashboardContent(model: SampleData.model(data), ollama: OllamaService(),
                         onReload: { _ in }, onFocus: { _ in })
    }
}

#Preview("Dashboard · normal · dark") {
    DashboardPreviews(data: SampleData.normal)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.dark)
}

#Preview("Dashboard · normal · light") {
    DashboardPreviews(data: SampleData.normal)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.light)
}

#Preview("Dashboard · near the limit") {
    DashboardPreviews(data: SampleData.nearLimit)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.dark)
}

#Preview("Dashboard · unavailable and stale") {
    DashboardPreviews(data: SampleData.degraded)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.dark)
}

#Preview("Dashboard · loading") {
    DashboardPreviews(data: SampleData.loading)
        .frame(width: 1120, height: 460)
        .preferredColorScheme(.dark)
}

#Preview("Dashboard · no data") {
    DashboardPreviews(data: SampleData.noData)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.dark)
}

#Preview("Dashboard · provider detail") {
    DashboardPreviews(data: SampleData.focused)
        .frame(width: 1120, height: 900)
        .preferredColorScheme(.dark)
}

/// The cards on their own, at the size they actually appear, so each state can be compared
/// side by side rather than hunted for in a full window.
struct ProviderCardPreviews: View {
    let data: DashboardData

    var body: some View {
        let cards = data.providerCards
        ProviderCardGrid(count: cards.count) { index in
            ProviderCardView(card: cards[index], yellow: data.yellowPct, red: data.redPct,
                             periodLabel: "last 14 days", scannedAt: data.scannedAt,
                             selected: false, onOpen: {})
        }
        .padding(RL.Space.xl)
        .background(RL.Surface.ground)
    }
}

#Preview("Cards · normal · dark") {
    ProviderCardPreviews(data: SampleData.normal)
        .frame(width: 900)
        .preferredColorScheme(.dark)
}

#Preview("Cards · normal · light") {
    ProviderCardPreviews(data: SampleData.normal)
        .frame(width: 900)
        .preferredColorScheme(.light)
}

#Preview("Cards · near the limit") {
    ProviderCardPreviews(data: SampleData.nearLimit)
        .frame(width: 900)
        .preferredColorScheme(.dark)
}

#Preview("Cards · unavailable, off and stopped") {
    ProviderCardPreviews(data: SampleData.degraded)
        .frame(width: 900)
        .preferredColorScheme(.dark)
}

#Preview("Cards · no data") {
    ProviderCardPreviews(data: SampleData.noData)
        .frame(width: 900)
        .preferredColorScheme(.dark)
}

/// Every component in the system on one sheet, which is the fastest way to see that a change
/// to a token reached everything rather than one screen.
struct DesignSystemPreview: View {
    var body: some View {
        VStack(alignment: .leading, spacing: RL.Space.xl) {
                RLCard {
                    VStack(alignment: .leading, spacing: RL.Space.lg) {
                        RLSectionHeader("Provider badges", note: "mark plus name")
                        HStack(spacing: RL.Space.lg) {
                            ForEach(Config.knownProviders, id: \.self) {
                                ProviderBadge.forProvider($0, size: 15)
                            }
                            ProviderBadge.forProvider(nil, size: 15)
                        }
                        RLSectionHeader("Provider tiles", note: "for a row with no room")
                        HStack(spacing: RL.Space.lg) {
                            ForEach(Config.knownProviders, id: \.self) {
                                ProviderTile(provider: $0, size: 26)
                            }
                            ProviderTile(provider: nil, size: 26)
                        }
                    }
                }
                RLCard {
                    VStack(alignment: .leading, spacing: RL.Space.lg) {
                        RLSectionHeader("States", note: "shape and word, never colour alone")
                        ForEach([RLStatus.Kind.healthy, .approaching, .atLimit, .offline,
                                 .unknown, .stale], id: \.self) { kind in
                            RLStatusIndicator(RLStatus(kind), size: 13, showsLabel: true)
                        }
                    }
                }
                RLCard {
                    VStack(alignment: .leading, spacing: RL.Space.lg) {
                        RLSectionHeader("Rails")
                        ForEach([12.0, 58.0, 91.0], id: \.self) { pct in
                            RLUsageRail(utilization: pct,
                                        status: RLStatus.forUtilization(pct), height: 8,
                                        elapsed: 0.5)
                        }
                    }
                }
                RLCard {
                    HStack(spacing: RL.Space.xxl) {
                        RLMetricTile(label: "Tokens", value: "5.7M", note: "in + out")
                        RLMetricTile(label: "Estimated cost", value: "$91.40+",
                                     note: "some models unpriced", tint: RL.Brandmark.money)
                        RLMetricTile(label: "Remaining", value: "not reported",
                                     note: "no limit to have capacity in")
                    }
                }
                RLCard {
                    VStack(alignment: .leading, spacing: RL.Space.lg) {
                        RLSectionHeader("Placeholders")
                        RLStateBlock(.loading("Scanning 14 days of usage"))
                        RLStateBlock(.empty("No usage recorded in this range"))
                        RLStateBlock(.error("Ollama is not reachable"),
                                     hint: "Start it with: ollama serve")
                        RLStateBlock(.unavailable("Runs on this Mac, so there is no rate "
                                                 + "limit to report"))
                    }
                }
        }
        .padding(RL.Space.xl)
        .background(RL.Surface.ground)
    }
}

#Preview("Design system · dark") {
    DesignSystemPreview()
        .frame(width: 760, height: 940)
        .preferredColorScheme(.dark)
}

#Preview("Design system · light") {
    DesignSystemPreview()
        .frame(width: 760, height: 940)
        .preferredColorScheme(.light)
}

#Preview("Settings · providers") {
    SettingsView(model: SampleData.settingsModel())
        .frame(width: 820, height: 560)
        .preferredColorScheme(.dark)
}

#Preview("Settings · limits and alerts") {
    SettingsView(model: SampleData.settingsModel(section: .limits))
        .frame(width: 820, height: 560)
        .preferredColorScheme(.dark)
}

extension SampleData {
    /// A settings model over sample environment state. Its actions are all no-ops, so a
    /// preview cannot write the real config or open a real window.
    static func settingsModel(section: SettingsSection = .providers) -> SettingsModel {
        let model = SettingsModel(config: Config())
        model.section = section
        model.state = SettingsEnvironmentState(
            signedIn: false, oauthConfigured: false, claudeFeedInstalled: true,
            ollamaShimInstalled: false, launchAtLogin: true, fullDiskAccess: false,
            availability: ProviderAvailability(installed: ["Claude", "Codex", "Ollama"]),
            appVersion: "0.7.2 (sample)")
        return model
    }
}

// MARK: - Rendering the states to files

/// Writes every state above to a PNG, so the redesign can be reviewed as images without a
/// running app and without inventing data inside the real one.
///
/// Reached only by `redline --render-previews <dir>` in a debug build. Nothing here exists in
/// a release binary.
enum PreviewRenderer {
    struct Shot {
        let name: String
        let dark: Bool
        let width: CGFloat
        let view: AnyView
    }

    static var shots: [Shot] {
        var out: [Shot] = []
        func dashboard(_ name: String, _ data: DashboardData, dark: Bool = true,
                       width: CGFloat = 1120) {
            out.append(Shot(name: name, dark: dark, width: width,
                            view: AnyView(DashboardPreviews(data: data))))
        }
        dashboard("dashboard-normal-dark", SampleData.normal)
        dashboard("dashboard-normal-light", SampleData.normal, dark: false)
        dashboard("dashboard-near-limit", SampleData.nearLimit)
        dashboard("dashboard-unavailable-stale", SampleData.degraded)
        dashboard("dashboard-loading", SampleData.loading)
        dashboard("dashboard-no-data", SampleData.noData)
        dashboard("dashboard-provider-detail", SampleData.focused)
        // Compact width, to show the card grid reflowing rather than being clipped
        dashboard("dashboard-compact", SampleData.normal, width: 620)

        for (name, data, dark) in [
            ("cards-normal-dark", SampleData.normal, true),
            ("cards-normal-light", SampleData.normal, false),
            ("cards-near-limit", SampleData.nearLimit, true),
            ("cards-unavailable", SampleData.degraded, true),
            ("cards-no-data", SampleData.noData, true),
        ] {
            out.append(Shot(name: name, dark: dark, width: 900,
                            view: AnyView(ProviderCardPreviews(data: data))))
        }
        out.append(Shot(name: "design-system-dark", dark: true, width: 760,
                        view: AnyView(DesignSystemPreview())))
        out.append(Shot(name: "design-system-light", dark: false, width: 760,
                        view: AnyView(DesignSystemPreview())))
        // Settings is a NavigationSplitView, which ImageRenderer also declines to draw. Its
        // screenshots are captured from the real window rather than faked with an empty frame.
        return out
    }

    @MainActor
    static func run(directory: String) -> Int32 {
        let dir = URL(fileURLWithPath: directory)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            FileHandle.standardError.write(Data("cannot create \(dir.path)\n".utf8))
            return 1
        }
        var written = 0
        for shot in shots {
            // The tokens resolve against the drawing appearance, so it is set for the render
            // rather than assumed: a dark shot rendered under Aqua would silently be a light
            // one with dark ink.
            let appearance = NSAppearance(named: shot.dark ? .darkAqua : .aqua)
            var image: NSImage?
            appearance?.performAsCurrentDrawingAppearance {
                let renderer = ImageRenderer(
                    content: shot.view
                        .environment(\.colorScheme, shot.dark ? .dark : .light)
                        .frame(width: shot.width)
                        .fixedSize(horizontal: false, vertical: true))
                renderer.scale = 2
                image = renderer.nsImage
            }
            guard let image,
                  let tiff = image.tiffRepresentation,
                  let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                FileHandle.standardError.write(Data("could not render \(shot.name)\n".utf8))
                continue
            }
            let url = dir.appendingPathComponent("\(shot.name).png")
            do {
                try png.write(to: url)
                written += 1
                print("wrote \(url.path) (\(rep.pixelsWide)x\(rep.pixelsHigh))")
            } catch {
                FileHandle.standardError.write(
                    Data("could not write \(url.path): \(error)\n".utf8))
            }
        }
        print("rendered \(written) of \(shots.count) states")
        return written == shots.count ? 0 : 1
    }
}
#endif
