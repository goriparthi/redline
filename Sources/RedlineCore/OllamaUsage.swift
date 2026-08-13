// Reads the JSONL written by the ollama shim (scripts/ollama-shim.sh). Ollama keeps no usage
// history of its own, so anything that bypasses the shim is invisible here by design.
import Foundation

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
        self.log = log ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/share/redline/ollama.jsonl")
    }

    public var isConfigured: Bool { FileManager.default.fileExists(atPath: log.path) }

    public func scan(lookbackDays: Int, now: Date = Date()) -> [Entry] {
        guard let text = try? String(contentsOf: log, encoding: .utf8) else { return [] }
        let cutoff = now.addingTimeInterval(-Double(lookbackDays + 1) * 86400)
        var out: [Entry] = []
        text.enumerateLines { line, _ in
            guard let data = line.data(using: .utf8),
                  let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tsStr = o["ts"] as? String,
                  let ts = self.iso.date(from: tsStr) ?? self.isoPlain.date(from: tsStr),
                  ts > cutoff else { return }
            let input = self.int(o["prompt_eval_count"])
            let output = self.int(o["eval_count"])
            guard input + output > 0 else { return }
            // Local inference has no dollar cost, so these stay unpriced on purpose and
            // surface as token and call volume instead of spend.
            out.append(Entry(provider: OllamaStore.provider, key: nil, ts: ts,
                             model: (o["model"] as? String) ?? "ollama",
                             input: input, output: output,
                             cacheRead: 0, cache5m: 0, cache1h: 0))
        }
        return out
    }

    private func int(_ v: Any?) -> Int {
        if let i = v as? Int { return i }
        if let d = v as? Double { return Int(d) }
        return 0
    }
}
