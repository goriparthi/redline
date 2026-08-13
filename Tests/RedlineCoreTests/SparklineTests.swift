import XCTest
@testable import RedlineCore

final class SparklineTests: XCTestCase {
    func testBarIsAlwaysExactlyTheRequestedWidth() {
        for share in [0.0, 0.001, 0.13, 0.5, 0.99, 1.0, 2.0, -1.0] {
            XCTAssertEqual(Sparkline.bar(share: share, width: 10).count, 10,
                           "width must be fixed or later columns misalign at share \(share)")
        }
    }

    func testFullAndEmpty() {
        XCTAssertEqual(Sparkline.bar(share: 1, width: 5), "█████")
        XCTAssertEqual(Sparkline.bar(share: 0, width: 5), "     ")
    }

    func testTinyShareStillShowsSomething() {
        let bar = Sparkline.bar(share: 0.004, width: 10)
        XCTAssertFalse(bar.trimmingCharacters(in: .whitespaces).isEmpty,
                       "a real but small share must not render as empty")
    }

    func testHalfUsesAFullHalfOfTheWidth() {
        XCTAssertTrue(Sparkline.bar(share: 0.5, width: 10).hasPrefix("█████"))
    }

    func testPercentPadsAndFlagsSubOnePercent() {
        XCTAssertEqual(Sparkline.percent(0.5), " 50%")
        XCTAssertEqual(Sparkline.percent(1.0), "100%")
        XCTAssertEqual(Sparkline.percent(0.004), " <1%",
                       "0% would read as unused when it is not")
        XCTAssertEqual(Sparkline.percent(0), "  0%")
    }

    func testPadTruncatesWithEllipsisAndFills() {
        XCTAssertEqual(Sparkline.pad("abc", to: 5), "abc  ")
        XCTAssertEqual(Sparkline.pad("abcdef", to: 4), "abc…")
        XCTAssertEqual(Sparkline.pad("42", to: 5, alignRight: true), "   42")
        XCTAssertEqual(Sparkline.pad("abc", to: 3), "abc")
    }

    func testShortModelDropsVendorPrefixOnly() {
        XCTAssertEqual(Sparkline.shortModel("claude-opus-5"), "opus-5")
        XCTAssertEqual(Sparkline.shortModel("gpt-5.3-codex"), "5.3-codex")
        XCTAssertEqual(Sparkline.shortModel("qwen3-coder:30b"), "qwen3-coder:30b")
    }
}

final class GroupedAggregationTests: XCTestCase {
    private let t = Date(timeIntervalSince1970: 1_700_000_000)

    private func entry(_ provider: String, _ model: String, io: Int) -> Entry {
        Entry(provider: provider, key: nil, ts: t, model: model, input: io,
              output: 0, cacheRead: 0, cache5m: 0, cache1h: 0)
    }

    private var cfg: Config = {
        var c = Config()
        c.pricing = ["sonnet": ModelPrice(input: 3, output: 15, cacheRead: 0.3)]
        return c
    }()

    func testModelsNestUnderTheirOwnProvider() {
        let a = aggregate([entry("Claude", "claude-sonnet-5", io: 100),
                           entry("Codex", "gpt-5.3-codex", io: 50)],
                          since: t, config: cfg)
        XCTAssertEqual(Set(a.providers["Claude"]?.models.keys ?? [:].keys), ["claude-sonnet-5"])
        XCTAssertEqual(Set(a.providers["Codex"]?.models.keys ?? [:].keys), ["gpt-5.3-codex"],
                       "a Codex model must never appear under Claude")
    }

    func testRankedProvidersLargestFirst() {
        let a = aggregate([entry("Claude", "claude-sonnet-5", io: 10),
                           entry("Codex", "gpt-5.3-codex", io: 900)],
                          since: t, config: cfg)
        XCTAssertEqual(a.rankedProviders.map(\.provider), ["Codex", "Claude"])
    }

    func testRankedModelsLargestFirst() {
        let a = aggregate([entry("Claude", "claude-sonnet-5", io: 10),
                           entry("Claude", "claude-opus-5", io: 900)],
                          since: t, config: cfg)
        XCTAssertEqual(a.providers["Claude"]?.rankedModels.map(\.model),
                       ["claude-opus-5", "claude-sonnet-5"])
    }

    func testUnpricedModelIsFlaggedPerModel() {
        let a = aggregate([entry("Codex", "gpt-5.3-codex", io: 50)], since: t, config: cfg)
        XCTAssertEqual(a.providers["Codex"]?.models["gpt-5.3-codex"]?.priced, false)
        XCTAssertTrue(a.hasUnpriced)
    }

    func testShareOfTotal() {
        let a = aggregate([entry("Claude", "claude-sonnet-5", io: 75),
                           entry("Codex", "gpt-5.3-codex", io: 25)],
                          since: t, config: cfg)
        XCTAssertEqual(a.share(ofIO: a.providers["Claude"]?.io ?? 0), 0.75, accuracy: 0.0001)
    }

    func testShareIsZeroWhenNothingRecorded() {
        XCTAssertEqual(Agg().share(ofIO: 0), 0, "must not divide by zero")
    }
}

final class ProviderCacheTests: XCTestCase {
    private let t = Date(timeIntervalSince1970: 1_700_000_000)

    func testCacheFiguresAreTrackedPerProvider() {
        let entries = [
            Entry(provider: "Claude", key: nil, ts: t, model: "claude-sonnet-5",
                  input: 10, output: 1, cacheRead: 500, cache5m: 20, cache1h: 5),
            Entry(provider: "Codex", key: nil, ts: t, model: "gpt-5.3-codex",
                  input: 5, output: 1, cacheRead: 7, cache5m: 0, cache1h: 0),
        ]
        let a = aggregate(entries, since: t, config: Config())
        XCTAssertEqual(a.providers["Claude"]?.cacheRead, 500)
        XCTAssertEqual(a.providers["Claude"]?.cacheWrite, 25)
        XCTAssertEqual(a.providers["Codex"]?.cacheRead, 7,
                       "a focused tile must not show another provider's cache")
        XCTAssertEqual(a.cacheRead, 507, "the global figure still sums every provider")
    }
}
