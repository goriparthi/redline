// Provider-agnostic usage records and aggregation.
import Foundation

public struct Entry {
    public let provider: String
    public let key: String?
    public let ts: Date
    public let model: String
    public let input: Int
    public let output: Int
    public let cacheRead: Int
    public let cache5m: Int
    public let cache1h: Int

    public init(provider: String, key: String?, ts: Date, model: String, input: Int,
                output: Int, cacheRead: Int, cache5m: Int, cache1h: Int) {
        self.provider = provider
        self.key = key
        self.ts = ts
        self.model = model
        self.input = input
        self.output = output
        self.cacheRead = cacheRead
        self.cache5m = cache5m
        self.cache1h = cache1h
    }
}

public struct ModelUsage: Equatable {
    public var io = 0
    public var cost = 0.0
    // False when no pricing key matched, so cost is deliberately absent rather than zero
    public var priced = true
}

public struct ProviderUsage: Equatable {
    public var io = 0
    public var cost = 0.0
    public var cacheRead = 0
    public var cacheWrite = 0
    public var models: [String: ModelUsage] = [:]

    // Largest first, with a name tiebreak so the order does not jitter between refreshes
    public var rankedModels: [(model: String, usage: ModelUsage)] {
        models.map { (model: $0.key, usage: $0.value) }
            .sorted { $0.usage.io == $1.usage.io ? $0.model < $1.model
                                                : $0.usage.io > $1.usage.io }
    }
}

public struct Agg {
    public var io = 0
    public var cacheRead = 0
    public var cacheWrite = 0
    public var cost = 0.0
    // Models nest under the provider that produced them, so a Codex model can never be
    // listed among Claude's
    public var providers: [String: ProviderUsage] = [:]
    // Set when a model had no pricing entry, so the UI can mark the cost as partial
    public var hasUnpriced = false

    public init() {}

    public var rankedProviders: [(provider: String, usage: ProviderUsage)] {
        providers.map { (provider: $0.key, usage: $0.value) }
            .sorted { $0.usage.io == $1.usage.io ? $0.provider < $1.provider
                                                : $0.usage.io > $1.usage.io }
    }

    // Share of this period's in+out tokens, for the inline bars
    public func share(ofIO io: Int) -> Double {
        self.io > 0 ? Double(io) / Double(self.io) : 0
    }
}

public func aggregate(_ entries: [Entry], since: Date, config: Config) -> Agg {
    var a = Agg()
    for e in entries where e.ts >= since {
        var cost = 0.0
        if let p = config.price(for: e.model) {
            // Cache writes bill at 1.25x (5m) and 2x (1h) of the input rate
            var total: Double = Double(e.input) * p.input
            total += Double(e.output) * p.output
            total += Double(e.cacheRead) * p.cacheRead
            total += Double(e.cache5m) * p.input * 1.25
            total += Double(e.cache1h) * p.input * 2.0
            cost = total / 1_000_000
        } else if e.input + e.output > 0 {
            a.hasUnpriced = true
        }
        let io = e.input + e.output
        a.io += io
        a.cacheRead += e.cacheRead
        a.cacheWrite += e.cache5m + e.cache1h
        a.cost += cost
        var provider = a.providers[e.provider] ?? ProviderUsage()
        provider.io += io
        provider.cost += cost
        provider.cacheRead += e.cacheRead
        provider.cacheWrite += e.cache5m + e.cache1h
        var model = provider.models[e.model] ?? ModelUsage()
        model.io += io
        model.cost += cost
        model.priced = config.price(for: e.model) != nil
        provider.models[e.model] = model
        a.providers[e.provider] = provider
    }
    return a
}

public func fmtTokens(_ n: Int) -> String {
    let d = Double(n)
    switch d {
    case 1_000_000_000...: return String(format: "%.1fB", d / 1_000_000_000)
    case 1_000_000...:     return String(format: "%.1fM", d / 1_000_000)
    case 1_000...:         return String(format: "%.1fK", d / 1_000)
    default:               return "\(n)"
    }
}

// One shared formatter: fmtCost runs in row loops, and NumberFormatter is not cheap to make
private let costFormatter: NumberFormatter = {
    let f = NumberFormatter()
    f.numberStyle = .decimal
    f.minimumFractionDigits = 2
    f.maximumFractionDigits = 2
    f.groupingSeparator = ","
    f.usesGroupingSeparator = true
    return f
}()

/// "$24,320.91", grouped: past four digits an ungrouped dollar figure misreads by 10x at a
/// glance, which is exactly the glance a menu bar app is for.
public func fmtCost(_ c: Double) -> String {
    "$" + (costFormatter.string(from: NSNumber(value: c)) ?? String(format: "%.2f", c))
}
