// The bundled command line tool. `Redline.app/Contents/MacOS/redline status --json` is the
// supported way for a status bar, a shell prompt, a CI step or an agent to ask what RedLine
// already knows, without any of them racing for the same sources.
//
// Exit codes are part of the contract, because a script should not have to parse prose:
//   0  fine        10  near a limit      11  at a limit
//  20  indeterminate (nothing to report) 30  no data or unreadable config
import Foundation

public enum RedlineCLI {
    public struct Result {
        public let text: String
        public let code: Int32

        public init(text: String, code: Int32) {
            self.text = text
            self.code = code
        }
    }

    public enum Code {
        public static let ok: Int32 = 0
        public static let near: Int32 = 10
        public static let hit: Int32 = 11
        public static let indeterminate: Int32 = 20
        public static let noData: Int32 = 30
    }

    /// The words that mean "run the tool and exit" rather than "launch the app". Kept here
    /// so the entry point and the help text cannot disagree about what exists.
    public static let commands = ["status", "findings", "history", "cadence", "ingest",
                                  "log", "help"]

    public static let usage = """
    redline <command> [options]

      status              current limits, tokens and cost
      findings            setup findings from your transcripts
      history             recorded daily history from the local warehouse
      cadence             how the work is spread out: runs, hours, days in a row
      ingest              read new transcript records into the local store now
      log                 recorded warnings and errors, newest last
      help                this text

    options
      --json              machine-readable output
      --csv               comma-separated output (history only)
      --days N            window to report on (findings, history, log)
      --level L           debug | info | warn | error, lowest to report (log)
      --tally             group the log by code, most frequent first
      --tail N            only the last N entries (log)

    exit codes
      0 ok · 10 near a limit · 11 at a limit · 20 nothing to report · 30 no data
    """

    public static func run(_ args: [String], version: String = "dev",
                           now: Date = Date()) -> Result {
        var args = args
        let command = args.first.map { $0.hasPrefix("--") ? "status" : args.removeFirst() }
            ?? "status"
        let json = args.contains("--json")
        let csv = args.contains("--csv")
        let days = intOption(args, name: "--days")

        switch command {
        case "status":   return status(json: json, now: now)
        case "findings": return findings(json: json, days: days ?? 7, now: now)
        case "history":  return history(json: json, csv: csv, days: days ?? 30, now: now)
        case "cadence":  return cadence(json: json, days: days ?? 14, now: now)
        case "ingest":   return ingest(json: json, now: now)
        case "log":      return logs(json: json, args: args, days: days, now: now)
        case "help", "--help", "-h": return Result(text: usage, code: Code.ok)
        default:
            return Result(text: "unknown command: \(command)\n\n" + usage,
                          code: Code.noData)
        }
    }

    /// The diagnostics file, as text or JSON. This is the command an eval loop runs: it
    /// answers "what has actually been going wrong" without opening the app.
    static func logs(json: Bool, args: [String], days: Int?, now: Date) -> Result {
        let level = stringOption(args, name: "--level")
            .flatMap { DiagLevel(rawValue: $0.lowercased()) } ?? .warn
        let since = days.map { now.addingTimeInterval(-Double($0) * 86_400) }
        let log = Diag.log

        if args.contains("--tally") {
            let rows = log.tally(minimumLevel: level, since: since)
            guard !rows.isEmpty else {
                return Result(text: "No entries at \(level.rawValue) or above.",
                              code: Code.indeterminate)
            }
            if json {
                let out: [[String: Any]] = rows.map {
                    ["code": $0.code, "count": $0.count, "latest": $0.latest]
                }
                return Result(text: encode(["codes": out]), code: Code.ok)
            }
            let width = rows.map { $0.code.count }.max() ?? 0
            let text = rows.map {
                "\(String(repeating: " ", count: width - $0.code.count))\($0.code)"
                    + "  \($0.count)  last \($0.latest)"
            }.joined(separator: "\n")
            return Result(text: text, code: Code.ok)
        }

        var events = log.read(minimumLevel: level, since: since)
        if let tail = intOption(args, name: "--tail"), events.count > tail {
            events = Array(events.suffix(tail))
        }
        guard !events.isEmpty else {
            return Result(text: "No entries at \(level.rawValue) or above.",
                          code: Code.indeterminate)
        }
        if json {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
            let data = (try? encoder.encode(events)) ?? Data()
            return Result(text: String(decoding: data, as: UTF8.self), code: Code.ok)
        }
        let text = events.map { e -> String in
            let ctx = e.context.isEmpty ? "" : "  "
                + e.context.sorted { $0.key < $1.key }
                    .map { "\($0.key)=\($0.value)" }.joined(separator: " ")
            return "\(e.at)  \(e.level.rawValue.uppercased())  \(e.code)  \(e.message)\(ctx)"
        }.joined(separator: "\n")
        return Result(text: text, code: Code.ok)
    }

    static func stringOption(_ args: [String], name: String) -> String? {
        guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
        return args[i + 1]
    }

    static func intOption(_ args: [String], name: String) -> Int? {
        guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
        return Int(args[i + 1])
    }

    // MARK: - status

    /// Reads what the app published, and tops it up from the statusline feed when that is
    /// fresher. The CLI never fetches anything: if the app is not running, the honest answer
    /// is a stale reading with its age attached.
    static func status(json: Bool, now: Date) -> Result {
        let config = Config.load()
        let snapshot = SnapshotStore.readAny()
        let feed = StatuslineFeed.read(path: StatuslineFeed.defaultPath(), now: now)

        var windows: [LimitWindow] = (snapshot?.limits ?? []).map {
            LimitWindow(provider: $0.provider, key: $0.key, utilization: $0.utilization,
                        resetsAt: $0.resetsAt, source: .unknown)
        }
        var limitsAsOf = snapshot?.claudeLimitsAsOf
        if let feed, !feed.isEmpty, let at = feed.updatedAt,
           limitsAsOf == nil || at > limitsAsOf! {
            windows = windows.filter {
                $0.provider.caseInsensitiveCompare(StatuslineFeed.provider) != .orderedSame
            } + feed.windows
            limitsAsOf = at
        }
        // Grouped by provider, then in window order inside each, so the rows read the way
        // the dropdown does rather than interleaving two providers' weeks. Spelled as one
        // comparator because Swift's sort is not stable, so sorting twice would not hold.
        windows = LimitParser.unexpired(windows, now: now)
            .filter { !$0.isUninformative }
            .sorted { a, b in
                if a.provider != b.provider { return a.provider < b.provider }
                let ra = LimitParser.order[a.key] ?? 9, rb = LimitParser.order[b.key] ?? 9
                return ra == rb ? a.key < b.key : ra < rb
            }

        guard snapshot != nil || feed != nil else {
            return Result(text: json ? "{\"error\":\"no data\"}" :
                "No reading available. Start RedLine, or set up the usage feed.",
                          code: Code.noData)
        }
        let samples = Warehouse().limitSamples(since: now.addingTimeInterval(-86400))
        let paces = PaceEstimator.paces(for: windows, samples: samples, now: now)
        let code = exitCode(for: windows, config: config)

        if json {
            return Result(text: statusJSON(windows: windows, paces: paces,
                                           snapshot: snapshot, limitsAsOf: limitsAsOf,
                                           now: now),
                          code: code)
        }
        return Result(text: statusText(windows: windows, paces: paces, snapshot: snapshot,
                                       limitsAsOf: limitsAsOf, now: now),
                      code: code)
    }

    static func exitCode(for windows: [LimitWindow], config: Config) -> Int32 {
        guard !windows.isEmpty else { return Code.indeterminate }
        let worst = windows.map(\.utilization).max() ?? 0
        if worst >= 100 { return Code.hit }
        if worst >= config.limitRedPct { return Code.near }
        return Code.ok
    }

    static func statusText(windows: [LimitWindow], paces: [Pace], snapshot: Snapshot?,
                           limitsAsOf: Date?, now: Date) -> String {
        var lines: [String] = []
        if windows.isEmpty {
            lines.append("No limit windows are being reported.")
        }
        for w in windows {
            let pace = paces.first { $0.provider == w.provider && $0.key == w.key }
            var row = pad(w.provider, 7) + pad(w.displayName, 20)
            row += pad(String(format: "%.0f%%", w.utilization), 6)
            if let r = w.resetsAt, r > now {
                row += pad("resets in " + Pace.short(r.timeIntervalSince(now)), 22)
            } else {
                row += pad("", 22)
            }
            if let summary = pace?.summary(now: now) { row += summary }
            lines.append(row.trimmingCharacters(in: .whitespaces))
        }
        if let snapshot {
            let today = snapshot.today
            let week = snapshot.week
            lines.append("")
            lines.append("today   \(fmtTokens(today.io)) tokens  "
                + "\(fmtCost(today.cost))\(today.hasUnpriced ? "+" : "") (estimate)")
            lines.append("7 days  \(fmtTokens(week.io)) tokens  "
                + "\(fmtCost(week.cost))\(week.hasUnpriced ? "+" : "") (estimate)")
            let age = now.timeIntervalSince(snapshot.updatedAt)
            lines.append("")
            lines.append("snapshot \(Pace.short(age)) old"
                + (limitsAsOf.map { ", limits \(Pace.short(now.timeIntervalSince($0))) old" }
                   ?? ""))
        }
        return lines.joined(separator: "\n")
    }

    static func statusJSON(windows: [LimitWindow], paces: [Pace], snapshot: Snapshot?,
                           limitsAsOf: Date?, now: Date) -> String {
        let iso = ISO8601DateFormatter()
        var root: [String: Any] = ["generated_at": iso.string(from: now)]
        root["windows"] = windows.map { w -> [String: Any] in
            var out: [String: Any] = [
                "provider": w.provider,
                "key": w.key,
                "display_name": w.displayName,
                "utilization": w.utilization,
                "provenance": w.source.rawValue,
            ]
            if let r = w.resetsAt { out["resets_at"] = iso.string(from: r) }
            if let pace = paces.first(where: { $0.provider == w.provider && $0.key == w.key }) {
                var block: [String: Any] = [
                    "rate_per_hour": pace.ratePerHour,
                    "basis": pace.basisNote,
                    "hits_limit_before_reset": pace.hitsLimitBeforeReset,
                ]
                if let e = pace.exhaustsAt { block["exhausts_at"] = iso.string(from: e) }
                if let d = pace.paceDelta { block["pace_delta"] = d }
                out["pace"] = block
            }
            return out
        }
        if let snapshot {
            root["updated_at"] = iso.string(from: snapshot.updatedAt)
            root["today"] = totals(snapshot.today)
            root["week"] = totals(snapshot.week)
        }
        if let limitsAsOf { root["claude_limits_as_of"] = iso.string(from: limitsAsOf) }
        return encode(root)
    }

    static func totals(_ t: Snapshot.Totals) -> [String: Any] {
        [
            "tokens": t.io,
            "cost_usd": t.cost,
            "tokens_basis": Provenance.official.rawValue,
            "cost_basis": Provenance.localEstimate.rawValue,
            // True when a model in the mix had no pricing entry: the figure is arithmetic
            // over what could be priced, not the whole of it. The menu spells this "+".
            "cost_partial": t.hasUnpriced,
        ]
    }

    // MARK: - findings

    static func findings(json: Bool, days: Int, now: Date) -> Result {
        let config = Config.load()
        let scanner = TranscriptScanner()
        let sessions = scanner.scan(lookbackDays: days, now: now)
        guard !sessions.isEmpty else {
            return Result(text: json ? "{\"findings\":[]}" : "No transcripts in this window.",
                          code: Code.indeterminate)
        }
        let input = ClaudeSetup.findingsInput(sessions: sessions, windowDays: days, now: now)
        let report = Findings.report(input, config: config)
        if json {
            let iso = ISO8601DateFormatter()
            var root: [String: Any] = [
                "generated_at": iso.string(from: report.generatedAt),
                "window_days": report.windowDays,
                "sessions_scanned": report.sessionsScanned,
            ]
            root["findings"] = report.findings.map { f -> [String: Any] in
                var out: [String: Any] = [
                    "id": f.id,
                    "kind": f.kind.rawValue,
                    "basis": f.basis.rawValue,
                    "title": f.title,
                    "detail": f.detail,
                    "evidence": f.evidence.map { e -> [String: Any] in
                        var row: [String: Any] = ["label": e.label]
                        if let v = e.value { row["value"] = v }
                        return row
                    },
                ]
                if let t = f.estimatedTokens { out["estimated_tokens"] = t }
                if let u = f.estimatedUSD { out["estimated_usd"] = u }
                if let fix = f.fix { out["fix"] = fix }
                return out
            }
            return Result(text: encode(root), code: Code.ok)
        }
        var lines = ["\(report.summary) · \(report.sessionsScanned) sessions "
                        + "· \(report.windowDays) days"]
        for f in report.findings {
            lines.append("")
            var head = "[\(f.kind.label)] \(f.title)"
            if let usd = f.estimatedUSD { head += "  ~\(fmtCost(usd))" }
            lines.append(head)
            lines.append("  \(f.detail)")
            for row in f.evidence.prefix(8) {
                lines.append("    · \(row.label)" + (row.value.map { " · \($0)" } ?? ""))
            }
            if let fix = f.fix { lines.append("  fix: \(fix)") }
            lines.append("  basis: \(f.basis.rawValue)")
        }
        return Result(text: lines.joined(separator: "\n"), code: Code.ok)
    }

    // MARK: - history

    static func history(json: Bool, csv: Bool, days: Int, now: Date) -> Result {
        let warehouse = Warehouse()
        let since = now.addingTimeInterval(-Double(days) * 86400)
        let records = warehouse.records(since: since)
        guard !records.isEmpty else {
            return Result(text: json ? "{\"records\":[]}" :
                "No history recorded yet. RedLine writes it as it polls.",
                          code: Code.indeterminate)
        }
        if csv {
            var rows = ["day,provider,model,input,output,cache_read,cache_write,"
                        + "cost_usd,priced"]
            rows += records.map {
                "\($0.day),\($0.provider),\(csvField($0.model)),\($0.input),\($0.output),"
                    + "\($0.cacheRead),\($0.cacheWrite),"
                    + String(format: "%.6f", $0.cost) + ",\($0.priced)"
            }
            return Result(text: rows.joined(separator: "\n"), code: Code.ok)
        }
        if json {
            var root: [String: Any] = ["day_basis": "UTC", "records": records.map {
                [
                    "day": $0.day, "provider": $0.provider, "model": $0.model,
                    "input": $0.input, "output": $0.output, "cache_read": $0.cacheRead,
                    "cache_write": $0.cacheWrite, "cost_usd": $0.cost,
                    "priced": $0.priced, "cost_basis": $0.costBasis.rawValue,
                ]
            }]
            let byDay = Warehouse.byDay(records)
            root["days"] = byDay.count
            root["tokens"] = byDay.reduce(0) { $0 + $1.io }
            root["cost_usd"] = byDay.reduce(0) { $0 + $1.cost }
            return Result(text: encode(root), code: Code.ok)
        }
        var lines = ["day         tokens      cost"]
        for row in Warehouse.byDay(records) {
            lines.append("\(row.day)  " + pad(fmtTokens(row.io), 11)
                + fmtCost(row.cost) + (row.priced ? "" : "+"))
        }
        let tokens = records.reduce(0) { $0 + $1.io }
        let cost = records.reduce(0.0) { $0 + $1.cost }
        lines.append("")
        lines.append("\(records.count) records · \(fmtTokens(tokens)) tokens · "
            + "\(fmtCost(cost)) estimated · days are UTC")
        return Result(text: lines.joined(separator: "\n"), code: Code.ok)
    }

    // MARK: - ingest

    /// Reads whatever the transcripts have gained since the last pass and stores it.
    ///
    /// The app does this on its own timer; this is the same code path on demand, for a
    /// backfill after an install, for a machine where the app is not running, and for the
    /// end to end tests, which need to drive the real thing rather than a stand-in.
    static func ingest(json: Bool, now: Date) -> Result {
        let config = Config.load()
        guard config.recordHistory else {
            return Result(text: json ? "{\"error\":\"history is off\"}" :
                "Keep Local History is off, so there is no store to read into.",
                          code: Code.noData)
        }
        let warehouse = Warehouse()
        var counts: [String: Int] = [:]
        if config.wants(UsageStore.provider) {
            counts[UsageStore.provider] = UsageStore().ingest(into: warehouse, now: now)
        }
        if config.wants(CodexStore.provider) {
            let before = warehouse.entryCount
            _ = CodexStore().ingest(into: warehouse, now: now)
            counts[CodexStore.provider] = warehouse.entryCount - before
        }
        if config.wants(OllamaStore.provider) {
            counts[OllamaStore.provider] = OllamaStore().ingest(into: warehouse, now: now)
        }
        warehouse.rollupPending(config: config)
        let added = counts.values.reduce(0, +)

        if json {
            return Result(text: encode(["added": added, "by_provider": counts,
                                        "records": warehouse.entryCount]),
                          code: Code.ok)
        }
        let detail = counts.keys.sorted()
            .map { "\($0) \(counts[$0] ?? 0)" }.joined(separator: " · ")
        return Result(text: "\(added) new records · \(detail) · "
            + "\(warehouse.entryCount) held", code: Code.ok)
    }

    // MARK: - cadence

    /// What the timestamps say about the shape of the work. Reads the store and nothing
    /// else: no transcripts are parsed here and nothing is fetched.
    static func cadence(json: Bool, days: Int, now: Date) -> Result {
        let since = now.addingTimeInterval(-Double(days) * 86400)
        let entries = Warehouse().entries(since: since)
        guard !entries.isEmpty else {
            return Result(text: json ? "{\"records\":0}" :
                "No activity recorded in the last \(days) days.",
                          code: Code.indeterminate)
        }
        let stretches = Cadence.stretches(entries)
        let current = Cadence.current(entries, now: now)
        let streak = Cadence.streak(entries, endingOn: now)
        let hours = Cadence.byHourOfDay(entries)
        let longest = stretches.map(\.length).max() ?? 0
        let busiest = hours.enumerated().max { $0.element < $1.element }?.offset ?? 0

        if json {
            var root: [String: Any] = [
                "records": entries.count,
                "days": days,
                "active_days": Cadence.activeDays(entries).count,
                "streak_days": streak,
                "longest_stretch_seconds": Int(longest),
                "runs": stretches.count,
                "busiest_hour_local": busiest,
                "tokens_by_hour_local": hours,
                "basis": "counted from local usage records",
            ]
            if let current {
                root["current_stretch_seconds"] = Int(current.length)
                root["current_stretch_started"] = ISO8601DateFormatter()
                    .string(from: current.start)
            }
            return Result(text: encode(root), code: Code.ok)
        }

        var lines: [String] = []
        if let current {
            lines.append("current run   \(Pace.short(current.length)) so far")
        } else {
            lines.append("current run   none")
        }
        lines.append("longest run   \(Pace.short(longest)) of \(stretches.count) in "
            + "\(days) days")
        lines.append("days running  \(streak)")
        lines.append("busiest hour  \(String(format: "%02d:00", busiest)) local")
        return Result(text: lines.joined(separator: "\n"), code: Code.ok)
    }

    // MARK: - Helpers

    static func csvField(_ s: String) -> String {
        s.contains(",") || s.contains("\"")
            ? "\"" + s.replacingOccurrences(of: "\"", with: "\"\"") + "\""
            : s
    }

    static func pad(_ s: String, _ width: Int) -> String {
        s.count >= width ? s + " " : s + String(repeating: " ", count: width - s.count)
    }

    static func encode(_ object: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: object,
                                                     options: [.prettyPrinted, .sortedKeys]),
              let text = String(data: data, encoding: .utf8) else { return "{}" }
        return text
    }
}
