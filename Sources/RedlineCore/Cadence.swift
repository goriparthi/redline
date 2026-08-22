// What the timestamps say about how the work is spread out.
//
// The rule this module is built around: RedLine can see the keyboard and cannot see the
// person at it. So nothing here infers tiredness, stress or wellbeing, and nothing here
// gives advice. A cue states a measured fact and stops, in the same register the limit
// alerts use. What to do about it is the reader's business.
//
// Three facts are available from entry timestamps and nothing else:
//   stretch  how long the current run of activity has gone with no real gap in it
//   late     activity happening after an hour the reader nominated
//   streak   consecutive days with any activity at all
//
// A cue can only fire when there is activity, so a machine nobody is using is silent by
// construction rather than by a quiet-hours setting.
import Foundation

/// A run of activity with no gap longer than `gap` in it.
public struct Stretch: Equatable {
    public let start: Date
    public let end: Date
    public let records: Int

    public init(start: Date, end: Date, records: Int) {
        self.start = start
        self.end = end
        self.records = records
    }

    /// Measured from first activity to last, never to now: a pause is not work, and a
    /// counter that keeps climbing while nothing happens is not a measurement.
    public var length: TimeInterval { end.timeIntervalSince(start) }

    /// Stable across polls while the stretch continues, which is what "say this once" needs.
    public var id: String { "stretch|\(Int(start.timeIntervalSince1970))" }

    public func isOpen(now: Date, gap: TimeInterval) -> Bool {
        now.timeIntervalSince(end) <= gap
    }
}

public enum Cadence {
    /// What counts as a break. Fifteen minutes is long enough that a slow turn or a build
    /// does not split a stretch, and short enough that a coffee does.
    public static let defaultGap: TimeInterval = 900

    /// Every run of activity, oldest first.
    public static func stretches(_ entries: [Entry],
                                 gap: TimeInterval = defaultGap) -> [Stretch] {
        let times = entries.map(\.ts).sorted()
        guard let first = times.first else { return [] }
        var out: [Stretch] = []
        var start = first
        var last = first
        var count = 0
        for ts in times {
            if ts.timeIntervalSince(last) > gap {
                out.append(Stretch(start: start, end: last, records: count))
                start = ts
                count = 0
            }
            last = ts
            count += 1
        }
        out.append(Stretch(start: start, end: last, records: count))
        return out
    }

    /// The stretch still in progress, or nil when the last activity is old enough that the
    /// run has ended.
    public static func current(_ entries: [Entry], gap: TimeInterval = defaultGap,
                               now: Date = Date()) -> Stretch? {
        guard let last = stretches(entries, gap: gap).last,
              last.isOpen(now: now, gap: gap) else { return nil }
        return last
    }

    /// Local days that saw any activity, oldest first. Local rather than UTC because a
    /// streak is a claim about days as the person at the keyboard lived them.
    public static func activeDays(_ entries: [Entry],
                                  calendar: Calendar = .current) -> [Date] {
        var days = Set<Date>()
        for e in entries { days.insert(calendar.startOfDay(for: e.ts)) }
        return days.sorted()
    }

    /// Consecutive active days ending at `endingOn`, counting that day only if it is active.
    public static func streak(_ entries: [Entry], endingOn: Date = Date(),
                              calendar: Calendar = .current) -> Int {
        let days = Set(activeDays(entries, calendar: calendar))
        var day = calendar.startOfDay(for: endingOn)
        var count = 0
        while days.contains(day) {
            count += 1
            guard let previous = calendar.date(byAdding: .day, value: -1, to: day) else { break }
            day = previous
        }
        return count
    }

    /// Tokens by local hour of day, 24 buckets starting at midnight. The shape of a working
    /// day, for the panel that draws it.
    public static func byHourOfDay(_ entries: [Entry],
                                   calendar: Calendar = .current) -> [Int] {
        var out = [Int](repeating: 0, count: 24)
        for e in entries {
            let hour = calendar.component(.hour, from: e.ts)
            guard hour >= 0, hour < 24 else { continue }
            out[hour] += e.input + e.output
        }
        return out
    }

    /// The night a moment belongs to, as a local day. Nights roll at 05:00 rather than
    /// midnight, so 01:30 belongs to the evening that ran into it rather than to a new day.
    static func nightKey(for date: Date, calendar: Calendar = .current) -> String {
        let shifted = date.addingTimeInterval(-5 * 3600)
        let day = calendar.startOfDay(for: shifted)
        return Warehouse.dayFormatter.string(from: day)
    }
}

/// One thing worth saying, once.
public struct CadenceCue: Equatable {
    public enum Kind: Equatable {
        /// A run of activity has passed a multiple of the reader's threshold.
        case stretch(TimeInterval)
        /// Activity after the hour the reader nominated.
        case late(hour: Int)
        /// Consecutive days with activity.
        case streak(days: Int)
    }

    public let kind: Kind
    public let title: String
    public let body: String
    /// Stable per cue, so a delivery layer can avoid posting the same thing twice.
    public let id: String

    public init(kind: Kind, title: String, body: String, id: String) {
        self.kind = kind
        self.title = title
        self.body = body
        self.id = id
    }
}

/// What has already been said. Keyed so a new stretch, a new night and a longer streak each
/// re-arm on their own terms.
public struct CadenceState: Codable, Equatable {
    /// Identity of the stretch the last stretch cue was said for, and how many thresholds
    /// of it have been announced.
    public var stretchID: String = ""
    public var stretchFired: Int = 0
    /// The night the last late cue was said for.
    public var lastLateNight: String = ""
    /// Highest streak multiple already announced.
    public var streakFired: Int = 0
    public var updatedAt: Date?

    public init() {}
}

public enum CadenceRules {
    /// Cues for one poll's worth of entries, given what has already been said.
    ///
    /// `entries` needs to cover enough history for the questions asked of it: a streak needs
    /// days, a stretch needs the last few hours. Passing less does not produce a wrong
    /// answer, it produces a smaller one, which is the right way round for this.
    public static func evaluate(entries: [Entry], config: Config, now: Date = Date(),
                                calendar: Calendar = .current,
                                state: inout CadenceState) -> [CadenceCue] {
        guard config.mindfulCues else { return [] }
        guard !entries.isEmpty else { return [] }
        var cues: [CadenceCue] = []
        state.updatedAt = now

        // Stretch. Announced at each multiple of the threshold, so a long run says something
        // at two hours and again at four rather than once and then never.
        let threshold = max(15, config.stretchMinutes) * 60
        if let stretch = Cadence.current(entries, now: now) {
            if state.stretchID != stretch.id {
                state.stretchID = stretch.id
                state.stretchFired = 0
            }
            let reached = Int(stretch.length / threshold)
            if reached > state.stretchFired {
                state.stretchFired = reached
                cues.append(CadenceCue(
                    kind: .stretch(stretch.length),
                    title: "\(Pace.short(stretch.length)) at this",
                    body: "Since \(clock(stretch.start, calendar: calendar)), with no gap "
                        + "longer than \(Pace.short(Cadence.defaultGap)).",
                    id: "\(stretch.id)|\(reached)"))
            }
        } else if !state.stretchID.isEmpty {
            // The run ended. Nothing is said about that; it just re-arms.
            state.stretchID = ""
            state.stretchFired = 0
        }

        // Late. One per night, and only when the activity is recent enough to be happening
        // rather than remembered.
        if let last = entries.map(\.ts).max(),
           now.timeIntervalSince(last) <= Cadence.defaultGap {
            let hour = calendar.component(.hour, from: last)
            let lateHour = min(23, max(18, config.lateHour))
            if hour >= lateHour || hour < 5 {
                let night = Cadence.nightKey(for: last, calendar: calendar)
                if state.lastLateNight != night {
                    state.lastLateNight = night
                    let dayStart = calendar.startOfDay(for: last)
                    let first = entries.map(\.ts).filter { $0 >= dayStart }.min()
                    var body = "Still going."
                    if let first, first < last {
                        body = "First activity today was \(clock(first, calendar: calendar))."
                    }
                    cues.append(CadenceCue(
                        kind: .late(hour: hour),
                        title: "It is \(clock(last, calendar: calendar))",
                        body: body,
                        id: "late|\(night)"))
                }
            }
        }

        // Streak. Announced at each multiple of the threshold for the same reason as the
        // stretch: a daily "you have worked another day" is noise, not news.
        let streakThreshold = max(2, config.streakDays)
        let streak = Cadence.streak(entries, endingOn: now, calendar: calendar)
        let multiple = streak / streakThreshold
        if multiple > state.streakFired, streak >= streakThreshold {
            state.streakFired = multiple
            let days = Cadence.activeDays(entries, calendar: calendar)
            let since = days.suffix(streak).first
            var body = "Every day counted here has had usage."
            if let since {
                body = "Every day since \(date(since, calendar: calendar)) has had usage."
            }
            cues.append(CadenceCue(kind: .streak(days: streak),
                                   title: "\(streak) days running",
                                   body: body, id: "streak|\(streak)"))
        } else if streak < streakThreshold {
            state.streakFired = 0
        }

        return cues
    }

    static func clock(_ date: Date, calendar: Calendar) -> String {
        let f = DateFormatter()
        f.calendar = calendar
        f.timeZone = calendar.timeZone
        f.locale = Locale.autoupdatingCurrent
        f.setLocalizedDateFormatFromTemplate("jmm")
        return f.string(from: date)
    }

    static func date(_ date: Date, calendar: Calendar) -> String {
        let f = DateFormatter()
        f.calendar = calendar
        f.timeZone = calendar.timeZone
        f.locale = Locale.autoupdatingCurrent
        f.setLocalizedDateFormatFromTemplate("MMMd")
        return f.string(from: date)
    }
}

/// Where the cue state lives. Its own file rather than the config, because it is a record of
/// what happened, not something anyone should hand-edit.
public enum CadenceStore {
    public static func url(home: URL? = nil) -> URL {
        AppPaths.data("cadence.json", in: home)
    }

    public static func load(from url: URL? = nil) -> CadenceState {
        let url = url ?? CadenceStore.url()
        guard let data = try? Data(contentsOf: url) else { return CadenceState() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // A file that exists but will not decode means the state silently reset. Absence is
        // ordinary and stays quiet; corruption is not and must leave a record.
        do {
            return try decoder.decode(CadenceState.self, from: data)
        } catch {
            Diag.log.error("cadence.state_corrupt", "state file did not decode; starting over",
                           ["path": url.path, "error": String(describing: error)])
            return CadenceState()
        }
    }

    @discardableResult
    public static func save(_ state: CadenceState, to url: URL? = nil) -> Bool {
        let url = url ?? CadenceStore.url()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(state) else {
            Diag.log.error("cadence.encode_failed", "could not encode state")
            return false
        }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try data.write(to: url, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: url.path)
            return true
        } catch {
            Diag.log.error("cadence.save_failed", "could not write state",
                           ["path": url.path, "error": String(describing: error)])
            return false
        }
    }
}
