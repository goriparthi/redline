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
