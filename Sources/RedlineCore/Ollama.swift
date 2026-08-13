// Parsing for the Ollama HTTP API. Network calls live in the app target; only the shapes are
// here so they can be tested without a running server.
import Foundation

public struct OllamaModel: Equatable, Identifiable {
    public let name: String
    public let sizeBytes: Int64
    public let parameterSize: String?
    public let quantization: String?
    public let modifiedAt: Date?

    public var id: String { name }

    /// Family without the tag, e.g. "qwen3-coder" from "qwen3-coder:30b"
    public var family: String {
        name.split(separator: ":").first.map(String.init) ?? name
    }

    public var tag: String? {
        let parts = name.split(separator: ":")
        return parts.count > 1 ? String(parts[1]) : nil
    }
}

public struct OllamaRunningModel: Equatable, Identifiable {
    public let name: String
    public let sizeBytes: Int64
    public let sizeVRAM: Int64
    public let expiresAt: Date?

    public var id: String { name }

    /// Share of the loaded weights held in VRAM rather than system memory
    public var vramShare: Double {
        sizeBytes > 0 ? min(1, Double(sizeVRAM) / Double(sizeBytes)) : 0
    }
}

public enum OllamaParse {
    private static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let isoPlain = ISO8601DateFormatter()

    static func date(_ v: Any?) -> Date? {
        guard let s = v as? String else { return nil }
        return iso.date(from: s) ?? isoPlain.date(from: s)
    }

    static func int64(_ v: Any?) -> Int64 {
        if let i = v as? Int64 { return i }
        if let i = v as? Int { return Int64(i) }
        if let d = v as? Double { return Int64(d) }
        return 0
    }

    /// GET /api/tags
    public static func models(_ json: [String: Any]) -> [OllamaModel] {
        guard let list = json["models"] as? [[String: Any]] else { return [] }
        return list.compactMap { m in
            guard let name = (m["name"] as? String) ?? (m["model"] as? String),
                  !name.isEmpty else { return nil }
            let details = m["details"] as? [String: Any]
            return OllamaModel(name: name,
                               sizeBytes: int64(m["size"]),
                               parameterSize: details?["parameter_size"] as? String,
                               quantization: details?["quantization_level"] as? String,
                               modifiedAt: date(m["modified_at"]))
        }
        .sorted { $0.name < $1.name }
    }

    /// GET /api/ps
    public static func running(_ json: [String: Any]) -> [OllamaRunningModel] {
        guard let list = json["models"] as? [[String: Any]] else { return [] }
        return list.compactMap { m in
            guard let name = (m["name"] as? String) ?? (m["model"] as? String),
                  !name.isEmpty else { return nil }
            return OllamaRunningModel(name: name,
                                      sizeBytes: int64(m["size"]),
                                      sizeVRAM: int64(m["size_vram"]),
                                      expiresAt: date(m["expires_at"]))
        }
        .sorted { $0.name < $1.name }
    }
}

public func fmtBytes(_ n: Int64) -> String {
    let d = Double(n)
    switch d {
    case 1_000_000_000...: return String(format: "%.1f GB", d / 1_000_000_000)
    case 1_000_000...:     return String(format: "%.0f MB", d / 1_000_000)
    case 1_000...:         return String(format: "%.0f KB", d / 1_000)
    default:               return "\(n) B"
    }
}
