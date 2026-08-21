// Keeping the warehouse current without an app.
//
// On macOS the menu bar app does this. Off macOS nothing does, so history only exists for as
// long as Claude Code keeps its transcripts, which is thirty days. This is the headless half
// of that job, and the thing an autostart entry should point at.
import Foundation
import RedlineCore

public final class WatchLoop {
    public struct Options {
        /// The floor. The watchers are an optimisation on top of this, never a replacement:
        /// transcripts live one directory below the ones being watched, and no backend here
        /// watches a subtree.
        public var sweepSeconds: TimeInterval
        /// A burst of writes is one edit as far as anyone cares, and a transcript is appended
        /// to line by line.
        public var debounceSeconds: TimeInterval
        public var home: URL
        /// What a pass actually does. Injectable so a test can exercise the scheduling
        /// without reading anyone's real transcripts or writing a real warehouse.
        public var ingest: () -> Ingest.Outcome?

        public init(sweepSeconds: TimeInterval = 60, debounceSeconds: TimeInterval = 2,
                    home: URL = RedlineHome.url,
                    ingest: @escaping () -> Ingest.Outcome? = {
                        Ingest.run(config: Config.load())
                    }) {
            self.sweepSeconds = sweepSeconds
            self.debounceSeconds = debounceSeconds
            self.home = home
            self.ingest = ingest
        }
    }

    public enum Event {
        case started(watching: [URL], sweep: TimeInterval)
        case ingested(Ingest.Outcome, reason: String)
        case historyOff
    }

    private let options: Options
    private let report: (Event) -> Void
    private let queue = DispatchQueue(label: "redline.watch")
    private var watchers: [String: DirectoryWatching] = [:]
    private var pending: DispatchWorkItem?
    private let stopped = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var running = false

    public init(options: Options = Options(), report: @escaping (Event) -> Void = { _ in }) {
        self.options = options
        self.report = report
    }

    /// The roots. Transcripts do not live in these, they live below them: Claude keeps one
    /// directory per project and Codex a year/month/day tree, so watching only the roots sees
    /// a new session appear and never sees it written to.
    public var roots: [URL] {
        [options.home.appendingPathComponent(".claude/projects"),
         options.home.appendingPathComponent(".codex/sessions"),
         AppPaths.data(in: options.home == RedlineHome.url ? nil : options.home)]
    }

    /// Every directory worth watching: the roots that exist, plus their descendants down to
    /// `maxDepth`, which covers Claude at one level and Codex at three.
    ///
    /// Capped because this walks a tree someone else owns, and an unbounded watch count is
    /// how a background process runs a machine out of file descriptors.
    public func directoriesToWatch(maxDepth: Int = 3, limit: Int = 128) -> [URL] {
        let fm = FileManager.default
        var found: [URL] = []
        var frontier = roots.filter { fm.fileExists(atPath: $0.path) }
        var depth = 0
        while !frontier.isEmpty, depth <= maxDepth, found.count < limit {
            found.append(contentsOf: frontier)
            guard depth < maxDepth else { break }
            frontier = frontier.flatMap { parent -> [URL] in
                let children = (try? fm.contentsOfDirectory(
                    at: parent, includingPropertiesForKeys: [.isDirectoryKey],
                    options: [.skipsHiddenFiles])) ?? []
                return children.filter {
                    (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
                }
            }
            depth += 1
        }
        return Array(found.prefix(limit))
    }

    /// Runs until `stop()`. Ingests once on entry, so starting the loop is itself a catch-up.
    public func run() {
        lock.lock()
        running = true
        lock.unlock()

        refreshWatchers()
        report(.started(watching: watchedPaths, sweep: options.sweepSeconds))
        ingest(reason: "start")

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + options.sweepSeconds, repeating: options.sweepSeconds)
        timer.setEventHandler { [weak self] in
            // The sweep re-walks the tree as well, so a project created while nothing was
            // being written still gets a watcher of its own
            self?.refreshWatchers()
            self?.ingest(reason: "sweep")
        }
        timer.resume()

        stopped.wait()
        timer.cancel()
        lock.lock()
        watchers.values.forEach { $0.cancel() }
        watchers.removeAll()
        lock.unlock()
    }

    public func stop() {
        lock.lock()
        let wasRunning = running
        running = false
        lock.unlock()
        guard wasRunning else { return }
        pending?.cancel()
        stopped.signal()
    }

    public var watchedPaths: [URL] {
        lock.lock(); defer { lock.unlock() }
        return watchers.keys.sorted().map { URL(fileURLWithPath: $0) }
    }

    /// Adds a watcher for anything new and drops one for anything gone. Cheap enough to run
    /// on every sweep, which is what keeps a brand new project directory from being missed.
    private func refreshWatchers() {
        let wanted = directoriesToWatch()
        let wantedPaths = Set(wanted.map { $0.path })

        lock.lock()
        let existing = Set(watchers.keys)
        for gone in existing.subtracting(wantedPaths) {
            watchers.removeValue(forKey: gone)?.cancel()
        }
        let missing = wanted.filter { !existing.contains($0.path) }
        lock.unlock()

        for directory in missing {
            guard let watcher = DirectoryWatcher.watch(directory, queue: queue, onChange: {
                [weak self] in self?.schedule(reason: "change")
            }) else { continue }
            lock.lock()
            watchers[directory.path] = watcher
            lock.unlock()
        }
    }

    /// Collapses a burst into one pass. Each new change pushes the work later rather than
    /// queueing another one behind it.
    private func schedule(reason: String) {
        pending?.cancel()
        let work = DispatchWorkItem { [weak self] in
            // A change may have been a new directory appearing, so pick it up before reading
            self?.refreshWatchers()
            self?.ingest(reason: reason)
        }
        pending = work
        queue.asyncAfter(deadline: .now() + options.debounceSeconds, execute: work)
    }

    private func ingest(reason: String) {
        lock.lock()
        let active = running
        lock.unlock()
        guard active else { return }
        guard let outcome = options.ingest() else {
            report(.historyOff)
            return
        }
        report(.ingested(outcome, reason: reason))
    }
}
