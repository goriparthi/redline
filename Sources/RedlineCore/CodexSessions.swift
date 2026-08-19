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
        self.root = root ?? RedlineHome.url
            .appendingPathComponent(".codex/sessions")
    }

    /// Reads what is new in every rollout transcript, stores the usage records, and returns
    /// the percentages.
    ///
    /// Limits are the awkward part of tailing. They live inside the same events as the token
    /// counts, so a poll that reads no new lines reads no percentages either, and a pruned
    /// session file would take the last known ones with it. The stored samples answer that:
    /// when this pass found nothing newer, the newest reading already recorded stands, and
    /// the caller is told when it was taken rather than being left to assume it is current.
    public func ingest(into warehouse: Warehouse, now: Date = Date()) -> CodexSnapshot {
        var snap = CodexSnapshot()
        let fm = FileManager.default
        guard fm.fileExists(atPath: root.path),
              let en = fm.enumerator(at: root, includingPropertiesForKeys:
                [.contentModificationDateKey, .fileSizeKey]) else { return snap }

        var live = Set<String>()
        for case let url as URL in en {
            guard url.pathExtension == "jsonl" else { continue }
            guard let vals = try? url.resourceValues(
                forKeys: [.contentModificationDateKey, .fileSizeKey]),
                let mtime = vals.contentModificationDate,
                let size = vals.fileSize else { continue }
            let path = url.path
            live.insert(path)
            let mark = warehouse.ingestMark(path: path)
            let start = TranscriptTail.startOffset(mark: mark, size: size)
            if let mark, start == mark.byteOffset, size == mark.size { continue }

            var batch: [Entry] = []
            let next = TranscriptTail.read(url: url, from: start) { line, offset in
                let parsed = self.event(from: line, path: path, offset: offset)
                if let entry = parsed.entry { batch.append(entry) }
                if let windows = parsed.limits, !windows.isEmpty, let at = parsed.ts,
                   at > (snap.limitsAt ?? .distantPast) {
                    snap.limits = windows
                    snap.limitsAt = at
                }
            }
            warehouse.ingest(batch)
            snap.entries += batch
            warehouse.setIngestMark(IngestMark(path: path, provider: CodexStore.provider,
                                               size: size, byteOffset: next, mtime: mtime),
                                    at: now)
        }
        warehouse.forgetIngestMarks(notIn: live, provider: CodexStore.provider)

        if snap.limits.isEmpty, let stored = warehouse.latestLimits(provider: CodexStore.provider) {
            snap.limits = stored.windows
            snap.limitsAt = stored.at
        }
        // Sessions can be days old, so discard windows that have already rolled over
        snap.limits = LimitParser.sorted(LimitParser.unexpired(snap.limits, now: now))
        return snap
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
            let parsed = self.event(from: line, path: url.path, offset: nil)
            guard let ts = parsed.ts, ts > cutoff else { return }
            if let windows = parsed.limits, !windows.isEmpty,
               ts > (snap.limitsAt ?? .distantPast) {
                snap.limits = windows
                snap.limitsAt = ts
            }
            if let entry = parsed.entry { snap.entries.append(entry) }
        }
        return snap
    }

    /// One rollout line, split into the three things it can carry. Both readers go through
    /// here so a tailed file and a whole file cannot disagree about what a line means.
    func event(from line: String, path: String,
               offset: Int?) -> (entry: Entry?, limits: [LimitWindow]?, ts: Date?) {
        guard line.contains("token_count"), let data = line.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let payload = obj["payload"] as? [String: Any],
              payload["type"] as? String == "token_count",
              let tsStr = obj["timestamp"] as? String,
              let ts = isoFrac.date(from: tsStr) ?? iso.date(from: tsStr)
        else { return (nil, nil, nil) }

        var limits: [LimitWindow]?
        if let rl = payload["rate_limits"] as? [String: Any] {
            limits = LimitParser.codexRateLimits(rl)
        }

        // last_token_usage is the per-turn delta. total_token_usage is cumulative for
        // the session and would double count once summed across events.
        guard let info = payload["info"] as? [String: Any],
              let last = info["last_token_usage"] as? [String: Any]
        else { return (nil, limits, ts) }
        let input = int(last["input_tokens"])
        let cached = int(last["cached_input_tokens"])
        let output = int(last["output_tokens"]) + int(last["reasoning_output_tokens"])
        // Codex reports cached tokens inside input_tokens; split them so the cache read
        // is not billed at the full input rate.
        let fresh = max(0, input - cached)
        guard fresh + cached + output > 0 else { return (nil, limits, ts) }
        let entry = Entry(provider: CodexStore.provider, key: nil, ts: ts,
                          model: model(for: obj, payload: payload),
                          input: fresh, output: output, cacheRead: cached,
                          cache5m: 0, cache1h: 0,
                          origin: offset.map { "\(path)#\($0)" })
        return (entry, limits, ts)
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
