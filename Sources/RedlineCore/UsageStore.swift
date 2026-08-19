// Parses Claude Code transcripts (~/.claude/projects/**/*.jsonl) into usage entries, with
// per-file caching keyed on mtime+size+cutoff so unchanged files are not re-read.
import Foundation

public final class UsageStore {
    public static let provider = "Claude"
    private let root: URL
    private var fileCache:
        [String: (mtime: Date, size: Int, cutoff: Date, entries: [Entry])] = [:]
    private let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/projects")
    }

    public func scan(lookbackDays: Int, now: Date = Date()) -> [Entry] {
        let fm = FileManager.default
        let cutoff = now.addingTimeInterval(-Double(lookbackDays + 1) * 86400)
        var livePaths = Set<String>()

        guard fm.fileExists(atPath: root.path),
              let en = fm.enumerator(at: root, includingPropertiesForKeys:
                [.contentModificationDateKey, .fileSizeKey]) else { return [] }

        for case let url as URL in en {
            guard url.pathExtension == "jsonl" else { continue }
            guard let vals = try? url.resourceValues(
                forKeys: [.contentModificationDateKey, .fileSizeKey]),
                let mtime = vals.contentModificationDate,
                let size = vals.fileSize, mtime > cutoff else { continue }
            let path = url.path
            livePaths.insert(path)
            // The cutoff is part of the key because entries are filtered at parse time: a
            // cache built for 14 days answered a 30 day scan with 14 days of data.
            if let c = fileCache[path], c.mtime == mtime, c.size == size,
               c.cutoff <= cutoff { continue }
            fileCache[path] = (mtime, size, cutoff, parse(url: url, cutoff: cutoff))
        }
        fileCache = fileCache.filter { livePaths.contains($0.key) }

        // Dedup across files: resumed sessions copy identical message ids between transcripts
        var seen = Set<String>()
        var out: [Entry] = []
        for (_, c) in fileCache {
            // A cache parsed for a wider window reaches past this scan's cutoff, so the
            // window is enforced again here rather than trusted from the parse
            for e in c.entries where e.ts > cutoff {
                if let k = e.key {
                    if seen.contains(k) { continue }
                    seen.insert(k)
                }
                out.append(e)
            }
        }
        return out
    }

    func parse(url: URL, cutoff: Date) -> [Entry] {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        var entries: [Entry] = []
        text.enumerateLines { line, _ in
            guard line.contains("\"usage\""), let data = line.data(using: .utf8),
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let msg = obj["message"] as? [String: Any],
                  let usage = msg["usage"] as? [String: Any],
                  let tsStr = obj["timestamp"] as? String,
                  let ts = self.isoFrac.date(from: tsStr) ?? self.iso.date(from: tsStr),
                  ts > cutoff else { return }
            let model = (msg["model"] as? String) ?? "unknown"
            guard model != "<synthetic>" else { return }

            let input = (usage["input_tokens"] as? Int) ?? 0
            let output = (usage["output_tokens"] as? Int) ?? 0
            let cacheRead = (usage["cache_read_input_tokens"] as? Int) ?? 0
            var c5m = (usage["cache_creation_input_tokens"] as? Int) ?? 0
            var c1h = 0
            if let cc = usage["cache_creation"] as? [String: Any] {
                c5m = (cc["ephemeral_5m_input_tokens"] as? Int) ?? 0
                c1h = (cc["ephemeral_1h_input_tokens"] as? Int) ?? 0
            }
            guard input + output + cacheRead + c5m + c1h > 0 else { return }

            var key: String? = nil
            if let mid = msg["id"] as? String {
                key = mid + ":" + ((obj["requestId"] as? String) ?? "")
            }
            entries.append(Entry(provider: UsageStore.provider, key: key, ts: ts,
                                 model: model, input: input, output: output,
                                 cacheRead: cacheRead, cache5m: c5m, cache1h: c1h))
        }
        return entries
    }
}
