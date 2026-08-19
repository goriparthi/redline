// Setup findings: what the transcripts say about how the tool is configured, as opposed to
// how much it cost. Everything here is read from files already on disk, computed locally,
// and labelled with whether the number was measured or estimated.
//
// The rule that governs this file: never invent a figure. A finding with no honest saving
// attached is still worth reporting, and is reported without one. Guessing a dollar amount
// to make a finding look weightier is exactly the failure this project refuses elsewhere.
import Foundation

public struct Finding: Equatable, Identifiable {
    /// What the reader is expected to do about it. Three kinds, because "fix this now" and
    /// "this is how you work" call for different reactions and should not be mixed.
    public enum Kind: String, Equatable {
        case fixNow = "fix"
        case habit
        case fyi

        public var label: String {
            switch self {
            case .fixNow: return "Fix now"
            case .habit:  return "Habit"
            case .fyi:    return "FYI"
            }
        }
    }

    /// Where the numbers in this finding came from. `measured` means counted from the
    /// transcripts; `estimated` means derived through an assumption that is stated.
    public enum Basis: String, Equatable {
        case measured
        case estimated
    }

    public let id: String
    public let kind: Kind
    public let basis: Basis
    public let title: String
    /// One or two sentences. Says what was observed and why it matters, nothing else.
    public let detail: String
    /// Concrete rows behind the claim: file names, counts, server names.
    public let evidence: [String]
    /// Nil whenever no honest figure exists. A nil here is a deliberate answer.
    public let estimatedTokens: Int?
    public let estimatedUSD: Double?
    /// Something to copy or run. Nil when the fix is a judgement rather than an edit.
    public let fix: String?

    public init(id: String, kind: Kind, basis: Basis, title: String, detail: String,
                evidence: [String] = [], estimatedTokens: Int? = nil,
                estimatedUSD: Double? = nil, fix: String? = nil) {
        self.id = id
        self.kind = kind
        self.basis = basis
        self.title = title
        self.detail = detail
        self.evidence = evidence
        self.estimatedTokens = estimatedTokens
        self.estimatedUSD = estimatedUSD
        self.fix = fix
    }
}

public struct FindingsReport: Equatable {
    public let generatedAt: Date
    public let windowDays: Int
    public let sessionsScanned: Int
    public let findings: [Finding]

    public init(generatedAt: Date, windowDays: Int, sessionsScanned: Int,
                findings: [Finding]) {
        self.generatedAt = generatedAt
        self.windowDays = windowDays
        self.sessionsScanned = sessionsScanned
        self.findings = findings
    }

    public var isEmpty: Bool { findings.isEmpty }

    /// Total of the savings that could honestly be computed. Findings without a figure are
    /// not counted, and the caller says so rather than presenting this as the whole picture.
    public var estimatedUSD: Double {
        findings.compactMap(\.estimatedUSD).reduce(0, +)
    }

    public var countsByKind: [Finding.Kind: Int] {
        findings.reduce(into: [:]) { $0[$1.kind, default: 0] += 1 }
    }

    /// "3 findings · 1 to fix". The line the menu shows.
    public var summary: String {
        guard !findings.isEmpty else { return "no findings" }
        let counts = countsByKind
        var parts = ["\(findings.count) finding\(findings.count == 1 ? "" : "s")"]
        if let fix = counts[.fixNow], fix > 0 { parts.append("\(fix) to fix") }
        return parts.joined(separator: " · ")
    }
}

// MARK: - Transcript scanning

/// One tool call, reduced to the fields the checks below actually ask about.
public struct ToolUse: Equatable {
    public let id: String?
    public let name: String
    /// Read/Edit/Write target, when the call had one.
    public let filePath: String?
    /// Skill name, subagent type, or the head of a Bash command, depending on the tool.
    public let subject: String?
    public let ts: Date?

    public init(id: String?, name: String, filePath: String?, subject: String?, ts: Date?) {
        self.id = id
        self.name = name
        self.filePath = filePath
        self.subject = subject
        self.ts = ts
    }
}

public struct SessionScan: Equatable {
    public let path: String
    public let cwd: String?
    public let lastModel: String?
    public let start: Date?
    public let end: Date?
    public let tools: [ToolUse]
    /// Characters returned by each tool call, keyed by tool_use id. The size of a re-read is
    /// the only measurable part of what a re-read costs.
    public let resultChars: [String: Int]
    public let commandNames: [String]

    public init(path: String, cwd: String?, lastModel: String?, start: Date?, end: Date?,
                tools: [ToolUse], resultChars: [String: Int], commandNames: [String]) {
        self.path = path
        self.cwd = cwd
        self.lastModel = lastModel
        self.start = start
        self.end = end
        self.tools = tools
        self.resultChars = resultChars
        self.commandNames = commandNames
    }
}

public final class TranscriptScanner {
    private let root: URL
    private var cache: [String: (mtime: Date, size: Int, scan: SessionScan)] = [:]
    private let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private let iso = ISO8601DateFormatter()

    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/projects")
    }

    /// Sessions touched inside the window. Cached on mtime and size, like the usage scan,
    /// because a findings run and a usage run walk the same tree.
    public func scan(lookbackDays: Int, now: Date = Date()) -> [SessionScan] {
        let fm = FileManager.default
        let cutoff = now.addingTimeInterval(-Double(lookbackDays) * 86400)
        guard fm.fileExists(atPath: root.path),
              let en = fm.enumerator(at: root, includingPropertiesForKeys:
                [.contentModificationDateKey, .fileSizeKey]) else { return [] }
        var live = Set<String>()
        var out: [SessionScan] = []
        for case let url as URL in en {
            guard url.pathExtension == "jsonl" else { continue }
            guard let vals = try? url.resourceValues(
                forKeys: [.contentModificationDateKey, .fileSizeKey]),
                let mtime = vals.contentModificationDate,
                let size = vals.fileSize, mtime > cutoff else { continue }
            live.insert(url.path)
            if let c = cache[url.path], c.mtime == mtime, c.size == size {
                out.append(c.scan)
                continue
            }
            let scan = parse(url: url)
            cache[url.path] = (mtime, size, scan)
            out.append(scan)
        }
        cache = cache.filter { live.contains($0.key) }
        return out
    }

    func parse(url: URL) -> SessionScan {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            return SessionScan(path: url.path, cwd: nil, lastModel: nil, start: nil, end: nil,
                               tools: [], resultChars: [:], commandNames: [])
        }
        var cwd: String?
        var lastModel: String?
        var start: Date?
        var end: Date?
        var tools: [ToolUse] = []
        var results: [String: Int] = [:]
        var commands: [String] = []

        text.enumerateLines { line, _ in
            // Cheap gate first: most lines in a transcript are plain text exchanges that
            // none of the checks below look at.
            let interesting = line.contains("\"tool_use\"") || line.contains("\"tool_result\"")
                || line.contains("<command-name>") || cwd == nil
            guard interesting, let data = line.data(using: .utf8),
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { return }
            if cwd == nil, let c = obj["cwd"] as? String, !c.isEmpty { cwd = c }
            let ts = (obj["timestamp"] as? String)
                .flatMap { self.isoFrac.date(from: $0) ?? self.iso.date(from: $0) }
            if let ts {
                if start == nil || ts < start! { start = ts }
                if end == nil || ts > end! { end = ts }
            }
            guard let msg = obj["message"] as? [String: Any] else { return }
            if let m = msg["model"] as? String, m != "<synthetic>" { lastModel = m }
            guard let blocks = msg["content"] as? [[String: Any]] else {
                // A slash command arrives as a plain string body carrying the tag
                if let body = msg["content"] as? String {
                    commands.append(contentsOf: Self.commandNames(in: body))
                }
                return
            }
            for block in blocks {
                switch block["type"] as? String {
                case "tool_use":
                    guard let name = block["name"] as? String else { continue }
                    let input = block["input"] as? [String: Any] ?? [:]
                    tools.append(ToolUse(id: block["id"] as? String, name: name,
                                         filePath: input["file_path"] as? String,
                                         subject: Self.subject(tool: name, input: input),
                                         ts: ts))
                case "tool_result":
                    guard let id = block["tool_use_id"] as? String else { continue }
                    results[id] = Self.resultLength(block["content"])
                case "text":
                    if let body = block["text"] as? String, body.contains("<command-name>") {
                        commands.append(contentsOf: Self.commandNames(in: body))
                    }
                default: continue
                }
            }
        }
        return SessionScan(path: url.path, cwd: cwd, lastModel: lastModel, start: start,
                           end: end, tools: tools, resultChars: results,
                           commandNames: commands)
    }

    /// The one input field each tool is identified by. Skills, agents and MCP servers are
    /// all "was this thing ever actually used", and each spells that differently.
    static func subject(tool: String, input: [String: Any]) -> String? {
        switch tool {
        case "Skill":
            return (input["skill"] as? String) ?? (input["command"] as? String)
        case "Task", "Agent":
            return input["subagent_type"] as? String
        case "Bash":
            return (input["command"] as? String)?
                .split(separator: " ").first.map(String.init)
        default:
            return nil
        }
    }

    static func resultLength(_ content: Any?) -> Int {
        if let s = content as? String { return s.count }
        if let blocks = content as? [[String: Any]] {
            return blocks.reduce(0) { $0 + (($1["text"] as? String)?.count ?? 0) }
        }
        return 0
    }

    static func commandNames(in body: String) -> [String] {
        var out: [String] = []
        var rest = Substring(body)
        while let open = rest.range(of: "<command-name>"),
              let close = rest.range(of: "</command-name>", range: open.upperBound..<rest.endIndex) {
            let name = rest[open.upperBound..<close.lowerBound]
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            if !name.isEmpty { out.append(name) }
            rest = rest[close.upperBound...]
        }
        return out
    }
}

// MARK: - The checks

/// One memory file: what it costs to carry and how often it is actually carried.
public struct MemoryFile: Equatable {
    public let path: String
    public let chars: Int
    /// Sessions in the window that load this file. Counted, not assumed: pricing a project's
    /// CLAUDE.md against every session on the machine would overstate it several times over.
    public let sessions: Int
    /// True for ~/.claude/CLAUDE.md, which every session carries whatever the project.
    public let global: Bool

    public init(path: String, chars: Int, sessions: Int, global: Bool) {
        self.path = path
        self.chars = chars
        self.sessions = sessions
        self.global = global
    }
}

public struct FindingsInput {
    public var sessions: [SessionScan]
    public var configuredMCPServers: [String]
    public var skills: [String]
    public var agents: [String]
    public var commands: [String]
    /// Memory files, already expanded through @imports, each with how many of the scanned
    /// sessions actually load it: the global file loads in all of them, a project's file
    /// only in that project's.
    public var memoryFiles: [MemoryFile]
    public var windowDays: Int
    public var now: Date

    public init(sessions: [SessionScan], configuredMCPServers: [String], skills: [String],
                agents: [String], commands: [String], memoryFiles: [MemoryFile],
                windowDays: Int, now: Date = Date()) {
        self.sessions = sessions
        self.configuredMCPServers = configuredMCPServers
        self.skills = skills
        self.agents = agents
        self.commands = commands
        self.memoryFiles = memoryFiles
        self.windowDays = windowDays
        self.now = now
    }
}

public enum Findings {
    /// Characters per token. A rough constant, used only where the result is labelled an
    /// estimate, and never applied to a figure the provider already counted for us.
    public static let charsPerToken = 4.0

    /// A file read this many times in one session is being re-read rather than read.
    public static let rereadThreshold = 3

    /// Estimated tokens above which a memory file is worth mentioning. Below this the
    /// arithmetic is real but the advice would be noise.
    public static let memoryTokenThreshold = 5_000

    public static func report(_ input: FindingsInput, config: Config) -> FindingsReport {
        var findings: [Finding] = []
        findings += unusedMCPServers(input)
        findings += rereadFiles(input, config: config)
        findings += heavyMemoryFiles(input, config: config)
        findings += ghosts(input)
        // Costed findings first, then by kind: what can be acted on with a number attached
        // is what a reader should meet first.
        let order: [Finding.Kind: Int] = [.fixNow: 0, .habit: 1, .fyi: 2]
        findings.sort {
            let a = $0.estimatedUSD ?? -1, b = $1.estimatedUSD ?? -1
            if a != b { return a > b }
            return (order[$0.kind] ?? 9) < (order[$1.kind] ?? 9)
        }
        return FindingsReport(generatedAt: input.now, windowDays: input.windowDays,
                              sessionsScanned: input.sessions.count, findings: findings)
    }

    // MARK: MCP servers configured but never invoked

    static func unusedMCPServers(_ input: FindingsInput) -> [Finding] {
        guard !input.configuredMCPServers.isEmpty, !input.sessions.isEmpty else { return [] }
        var used = Set<String>()
        for session in input.sessions {
            for tool in session.tools where tool.name.hasPrefix("mcp__") {
                let parts = tool.name.dropFirst(5).components(separatedBy: "__")
                if let server = parts.first { used.insert(normalize(server)) }
            }
        }
        let unused = input.configuredMCPServers
            .filter { !used.contains(normalize($0)) }
            .sorted()
        guard !unused.isEmpty else { return [] }
        return [Finding(
            id: "mcp-unused",
            kind: .habit,
            basis: .measured,
            title: "\(unused.count) MCP server\(unused.count == 1 ? "" : "s") "
                 + "configured but never called",
            detail: "Every session that loads a server pays for its tool schemas in the "
                  + "prompt, whether or not a tool is called. These were not called once in "
                  + "\(input.windowDays) days. The schema cost is real but is not visible "
                  + "from here, so no figure is claimed for it.",
            evidence: unused,
            fix: "Remove or disable the unused servers in ~/.claude.json, "
               + "or scope them to the projects that need them.")]
    }

    // MARK: Files read again and again inside one session

    static func rereadFiles(_ input: FindingsInput, config: Config) -> [Finding] {
        var wastedChars = 0
        var perFile: [String: (reads: Int, chars: Int)] = [:]
        var costUSD = 0.0
        var priced = false

        for session in input.sessions {
            var byFile: [String: [ToolUse]] = [:]
            for tool in session.tools where tool.name == "Read" {
                guard let path = tool.filePath else { continue }
                byFile[path, default: []].append(tool)
            }
            let price = session.lastModel.flatMap { config.price(for: $0) }
            for (path, reads) in byFile where reads.count >= rereadThreshold {
                // The first read is the work; every later one is the finding.
                let extra = reads.dropFirst()
                let chars = extra.reduce(0) { $0 + (session.resultChars[$1.id ?? ""] ?? 0) }
                guard chars > 0 else { continue }
                wastedChars += chars
                var slot = perFile[path] ?? (0, 0)
                slot.reads += extra.count
                slot.chars += chars
                perFile[path] = slot
                if let price {
                    priced = true
                    costUSD += Double(chars) / charsPerToken / 1_000_000 * price.input
                }
            }
        }
        guard wastedChars > 0 else { return [] }
        let tokens = Int(Double(wastedChars) / charsPerToken)
        let top = perFile.sorted { $0.value.chars > $1.value.chars }.prefix(5).map {
            "\(($0.key as NSString).lastPathComponent) · \($0.value.reads) extra reads · "
                + "~\(fmtTokens(Int(Double($0.value.chars) / charsPerToken))) tokens"
        }
        return [Finding(
            id: "reread-files",
            kind: .habit,
            basis: .estimated,
            title: "Files read \(rereadThreshold)+ times in a single session",
            detail: "The same file came back into context repeatedly inside one session. "
                  + "Character counts are measured; the token and dollar figures divide them "
                  + "by \(Int(charsPerToken)) characters per token and price them at the "
                  + "session's own model, so treat them as an order of magnitude.",
            evidence: top,
            estimatedTokens: tokens,
            estimatedUSD: priced ? costUSD : nil,
            fix: "Ask for the region rather than the file, or keep the result in the "
               + "conversation instead of re-reading after each shell step.")]
    }

    // MARK: Memory files that every session pays for

    static func heavyMemoryFiles(_ input: FindingsInput, config: Config) -> [Finding] {
        let heavy = input.memoryFiles
            .map { (file: $0, tokens: Int(Double($0.chars) / charsPerToken)) }
            .filter { $0.tokens >= memoryTokenThreshold && $0.file.sessions > 0 }
            .sorted { $0.tokens > $1.tokens }
        guard !heavy.isEmpty else { return [] }
        let tokens = heavy.reduce(0) { $0 + $1.tokens }
        // Priced at a cache write, which is what a system prompt costs on the first request
        // of a session, and multiplied by the sessions that actually load each file rather
        // than by every session on the machine.
        let price = config.price(for: "sonnet")
        let usd = price.map { p in
            heavy.reduce(0.0) { total, row in
                total + Double(row.tokens) / 1_000_000 * p.input * 1.25
                    * Double(row.file.sessions)
            }
        }
        let loads = heavy.reduce(0) { $0 + $1.file.sessions }
        return [Finding(
            id: "memory-heavy",
            kind: .fyi,
            basis: .estimated,
            title: "~\(fmtTokens(tokens)) tokens of memory files carried into sessions",
            detail: "CLAUDE.md and everything it imports are part of the prompt. Sizes and "
                  + "session counts are measured; the token figure assumes "
                  + "\(Int(charsPerToken)) characters per token and the cost prices "
                  + "\(loads) session loads at Sonnet's cache-write rate.",
            evidence: heavy.map {
                "\(shortPath($0.file.path)) · ~\(fmtTokens($0.tokens)) tokens · "
                    + "\($0.file.sessions) session\($0.file.sessions == 1 ? "" : "s")"
                    + ($0.file.global ? " · every project" : "")
            },
            estimatedTokens: tokens,
            estimatedUSD: usd,
            fix: "Move the parts that only matter to one project into that project's own "
               + "CLAUDE.md, or into a skill that loads on demand.")]
    }

    // MARK: Skills, agents and commands defined but never used

    static func ghosts(_ input: FindingsInput) -> [Finding] {
        guard !input.sessions.isEmpty else { return [] }
        var usedSkills = Set<String>()
        var usedAgents = Set<String>()
        var usedCommands = Set<String>()
        for session in input.sessions {
            for tool in session.tools {
                guard let subject = tool.subject.map(normalize) else { continue }
                switch tool.name {
                case "Skill":        usedSkills.insert(subject)
                case "Task", "Agent": usedAgents.insert(subject)
                default: continue
                }
            }
            for name in session.commandNames { usedCommands.insert(normalize(name)) }
        }
        // A slash command and a skill share a namespace in practice, so a name used either
        // way counts as used and no ghost is reported for it twice.
        let allUsed = usedSkills.union(usedCommands)
        var rows: [String] = []
        let skills = input.skills.filter { !allUsed.contains(normalize($0)) }.sorted()
        let agents = input.agents.filter { !usedAgents.contains(normalize($0)) }.sorted()
        let commands = input.commands
            .filter { !allUsed.contains(normalize($0)) }.sorted()
        rows += skills.map { "skill · \($0)" }
        rows += agents.map { "agent · \($0)" }
        rows += commands.map { "command · \($0)" }
        guard !rows.isEmpty else { return [] }
        return [Finding(
            id: "ghost-definitions",
            kind: .fyi,
            basis: .measured,
            title: "\(rows.count) skill\(rows.count == 1 ? "" : "s"), agent"
                 + "\(rows.count == 1 ? "" : "s") or command\(rows.count == 1 ? "" : "s") "
                 + "never invoked",
            detail: "Defined under ~/.claude and not used once in \(input.windowDays) days. "
                  + "Their names and descriptions are cheap to carry, so this is "
                  + "housekeeping rather than spend.",
            evidence: rows,
            fix: "Archive the ones you have stopped using; keep the rest and ignore this.")]
    }

    // MARK: Helpers

    static func normalize(_ s: String) -> String {
        s.lowercased()
            .replacingOccurrences(of: "-", with: "_")
            .replacingOccurrences(of: ".", with: "_")
            .replacingOccurrences(of: " ", with: "_")
    }

    static func shortPath(_ path: String) -> String {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
    }
}
