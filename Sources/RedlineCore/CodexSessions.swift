// Parses Codex CLI rollout transcripts (~/.codex/sessions/**/*.jsonl). Needs no auth:
// Codex writes both its rate-limit percentages and its token counts straight to disk.
import Foundation

public struct CodexSnapshot {
    public var entries: [Entry] = []
    public var limits: [LimitWindow] = []
    public var limitsAt: Date?
}

public final class CodexStore {
    public static let provider = "Codex"
    private let root: URL
    private let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private let iso = ISO8601DateFormatter()

    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/sessions")
    }

    public func scan(lookbackDays: Int, now: Date = Date()) -> CodexSnapshot {
        var snap = CodexSnapshot()
        let fm = FileManager.default
        guard fm.fileExists(atPath: root.path),
              let en = fm.enumerator(at: root,
                                     includingPropertiesForKeys: [.contentModificationDateKey])
        else { return snap }

        let cutoff = now.addingTimeInterval(-Double(lookbackDays + 1) * 86400)
        for case let url as URL in en {
            guard url.pathExtension == "jsonl" else { continue }
            // Limits come from the newest event overall, so a stale file cannot supply them
            let mtime = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate
            guard let mtime, mtime > cutoff else { continue }
            let part = parse(url: url, cutoff: cutoff)
            snap.entries += part.entries
            if let at = part.limitsAt, at > (snap.limitsAt ?? .distantPast) {
                snap.limits = part.limits
                snap.limitsAt = at
            }
        }
        // Sessions can be days old, so discard windows that have already rolled over
        snap.limits = LimitParser.sorted(LimitParser.unexpired(snap.limits, now: now))
        return snap
    }

    func parse(url: URL, cutoff: Date) -> CodexSnapshot {
        var snap = CodexSnapshot()
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return snap }
        text.enumerateLines { line, _ in
            guard line.contains("token_count"), let data = line.data(using: .utf8),
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let payload = obj["payload"] as? [String: Any],
                  payload["type"] as? String == "token_count",
                  let tsStr = obj["timestamp"] as? String,
                  let ts = self.isoFrac.date(from: tsStr) ?? self.iso.date(from: tsStr),
                  ts > cutoff else { return }

            if let rl = payload["rate_limits"] as? [String: Any] {
                let windows = LimitParser.codexRateLimits(rl)
                if !windows.isEmpty, ts > (snap.limitsAt ?? .distantPast) {
                    snap.limits = windows
                    snap.limitsAt = ts
                }
            }

            // last_token_usage is the per-turn delta. total_token_usage is cumulative for
            // the session and would double count once summed across events.
            guard let info = payload["info"] as? [String: Any],
                  let last = info["last_token_usage"] as? [String: Any] else { return }
            let input = self.int(last["input_tokens"])
            let cached = self.int(last["cached_input_tokens"])
            let output = self.int(last["output_tokens"])
                + self.int(last["reasoning_output_tokens"])
            // Codex reports cached tokens inside input_tokens; split them so the cache read
            // is not billed at the full input rate.
            let fresh = max(0, input - cached)
            guard fresh + cached + output > 0 else { return }
            snap.entries.append(Entry(provider: CodexStore.provider,
                                      key: nil, ts: ts,
                                      model: self.model(for: obj, payload: payload),
                                      input: fresh, output: output,
                                      cacheRead: cached, cache5m: 0, cache1h: 0))
        }
        return snap
    }

    private func model(for obj: [String: Any], payload: [String: Any]) -> String {
        (payload["model"] as? String) ?? (obj["model"] as? String) ?? "codex"
    }

    private func int(_ v: Any?) -> Int {
        if let i = v as? Int { return i }
        if let d = v as? Double { return Int(d) }
        return 0
    }
}
