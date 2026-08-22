// Every figure a person reads goes through these two functions, and the Windows shell has its
// own copy of them. One table, asserted from both languages, so neither can drift alone.
//
// This exists because it already happened: Linux Foundation ignored maximumFractionDigits and
// rendered a cost as $12.274. A third decimal on a dollar figure is the kind of wrong number
// nobody double-checks.
import XCTest
@testable import RedlineCore

final class FormattingContractTests: XCTestCase {
    private struct Table: Decodable {
        struct IntCase: Decodable { let value: Int; let expect: String }
        struct DoubleCase: Decodable { let value: Double; let expect: String }
        let tokens: [IntCase]
        let cost: [DoubleCase]
    }

    private func table() throws -> Table {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Fixtures/formatting.json")
        return try JSONDecoder().decode(Table.self, from: Data(contentsOf: url))
    }

    func testTokenFormattingMatchesTheSharedTable() throws {
        for row in try table().tokens {
            XCTAssertEqual(fmtTokens(row.value), row.expect,
                           "fmtTokens(\(row.value))")
        }
    }

    func testCostFormattingMatchesTheSharedTable() throws {
        for row in try table().cost {
            XCTAssertEqual(fmtCost(row.value), row.expect,
                           "fmtCost(\(row.value))")
        }
    }

    /// The table is only worth anything if it is actually exercising the interesting cases.
    func testTheTableCoversGroupingAndRounding() throws {
        let costs = try table().cost.map(\.expect)
        XCTAssertTrue(costs.contains { $0.contains(",") }, "no grouped figure in the table")
        XCTAssertTrue(costs.contains { $0.hasPrefix("-") }, "no negative figure in the table")
        let tokens = try table().tokens.map(\.expect)
        for suffix in ["K", "M", "B"] {
            XCTAssertTrue(tokens.contains { $0.hasSuffix(suffix) }, "no \(suffix) case")
        }
    }
}
