// The events the watcher has decided on, published for a shell that only delivers them.
//
// Off macOS there is no app doing the deciding, so `redline watch` evaluates and writes here.
// A shell reads this and posts; it never works out for itself when something is worth saying.
import Foundation

public enum AlertFeed {
    public static func url(home: URL? = nil) -> URL {
        AppPaths.data("alert-feed.json", in: home)
    }

    /// The word for each kind, which is the contract. A shell may want to treat a limit
    /// differently from a threshold, and it should not have to read the wording to tell.
    public static func name(of kind: AlertEvent.Kind) -> String {
        switch kind {
        case .threshold:    return "threshold"
        case .limitReached: return "limit_reached"
        case .projection:   return "projection"
        case .reset:        return "reset"
        }
    }

    /// A limit actually reached is the only one that makes a sound. The rest arrive while
    /// someone is working, and the decision is published rather than left to each shell.
    public static func makesSound(_ kind: AlertEvent.Kind) -> Bool {
        if case .limitReached = kind { return true }
        return false
    }

    public static func row(_ event: AlertEvent) -> [String: Any] {
        var row: [String: Any] = [
            "id": event.id,
            "kind": name(of: event.kind),
            "provider": event.provider,
            "key": event.key,
            "title": event.title,
            "body": event.body,
            "sound": makesSound(event.kind),
        ]
        if case let .threshold(percent) = event.kind { row["percent"] = percent }
        return row
    }

    /// What the last publication counted. Read back rather than kept in memory so the count
    /// survives a restart: a shell that remembers where it was must not be sent backwards.
    public static func sequence(at url: URL? = nil) -> Int {
        let url = url ?? AlertFeed.url()
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let seq = json["seq"] as? Int
        else { return 0 }
        return seq
    }

    /// Writes one batch and returns its sequence number, or nil when nothing was written.
    /// Only called with events, because an empty batch would move the sequence for nothing
    /// and wake every reader watching the file.
    @discardableResult
    public static func publish(_ events: [AlertEvent], now: Date = Date(),
                               to url: URL? = nil) -> Int? {
        guard !events.isEmpty else { return nil }
        let url = url ?? AlertFeed.url()
        let seq = sequence(at: url) + 1
        let root: [String: Any] = [
            "seq": seq,
            "at": ISO8601DateFormatter().string(from: now),
            "events": events.map(row),
        ]
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: root,
                                                  options: [.prettyPrinted, .sortedKeys])
            try data.write(to: url, options: .atomic)
            return seq
        } catch {
            Diag.log.error("alerts.publish_failed", "could not write the alert feed",
                           ["path": url.path, "error": String(describing: error)])
            return nil
        }
    }
}
