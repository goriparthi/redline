// Rendering-side counterparts to ProviderGlyphTests. These need AppKit, so they live with
// RedlineUI while the vector-vs-catalog drift tests stay in the core suite.
import XCTest
@testable import RedlineUI
import RedlineCore

final class ProviderMarkImageTests: XCTestCase {
    func testEveryMarkLoadsAsATemplateImage() {
        for mark in ProviderMark.allCases {
            let image = ProviderMarkImage.image(for: mark)
            XCTAssertNotNil(image, "\(mark.assetName) did not load")
            XCTAssertTrue(image?.isTemplate ?? false,
                          "\(mark.assetName) must be a template so it follows the appearance")
            // A zero size would collapse the frame it is drawn in
            XCTAssertGreaterThan(image?.size.width ?? 0, 0)
            XCTAssertGreaterThan(image?.size.height ?? 0, 0)
        }
    }

    func testMarksAreSquareSoAFrameCannotStretchThem() {
        for mark in ProviderMark.allCases {
            guard let size = ProviderMarkImage.image(for: mark)?.size else {
                return XCTFail("\(mark.assetName) did not load")
            }
            XCTAssertEqual(size.width, size.height, accuracy: 0.01,
                           "\(mark.assetName) is not square; scaledToFit would letterbox it")
        }
    }
}

final class RLStatusPresentationTests: XCTestCase {
    func testEveryStateHasItsOwnShapeSoColourIsNeverTheOnlyCarrier() {
        let kinds: [RLStatus.Kind] = [.healthy, .approaching, .atLimit, .offline, .unknown,
                                      .stale]
        let symbols = kinds.map { RLStatus($0).symbol }
        XCTAssertEqual(Set(symbols).count, kinds.count,
                       "two states share a glyph, so they differ by colour alone")
    }
}
