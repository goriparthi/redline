// Reads the JSONL written by the ollama shim (scripts/ollama-shim.sh). Ollama keeps no usage
// history of its own, so anything that bypasses the shim is invisible here by design.
import Foundation

/// Ollama models run in two places, and which one is a fact worth stating plainly: cloud
/// models carry a "cloud" tag in their name, everything else runs on this machine.
public enum OllamaLocality {
    public static func isCloud(_ model: String) -> Bool {
        let m = model.lowercased()
        return m.hasSuffix(":cloud") || m.hasSuffix("-cloud") || m.contains(":cloud-")
    }

    /// A cloud model gets the cloud glyph in front of its name, so the distinction is
    /// visible in every list without widening any column
    public static func marked(_ model: String) -> String {
        isCloud(model) ? "☁ \(model)" : model
    }
}

public final class OllamaStore {
    public static let provider = "Ollama"
    private let log: URL
    private let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private let isoPlain = ISO8601DateFormatter()

    public init(log: URL? = nil) {
        self.log = log ?? AppPaths.data("ollama.jsonl")
    }

    public var isConfigured: Bool { FileManager.default.fileExists(atPath: log.path) }

    public func scan(lookbackDays: Int, now: Date = Date()) -> [Entry] {
        guard let text = try? String(contentsOf: log, encoding: .utf8) else { return [] }
        let cutoff = now.addingTimeInterval(-Double(lookbackDays + 1) * 86400)
        var out: [Entry] = []
        text.enumerateLines { line, _ in
            guard let e = self.entry(from: line, offset: nil), e.ts > cutoff else { return }
            out.append(e)
        }
        return out
    }

    /// Reads what the shim has appended since the last pass and stores it.
    @discardableResult
    public func ingest(into warehouse: Warehouse, now: Date = Date()) -> Int {
        let path = log.path
        guard let vals = try? log.resourceValues(
            forKeys: [.contentModificationDateKey, .fileSizeKey]),
            let mtime = vals.contentModificationDate,
            let size = vals.fileSize else { return 0 }
        let mark = warehouse.ingestMark(path: path)
        let start = TranscriptTail.startOffset(mark: mark, size: size)
        if let mark, start == mark.byteOffset, size == mark.size { return 0 }

        var batch: [Entry] = []
        let next = TranscriptTail.read(url: log, from: start) { line, offset in
            guard let e = self.entry(from: line, offset: offset) else { return }
            batch.append(e)
        }
        let added = warehouse.ingest(batch)
        warehouse.setIngestMark(IngestMark(path: path, provider: OllamaStore.provider,
                                           size: size, byteOffset: next, mtime: mtime),
                                at: now)
        return added
    }

    func entry(from line: String, offset: Int?) -> Entry? {
        guard let data = line.data(using: .utf8),
              let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tsStr = o["ts"] as? String,
              let ts = iso.date(from: tsStr) ?? isoPlain.date(from: tsStr) else { return nil }
        let input = int(o["prompt_eval_count"])
        let output = int(o["eval_count"])
        guard input + output > 0 else { return nil }
        // Local inference has no dollar cost, so these stay unpriced on purpose and
        // surface as token and call volume instead of spend.
        return Entry(provider: OllamaStore.provider, key: nil, ts: ts,
                     model: (o["model"] as? String) ?? "ollama",
                     input: input, output: output, cacheRead: 0, cache5m: 0, cache1h: 0,
                     origin: offset.map { "\(log.path)#\($0)" })
    }

    private func int(_ v: Any?) -> Int {
        if let i = v as? Int { return i }
        if let d = v as? Double { return Int(d) }
        return 0
    }
}
