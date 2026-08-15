// Public status feeds for the hosted providers, in Statuspage's standard JSON shape.
// Ollama local is probed directly against the local server and needs no network; Ollama
// Cloud publishes no status feed or usage API yet, so it has nothing to read.
import Foundation

public enum ServiceStatus {
    public static let claudeURL = URL(string:
        "https://status.claude.com/api/v2/status.json")!
    public static let codexURL = URL(string:
        "https://status.openai.com/api/v2/status.json")!

    public struct Report: Equatable {
        public let indicator: String     // none | minor | major | critical
        public let description: String
        public let at: Date

        public init(indicator: String, description: String, at: Date = Date()) {
            self.indicator = indicator
            self.description = description
            self.at = at
        }

        public var isOperational: Bool { indicator == "none" }

        /// Calm and factual, per the brand: report what the operator reports, never alarm
        public var phrase: String {
            switch indicator {
            case "none":  return "service ok"
            case "minor": return "minor incident reported"
            case "major", "critical": return "outage reported"
            default: return "status unknown"
            }
        }
    }

    /// Parses Statuspage's status.json. Separated from the fetch so it is testable.
    public static func parse(_ data: Data) -> Report? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let status = json["status"] as? [String: Any],
              let indicator = status["indicator"] as? String else { return nil }
        return Report(indicator: indicator,
                      description: (status["description"] as? String) ?? "")
    }

    public static func fetch(_ url: URL, completion: @escaping (Report?) -> Void) {
        var req = URLRequest(url: url)
        req.timeoutInterval = 10
        URLSession.shared.dataTask(with: req) { data, _, _ in
            completion(data.flatMap(parse))
        }.resume()
    }
}
