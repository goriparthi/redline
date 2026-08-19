// Parses Claude Code transcripts (~/.claude/projects/**/*.jsonl) into usage entries.
//
// Two ways in, and which one runs is the "Keep Local History" setting:
//   ingest(into:)  tails each transcript from where it was last read and stores the new
//                  records. What the app uses. Costs a stat per file and nothing else when
//                  nothing has changed.
//   scan(...)      parses the whole lookback window into memory, cached per file on
//                  mtime+size+cutoff. The path for a machine keeping no history, and what
//                  the parser tests drive.
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
        self.root = root ?? RedlineHome.url
            .appendingPathComponent(".claude/projects")
    }

    // MARK: - Incremental ingest

    /// Reads what is new in every transcript and stores it. Returns the number of records
    /// added, which is zero on a quiet poll and is the normal case.
    ///
    /// No lookback window applies here: a transcript is read once, and what it said is kept
    /// until retention removes it. The window belongs to the questions asked of the store,
    /// not to the reading of the files.
    @discardableResult
    public func ingest(into warehouse: Warehouse, now: Date = Date()) -> Int {
        let fm = FileManager.default
        guard fm.fileExists(atPath: root.path),
              let en = fm.enumerator(at: root, includingPropertiesForKeys:
                [.contentModificationDateKey, .fileSizeKey]) else { return 0 }

        var live = Set<String>()
        var added = 0
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
            // Nothing appended since the last pass, so there is nothing to parse. This is
            // the branch that turns a nine hundred megabyte corpus into a directory walk.
            if let mark, start == mark.byteOffset, size == mark.size { continue }

            var batch: [Entry] = []
            let next = TranscriptTail.read(url: url, from: start) { line, offset in
                guard let entry = self.entry(from: line, path: path, offset: offset)
                else { return }
                batch.append(entry)
            }
            added += warehouse.ingest(batch)
            warehouse.setIngestMark(IngestMark(path: path, provider: UsageStore.provider,
                                               size: size, byteOffset: next, mtime: mtime),
                                    at: now)
        }
        warehouse.forgetIngestMarks(notIn: live, provider: UsageStore.provider)
        return added
    }

    // MARK: - Whole window scan

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
            guard let e = self.entry(from: line, path: url.path, offset: nil),
                  e.ts > cutoff else { return }
            entries.append(e)
        }
        return entries
    }

    /// One transcript line to one usage record, or nil for the great majority of lines that
    /// carry no usage at all. `offset` is the byte position the line started at, which
    /// becomes the record's origin; the whole file parse has no position to give.
    func entry(from line: String, path: String, offset: Int?) -> Entry? {
        guard line.contains("\"usage\""), let data = line.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let msg = obj["message"] as? [String: Any],
              let usage = msg["usage"] as? [String: Any],
              let tsStr = obj["timestamp"] as? String,
              let ts = isoFrac.date(from: tsStr) ?? iso.date(from: tsStr) else { return nil }
        let model = (msg["model"] as? String) ?? "unknown"
        guard model != "<synthetic>" else { return nil }

        let input = (usage["input_tokens"] as? Int) ?? 0
        let output = (usage["output_tokens"] as? Int) ?? 0
        let cacheRead = (usage["cache_read_input_tokens"] as? Int) ?? 0
        var c5m = (usage["cache_creation_input_tokens"] as? Int) ?? 0
        var c1h = 0
        if let cc = usage["cache_creation"] as? [String: Any] {
            c5m = (cc["ephemeral_5m_input_tokens"] as? Int) ?? 0
            c1h = (cc["ephemeral_1h_input_tokens"] as? Int) ?? 0
        }
        guard input + output + cacheRead + c5m + c1h > 0 else { return nil }

        var key: String? = nil
        if let mid = msg["id"] as? String {
            key = mid + ":" + ((obj["requestId"] as? String) ?? "")
        }
        return Entry(provider: UsageStore.provider, key: key, ts: ts, model: model,
                     input: input, output: output, cacheRead: cacheRead,
                     cache5m: c5m, cache1h: c1h,
                     origin: offset.map { "\(path)#\($0)" })
    }
}
