// Talks to a local Ollama server. Loopback only, no auth, and every call is scoped to the
// host in OLLAMA_HOST so nothing here can reach off the machine by accident.
import Foundation
import RedlineCore

struct OllamaState {
    var reachable = false
    var models: [OllamaModel] = []
    var running: [OllamaRunningModel] = []
    var version: String?
    var error: String?
    var busy: Set<String> = []
    var checkedAt: Date?
}

final class OllamaService: ObservableObject {
    @Published var state = OllamaState()

    private let host: URL
    private let session: URLSession

    init(host: String = ProcessInfo.processInfo.environment["OLLAMA_HOST"]
            ?? "http://127.0.0.1:11434") {
        // A malformed override must not silently become a request to somewhere else
        self.host = URL(string: host) ?? URL(string: "http://127.0.0.1:11434")!
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 10
        cfg.waitsForConnectivity = false
        self.session = URLSession(configuration: cfg)
    }

    var hostDescription: String { host.absoluteString }

    func refresh() {
        Task { await reload() }
    }

    @MainActor
    private func set(_ mutate: (inout OllamaState) -> Void) {
        mutate(&state)
    }

    func reload() async {
        async let version = getJSON("/api/version")
        async let tags = getJSON("/api/tags")
        async let ps = getJSON("/api/ps")
        let (v, t, p) = await (version, tags, ps)

        await set {
            $0.checkedAt = Date()
            // /api/version answering is the signal the server is up
            $0.reachable = v != nil
            $0.version = v?["version"] as? String
            $0.models = t.map(OllamaParse.models) ?? []
            $0.running = p.map(OllamaParse.running) ?? []
            $0.error = v == nil ? "Ollama is not running" : nil
        }
    }

    /// Loads a model with an empty prompt so it becomes resident without generating anything.
    func start(_ model: String, keepAlive: String = "30m") async {
        await mark(model, busy: true)
        _ = await postJSON("/api/generate",
                           body: ["model": model, "prompt": "", "keep_alive": keepAlive])
        await reload()
        await mark(model, busy: false)
    }

    /// keep_alive: 0 tells Ollama to unload immediately. This frees memory; it does not delete
    /// the model, and nothing here removes downloaded weights.
    func stop(_ model: String) async {
        await mark(model, busy: true)
        _ = await postJSON("/api/generate",
                           body: ["model": model, "prompt": "", "keep_alive": 0])
        await reload()
        await mark(model, busy: false)
    }

    @MainActor
    private func mark(_ model: String, busy: Bool) {
        if busy { state.busy.insert(model) } else { state.busy.remove(model) }
    }

    /// Snapshot form for the widget, which has no network access of its own.
    func snapshotSection() async -> Snapshot.Ollama? {
        async let version = getJSON("/api/version")
        async let tags = getJSON("/api/tags")
        async let ps = getJSON("/api/ps")
        let (v, t, p) = await (version, tags, ps)
        // Absent entirely when the server never answered, so the widget can say so rather
        // than implying zero models
        guard v != nil || t != nil || p != nil else {
            return Snapshot.Ollama(reachable: false, version: nil, running: [],
                                   downloadedCount: 0, downloadedBytes: 0)
        }
        let models = t.map(OllamaParse.models) ?? []
        let running = (p.map(OllamaParse.running) ?? []).map {
            Snapshot.Ollama.Running(name: $0.name, sizeBytes: $0.sizeBytes,
                                    vramShare: $0.vramShare)
        }
        return Snapshot.Ollama(reachable: v != nil,
                               version: v?["version"] as? String,
                               running: running,
                               downloadedCount: models.count,
                               downloadedBytes: models.reduce(0) { $0 + $1.sizeBytes })
    }

    // MARK: - Transport

    private func url(_ path: String) -> URL? {
        URL(string: path, relativeTo: host)
    }

    private func getJSON(_ path: String) async -> [String: Any]? {
        guard let url = url(path) else { return nil }
        do {
            let (data, resp) = try await session.data(from: url)
            guard (resp as? HTTPURLResponse).map({ (200..<300).contains($0.statusCode) }) ?? false
            else { return nil }
            return try JSONSerialization.jsonObject(with: data) as? [String: Any]
        } catch {
            return nil
        }
    }

    private func postJSON(_ path: String, body: [String: Any]) async -> [String: Any]? {
        guard let url = url(path) else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        do {
            let (data, _) = try await session.data(for: req)
            return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        } catch {
            await set { $0.error = "Request failed: \(error.localizedDescription)" }
            return nil
        }
    }
}
