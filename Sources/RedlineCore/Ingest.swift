// Reading every enabled provider into the warehouse, in one place.
// The CLI's `ingest` and the watch loop both call this, so a background run and a manual one
// can never disagree about what "ingest" means.
import Foundation

public enum Ingest {
    public struct Outcome {
        /// Entries added by this pass. Zero is the normal steady state, not a failure.
        public let added: Int
        public let byProvider: [String: Int]
        public let total: Int
        /// Limit windows this pass learned about, already recorded.
        public let limits: [LimitWindow]

        public init(added: Int, byProvider: [String: Int], total: Int,
                    limits: [LimitWindow] = []) {
            self.added = added
            self.byProvider = byProvider
            self.total = total
            self.limits = limits
        }
    }

    /// nil when history is switched off, which is a choice rather than an error.
    public static func run(config: Config, warehouse: Warehouse? = nil,
                           now: Date = Date()) -> Outcome? {
        guard config.recordHistory else { return nil }
        let warehouse = warehouse ?? Warehouse()
        var counts: [String: Int] = [:]
        if config.wants(UsageStore.provider) {
            counts[UsageStore.provider] = UsageStore().ingest(into: warehouse, now: now)
        }
        var limits: [LimitWindow] = []
        if config.wants(CodexStore.provider) {
            // CodexStore reports rows read rather than rows added, so the store is asked
            let before = warehouse.entryCount
            limits = CodexStore().ingest(into: warehouse, now: now).limits
            counts[CodexStore.provider] = warehouse.entryCount - before
        }
        if config.wants(OllamaStore.provider) {
            counts[OllamaStore.provider] = OllamaStore().ingest(into: warehouse, now: now)
        }
        warehouse.rollupPending(config: config)
        // Recorded here rather than by the caller, because an incremental read reports a
        // window once and then never again while it is still perfectly true. Whoever asks
        // next reads it back out of the store.
        let keep = limits.filter { !$0.isUninformative }
        if !keep.isEmpty { warehouse.recordLimits(keep, at: now) }
        return Outcome(added: counts.values.reduce(0, +), byProvider: counts,
                       total: warehouse.entryCount, limits: keep)
    }
}
