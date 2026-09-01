// Runs the findings checks off the main thread, rarely.
//
// A findings pass reads more of each transcript than the usage scan does, so it is not
// something to do on every poll. Once at launch and every few hours after is enough: setup
// changes at the speed of someone editing a config file, not at the speed of a token counter.
import Foundation
import RedlineCore

final class FindingsService {
    /// How long a report stands before another scan is worth the disk. Setup does not
    /// change often, and a background pass that nobody asked for should be rare.
    static let interval: TimeInterval = 6 * 3600
    /// The window the checks reason over. Long enough that a fortnight-old habit shows up,
    /// short enough that something you stopped doing drops off.
    static let windowDays = 14

    private let scanner = TranscriptScanner()
    private let queue = DispatchQueue(label: "findings-scan", qos: .utility)

    private(set) var report: FindingsReport?
    private(set) var lastRun: Date?
    /// Called on the main thread whenever a new report lands.
    var onUpdate: ((FindingsReport) -> Void)?

    /// True from the moment a scan is accepted until its report lands, so the UI can show
    /// that a click did something. Returned by both entry points for the same reason.
    private(set) var isRunning = false

    @discardableResult
    func refreshIfDue(config: Config, now: Date = Date()) -> Bool {
        guard config.findingsScans else { return false }
        if let lastRun, now.timeIntervalSince(lastRun) < Self.interval { return false }
        return refresh(config: config, now: now)
    }

    @discardableResult
    func refresh(config: Config, now: Date = Date()) -> Bool {
        guard !isRunning else { return false }
        isRunning = true
        lastRun = now
        queue.async { [weak self] in
            guard let self else { return }
            let sessions = self.scanner.scan(lookbackDays: Self.windowDays, now: now)
            // Nothing here touches the UI: the scan can take ten seconds on a busy machine
            // the first time through, and only the cache makes the next one quick.
            let report = sessions.isEmpty
                ? FindingsReport(generatedAt: now, windowDays: Self.windowDays,
                                 sessionsScanned: 0, findings: [])
                : Findings.report(
                    ClaudeSetup.findingsInput(sessions: sessions,
                                              windowDays: Self.windowDays, now: now),
                    config: config)
            DispatchQueue.main.async {
                self.isRunning = false
                self.report = report
                self.onUpdate?(report)
            }
        }
        return true
    }
}
