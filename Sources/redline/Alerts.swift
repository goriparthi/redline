// Delivery for the events Alerting decides on. The decision is in the core and tested; this
// is the part that talks to macOS.
//
// Nothing is posted until the user turns alerts on, and the authorization prompt is raised at
// that moment rather than on first launch: an app that asks to notify before it has anything
// to say is an app people say no to.
import Foundation
import RedlineCore
import UserNotifications

final class AlertCenter {
    private var state = AlertStore.load()
    private var cadenceState = CadenceStore.load()
    /// Notifications are unavailable outside an app bundle, which is how the binary runs
    /// from a plain `swift build`. Checked once rather than guarded at every call site.
    private let available = Bundle.main.bundleIdentifier != nil
    private var authorized = false
    private var askedThisLaunch = false

    /// Posts whatever this poll's readings have newly earned. Returns the events fired, so
    /// the caller can log or test without reaching into the notification centre.
    @discardableResult
    func evaluate(windows: [LimitWindow], paces: [Pace], config: Config,
                  isStale: @escaping (LimitWindow) -> Bool = { _ in false },
                  now: Date = Date()) -> [AlertEvent] {
        let events = Alerting.evaluate(windows: windows, paces: paces, config: config,
                                       now: now, isStale: isStale, state: &state)
        AlertStore.save(state)
        guard config.alerts, !events.isEmpty else { return events }
        deliver(events)
        return events
    }

    /// Posts whatever the shape of the day has newly earned. Same delivery path and same
    /// permission as the limit alerts; a cue never makes a sound, because none of them is
    /// the kind of thing that should pull someone out of what they are doing.
    @discardableResult
    func evaluateCadence(entries: [Entry], config: Config,
                         now: Date = Date()) -> [CadenceCue] {
        let cues = CadenceRules.evaluate(entries: entries, config: config, now: now,
                                         state: &cadenceState)
        CadenceStore.save(cadenceState)
        guard config.mindfulCues, !cues.isEmpty, available else { return cues }
        let center = UNUserNotificationCenter.current()
        if !authorized, !askedThisLaunch {
            requestAuthorization { [weak self] granted in
                guard granted else { return }
                self?.post(cues, to: center)
            }
            return cues
        }
        post(cues, to: center)
        return cues
    }

    private func post(_ cues: [CadenceCue], to center: UNUserNotificationCenter) {
        for cue in cues {
            let content = UNMutableNotificationContent()
            content.title = cue.title
            content.body = cue.body
            center.add(UNNotificationRequest(identifier: cue.id, content: content,
                                             trigger: nil))
        }
    }

    /// Called when the setting is switched on. macOS only shows the prompt once per app, so
    /// a refusal is remembered by the system and this simply stops delivering.
    func requestAuthorization(completion: ((Bool) -> Void)? = nil) {
        guard available else {
            completion?(false)
            return
        }
        askedThisLaunch = true
        UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .sound]) { [weak self] granted, _ in
                DispatchQueue.main.async {
                    self?.authorized = granted
                    completion?(granted)
                }
            }
    }

    private func deliver(_ events: [AlertEvent]) {
        guard available else { return }
        let center = UNUserNotificationCenter.current()
        // Authorization is asked for on the first delivery too, in case the setting was
        // turned on by hand in config.json rather than from the menu.
        if !authorized, !askedThisLaunch {
            requestAuthorization { [weak self] granted in
                guard granted else { return }
                self?.post(events, to: center)
            }
            return
        }
        post(events, to: center)
    }

    private func post(_ events: [AlertEvent], to center: UNUserNotificationCenter) {
        for event in events {
            let content = UNMutableNotificationContent()
            content.title = event.title
            content.body = event.body
            // A limit actually reached is the only one that gets a sound; the rest are
            // information arriving while you work.
            if case .limitReached = event.kind { content.sound = .default }
            center.add(UNNotificationRequest(identifier: event.id, content: content,
                                             trigger: nil))
        }
    }
}
