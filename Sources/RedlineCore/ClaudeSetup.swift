// What Claude Code has been configured with, read from the same files it reads. The findings
// checks compare this against what the transcripts show was actually used.
//
// Nothing here is written to, and nothing outside ~/.claude and a project's own CLAUDE.md is
// opened. A configuration file that cannot be parsed yields nothing rather than a guess.
import Foundation

public enum ClaudeSetup {
    /// Servers named in ~/.claude.json, both globally and per project, plus settings.json
    /// if a server block is there. Names only; nothing about how they are launched.
    public static func mcpServers(home: URL? = nil) -> [String] {
        let root = home ?? RedlineHome.url
        var names = Set<String>()
        func collect(_ any: Any?) {
            guard let dict = any as? [String: Any] else { return }
            for key in dict.keys { names.insert(key) }
        }
        if let json = readJSON(root.appendingPathComponent(".claude.json")) {
            collect(json["mcpServers"])
            if let projects = json["projects"] as? [String: Any] {
                for (_, value) in projects {
                    collect((value as? [String: Any])?["mcpServers"])
                }
            }
        }
        if let json = readJSON(root.appendingPathComponent(".claude/settings.json")) {
            collect(json["mcpServers"])
        }
        return names.sorted()
    }

    /// Skill directories under ~/.claude/skills. A skill is a directory holding SKILL.md,
    /// so a stray file is not counted as one.
    public static func skills(home: URL? = nil) -> [String] {
        let root = (home ?? RedlineHome.url)
            .appendingPathComponent(".claude/skills")
        return (try? FileManager.default.contentsOfDirectory(at: root,
                                                             includingPropertiesForKeys: nil))?
            .filter {
                FileManager.default.fileExists(
                    atPath: $0.appendingPathComponent("SKILL.md").path)
            }
            .map { $0.lastPathComponent }
            .sorted() ?? []
    }

    public static func agents(home: URL? = nil) -> [String] {
        markdownNames(in: (home ?? RedlineHome.url)
            .appendingPathComponent(".claude/agents"))
    }

    /// Commands, including one level of namespace directories, which is how Claude Code
    /// groups them. The namespace is dropped: the transcript records the invoked name.
    public static func commands(home: URL? = nil) -> [String] {
        let root = (home ?? RedlineHome.url)
            .appendingPathComponent(".claude/commands")
        var out = markdownNames(in: root)
        let fm = FileManager.default
        for url in (try? fm.contentsOfDirectory(at: root,
                                                includingPropertiesForKeys: [.isDirectoryKey]))
            ?? [] {
            guard (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true
            else { continue }
            out += markdownNames(in: url)
        }
        return Array(Set(out)).sorted()
    }

    static func markdownNames(in dir: URL) -> [String] {
        (try? FileManager.default.contentsOfDirectory(at: dir,
                                                      includingPropertiesForKeys: nil))?
            .filter { $0.pathExtension == "md" }
            .map { $0.deletingPathExtension().lastPathComponent }
            .sorted() ?? []
    }

    /// Memory files and their size in characters, with `@imports` expanded. Each file is
    /// counted once however many times it is imported, because the prompt holds it once.
    ///
    /// `sessionsByDir` says how many scanned sessions ran in each project directory, so the
    /// global file can be charged to every session and a project's file only to its own.
    public static func memoryFiles(sessionsByDir: [String: Int], home: URL? = nil)
        -> [MemoryFile] {
        let root = home ?? RedlineHome.url
        let total = sessionsByDir.values.reduce(0, +)
        var targets: [(url: URL, sessions: Int, global: Bool)] = [
            (root.appendingPathComponent(".claude/CLAUDE.md"), total, true),
        ]
        targets += sessionsByDir.keys.sorted().map {
            (URL(fileURLWithPath: $0).appendingPathComponent("CLAUDE.md"),
             sessionsByDir[$0] ?? 0, false)
        }
        var out: [MemoryFile] = []
        var seen = Set<String>()
        for target in targets {
            var chars = 0
            expand(target.url, home: root, depth: 0, seen: &seen) { chars += $0 }
            guard chars > 0 else { continue }
            out.append(MemoryFile(path: target.url.path, chars: chars,
                                  sessions: target.sessions, global: target.global))
        }
        return out
    }

    /// Depth is capped rather than cycle-detected as well: `seen` already prevents a loop,
    /// and a chain deeper than this is not something to silently follow.
    static func expand(_ url: URL, home: URL, depth: Int, seen: inout Set<String>,
                       add: (Int) -> Void) {
        guard depth < 3, !seen.contains(url.path),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return }
        seen.insert(url.path)
        add(text.count)
        var imports: [URL] = []
        text.enumerateLines { line, _ in
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("@"), trimmed.count > 1 else { return }
            let raw = String(trimmed.dropFirst()).split(separator: " ").first.map(String.init)
            guard let raw, !raw.isEmpty else { return }
            if raw.hasPrefix("~/") {
                imports.append(home.appendingPathComponent(String(raw.dropFirst(2))))
            } else if AppPaths.isAbsolute(raw) {
                imports.append(URL(fileURLWithPath: raw))
            } else {
                imports.append(url.deletingLastPathComponent().appendingPathComponent(raw))
            }
        }
        for next in imports {
            expand(next, home: home, depth: depth + 1, seen: &seen, add: add)
        }
    }

    static func readJSON(_ url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    /// Gathers everything the checks need. Kept in one place so the app, the CLI and the
    /// tests all assemble the same input.
    public static func findingsInput(sessions: [SessionScan], windowDays: Int,
                                     now: Date = Date(), home: URL? = nil) -> FindingsInput {
        var byDir: [String: Int] = [:]
        for cwd in sessions.compactMap(\.cwd) { byDir[cwd, default: 0] += 1 }
        return FindingsInput(sessions: sessions,
                             configuredMCPServers: mcpServers(home: home),
                             skills: skills(home: home),
                             agents: agents(home: home),
                             commands: commands(home: home),
                             memoryFiles: memoryFiles(sessionsByDir: byDir, home: home),
                             windowDays: windowDays,
                             now: now)
    }
}
