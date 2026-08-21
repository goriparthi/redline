// Building the published snapshot from disk alone.
//
// No network, no token, no credential store: everything here comes from files the machine
// already has, which is what lets a headless watch run publish the same wire format the
// widget reads without anyone being signed in.
import Foundation

public enum SnapshotBuilder {
    public struct Inputs {
        public let entries: [Entry]
        public let limits: [LimitWindow]
        public let claudeLimitsAsOf: Date?
    }

    /// Reads every enabled provider and returns a snapshot for `now`.
    ///
    /// Passing a warehouse makes the read incremental, the same trade the app makes: with
    /// history on, each transcript is read from where the last pass stopped.
    public static func fromDisk(config: Config, warehouse: Warehouse? = nil,
                                home: URL? = nil, now: Date = Date()) -> Snapshot {
        let store = warehouse ?? (config.recordHistory ? Warehouse() : nil)
        let inputs = gather(config: config, warehouse: store, home: home, now: now)
        // Recorded so the next pass still has them. An incremental read reports only what it
        // newly parsed, so without this a limit window is published once and then vanishes
        // while it is still perfectly true.
        if let store, config.recordHistory, !inputs.limits.isEmpty {
            store.recordLimits(inputs.limits.filter { !$0.isUninformative }, at: now)
        }
        let today = aggregate(inputs.entries, since: startOfDay(now), config: config)
        let week = aggregate(inputs.entries, since: now.addingTimeInterval(-7 * 86_400),
                             config: config)
        return Snapshot(updatedAt: now,
                        limits: inputs.limits.filter { !$0.isUninformative },
                        today: today, week: week,
                        // Services come from status pages over the network, which is exactly
                        // what this path does not do
                        services: nil,
                        claudeLimitsAsOf: inputs.claudeLimitsAsOf)
    }

    static func gather(config: Config, warehouse: Warehouse?, home: URL?, now: Date) -> Inputs {
        var entries: [Entry] = []
        var limits: [LimitWindow] = []

        if let warehouse, config.recordHistory {
            if config.wants(UsageStore.provider) {
                UsageStore().ingest(into: warehouse, now: now)
            }
            if config.wants(CodexStore.provider) {
                limits += CodexStore().ingest(into: warehouse, now: now).limits
            }
            if config.wants(OllamaStore.provider) {
                OllamaStore().ingest(into: warehouse, now: now)
            }
            warehouse.rollupPending(config: config)
            entries = warehouse.entries(since: now.addingTimeInterval(-7 * 86_400))
        } else {
            // Keeping no history means there is no store to ask, so the whole window is
            // parsed. That is the cost of the setting, not a fallback.
            if config.wants(UsageStore.provider) {
                entries += UsageStore().scan(lookbackDays: 7)
            }
            if config.wants(CodexStore.provider) {
                let snapshot = CodexStore().scan(lookbackDays: 7)
                entries += snapshot.entries
                limits += snapshot.limits
            }
            if config.wants(OllamaStore.provider) {
                entries += OllamaStore().scan(lookbackDays: 7)
            }
        }

        // Claude's windows, as Claude Code itself last reported them. Only while the reading
        // is fresh: a week window stays formally valid for days while its percentage drifts.
        var claudeAsOf: Date?
        if config.wants(UsageStore.provider),
           let feed = StatuslineFeed.read(path: StatuslineFeed.defaultPath(home: home), now: now),
           feed.isFresh(now: now) {
            limits += feed.windows
            claudeAsOf = feed.updatedAt
        }
        return Inputs(entries: entries, limits: limits, claudeLimitsAsOf: claudeAsOf)
    }

    private static func startOfDay(_ date: Date) -> Date {
        Calendar.current.startOfDay(for: date)
    }
}
