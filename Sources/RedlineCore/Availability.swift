// Which providers this machine actually has. RedLine is useful with any one of them, so the
// UI adapts rather than showing controls for tools that are not installed.
import Foundation

public struct ProviderAvailability: Equatable {
    /// Installed providers, in the canonical order.
    public let installed: [String]

    public init(installed: [String]) { self.installed = installed }

    public var isEmpty: Bool { installed.isEmpty }

    /// With a single track there is nothing to choose between, so pickers can disappear.
    public var hasChoice: Bool { installed.count > 1 }

    public func has(_ provider: String) -> Bool {
        installed.contains { $0.caseInsensitiveCompare(provider) == .orderedSame }
    }

    /// Track options for a picker: "all" only earns its place when there is more than one.
    public var trackChoices: [String] {
        hasChoice ? [Config.autoProvider] + installed : installed
    }

    /// A provider is considered installed when the directory its tool writes to exists.
    /// Ollama also counts when the app has seen it running, since a fresh install may have
    /// no data directory yet. Claude also counts with a signed-in account and nothing local:
    /// a claude.ai user has rate limits to show even though there are no transcripts to read.
    public static func detect(home: URL? = nil,
                              ollamaReachable: Bool = false,
                              claudeAccount: Bool = false) -> ProviderAvailability {
        let fm = FileManager.default
        let root = home ?? RedlineHome.url
        var found: [String] = []

        func exists(_ path: String) -> Bool {
            fm.fileExists(atPath: root.appendingPathComponent(path).path)
        }

        if exists(".claude/projects") || exists(".claude") || claudeAccount {
            found.append("Claude")
        }
        if exists(".codex/sessions") || exists(".codex") { found.append("Codex") }
        if ollamaReachable || exists(".ollama") || exists(".local/share/redline/ollama.jsonl") {
            found.append("Ollama")
        }
        return ProviderAvailability(installed: found)
    }
}
