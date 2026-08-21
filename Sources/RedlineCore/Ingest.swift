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

        public init(added: Int, byProvider: [String: Int], total: Int) {
            self.added = added
            self.byProvider = byProvider
            self.total = total
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
        if config.wants(CodexStore.provider) {
            // CodexStore reports rows read rather than rows added, so the store is asked
            let before = warehouse.entryCount
            _ = CodexStore().ingest(into: warehouse, now: now)
            counts[CodexStore.provider] = warehouse.entryCount - before
        }
        if config.wants(OllamaStore.provider) {
            counts[OllamaStore.provider] = OllamaStore().ingest(into: warehouse, now: now)
        }
        warehouse.rollupPending(config: config)
        return Outcome(added: counts.values.reduce(0, +), byProvider: counts,
                       total: warehouse.entryCount)
    }
}
