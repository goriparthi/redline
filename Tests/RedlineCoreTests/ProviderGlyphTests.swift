// The provider marks exist twice by necessity: the asset catalog is what the Xcode build
// compiles, and the embedded vector is what the hand-assembled bundle uses. These tests are
// what stops the two from drifting.
import XCTest
@testable import RedlineCore

final class ProviderGlyphTests: XCTestCase {
    /// Resources/ProviderIcons.xcassets, found from this file rather than from a cwd, so the
    /// test passes wherever the suite is run from.
    private var catalog: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // RedlineCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // repo root
            .appendingPathComponent("Resources/ProviderIcons.xcassets")
    }

    private func catalogSVG(_ mark: ProviderMark) throws -> String {
        let dir = catalog.appendingPathComponent("\(mark.assetName).imageset")
        let files = try FileManager.default.contentsOfDirectory(atPath: dir.path)
        guard let svg = files.first(where: { $0.hasSuffix(".svg") }) else {
            throw XCTSkip("no svg in \(dir.lastPathComponent)")
        }
        return try String(contentsOf: dir.appendingPathComponent(svg), encoding: .utf8)
            .trimmingCharacters(in: .newlines)
    }

    func testEmbeddedVectorMatchesTheAssetCatalog() throws {
        for mark in ProviderMark.allCases {
            let onDisk = try catalogSVG(mark)
            XCTAssertEqual(mark.svg, onDisk,
                           "\(mark.assetName): the embedded vector and the asset catalog have "
                           + "drifted; regenerate ProviderGlyph.swift from the catalog")
        }
    }

    func testAssetCatalogMarksEveryImageAsATemplateVector() throws {
        for mark in ProviderMark.allCases {
            let contents = catalog
                .appendingPathComponent("\(mark.assetName).imageset/Contents.json")
            let json = try JSONSerialization.jsonObject(
                with: Data(contentsOf: contents)) as? [String: Any]
            let properties = json?["properties"] as? [String: Any]
            XCTAssertEqual(properties?["template-rendering-intent"] as? String, "template",
                           "\(mark.assetName) must render as a template")
            XCTAssertEqual(properties?["preserves-vector-representation"] as? Bool, true,
                           "\(mark.assetName) must keep its vector representation")
        }
    }

    func testEveryMarkHasASpokenLabel() {
        for mark in ProviderMark.allCases {
            XCTAssertFalse(mark.accessibilityLabel.isEmpty)
            // The label has to name the provider, or a screen reader announces a shape
            XCTAssertTrue(mark.accessibilityLabel.contains(mark.assetName),
                          "\(mark.assetName) label does not name the provider")
        }
    }
}

final class ProviderIdentityTests: XCTestCase {
    func testEachProviderMapsToItsOwnMarkAndAccent() {
        XCTAssertEqual(ProviderIdentity.of("Claude")?.mark, .anthropic)
        XCTAssertEqual(ProviderIdentity.of("Codex")?.mark, .codex)
        XCTAssertEqual(ProviderIdentity.of("Ollama")?.mark, .ollama)
    }

    func testMatchingIsCaseInsensitive() {
        XCTAssertEqual(ProviderIdentity.of("ollama")?.mark, .ollama)
        XCTAssertEqual(ProviderIdentity.of("CLAUDE")?.mark, .anthropic)
        XCTAssertEqual(ProviderIdentity.of("codex")?.mark, .codex)
    }

    func testTheProviderLevelMarkIsAnthropicsAndTheProductNameIsKept() {
        // Claude is a track in RedLine's data; the provider-level mark is Anthropic's, and
        // the Claude sparkle is reserved for naming the product itself.
        XCTAssertEqual(ProviderIdentity.of("Claude")?.name, "Claude")
        XCTAssertEqual(ProviderIdentity.of("Anthropic")?.name, "Anthropic")
        XCTAssertEqual(ProviderIdentity.of("Anthropic")?.mark,
                       ProviderIdentity.of("Claude")?.mark)
    }

    func testNoProviderAndUnknownProviderHaveNoIdentity() {
        XCTAssertNil(ProviderIdentity.of(nil))
        XCTAssertNil(ProviderIdentity.of("gemini"))
    }

    func testMarksAreDistinctPerTrack() {
        let marks = ["Claude", "Codex", "Ollama"].compactMap { ProviderIdentity.of($0)?.mark }
        XCTAssertEqual(Set(marks).count, 3, "each track must be visually distinguishable")
    }

    func testOnlyOllamaCountsAsLocalCompute() {
        XCTAssertEqual(ProviderIdentity.of("Ollama")?.isLocal, true)
        XCTAssertEqual(ProviderIdentity.of("Claude")?.isLocal, false)
        XCTAssertEqual(ProviderIdentity.of("Codex")?.isLocal, false)
    }
}

final class RLStatusTests: XCTestCase {
    func testUtilizationCrossesThresholdsInOrder() {
        XCTAssertEqual(RLStatus.forUtilization(10).kind, .healthy)
        XCTAssertEqual(RLStatus.forUtilization(60).kind, .approaching)
        XCTAssertEqual(RLStatus.forUtilization(85).kind, .atLimit)
        XCTAssertEqual(RLStatus.forUtilization(100).kind, .atLimit)
    }

    func testStaleOutranksTheReadingItself() {
        // A stale reading must never be drawn as a live status, however healthy it looks
        XCTAssertEqual(RLStatus.forUtilization(10, stale: true).kind, .stale)
        XCTAssertEqual(RLStatus.forUtilization(99, stale: true).kind, .stale)
    }

    func testEveryStateSaysSomething() {
        let kinds: [RLStatus.Kind] = [.healthy, .approaching, .atLimit, .offline, .unknown,
                                      .stale]
        for kind in kinds {
            XCTAssertFalse(RLStatus(kind).phrase.isEmpty)
        }
    }

    func testToneMapsOntoTheSameVocabulary() {
        XCTAssertEqual(RLStatus.forTone(.healthy).kind, .healthy)
        XCTAssertEqual(RLStatus.forTone(.warning).kind, .approaching)
        XCTAssertEqual(RLStatus.forTone(.critical).kind, .atLimit)
        XCTAssertEqual(RLStatus.forTone(.unknown).kind, .unknown)
    }
}
