import XCTest
@testable import RedlineCore

final class OllamaLocalityTests: XCTestCase {
    func testCloudModelsAreDetectedByTag() {
        XCTAssertTrue(OllamaLocality.isCloud("deepseek-v4-flash:cloud"))
        XCTAssertTrue(OllamaLocality.isCloud("gpt-oss:120b-cloud"))
        XCTAssertFalse(OllamaLocality.isCloud("qwen3-coder:30b"))
        XCTAssertFalse(OllamaLocality.isCloud("cloudqwen:7b"))
    }

    func testCloudModelsGetTheGlyphAndLocalOnesDoNot() {
        XCTAssertEqual(OllamaLocality.marked("x:cloud"), "☁ x:cloud")
        XCTAssertEqual(OllamaLocality.marked("qwen3-coder:30b"), "qwen3-coder:30b")
    }
}

final class ServiceStatusTests: XCTestCase {
    func testStatuspageParse() {
        let json = #"{"page":{"name":"Claude"},"status":{"indicator":"minor","description":"Degraded"}}"#
        let r = ServiceStatus.parse(json.data(using: .utf8)!)
        XCTAssertEqual(r?.indicator, "minor")
        XCTAssertEqual(r?.description, "Degraded")
        XCTAssertEqual(r?.isOperational, false)
        XCTAssertEqual(r?.phrase, "minor incident reported")
    }

    func testGarbageParsesToNil() {
        XCTAssertNil(ServiceStatus.parse(Data("not json".utf8)))
        XCTAssertNil(ServiceStatus.parse(Data("{\"status\":{}}".utf8)))
    }
}

final class OllamaParseTests: XCTestCase {
    func testParsesTagsWithDetails() {
        let json: [String: Any] = ["models": [
            ["name": "qwen3-coder:30b", "size": 18_500_000_000,
             "modified_at": "2026-08-01T10:00:00Z",
             "details": ["parameter_size": "30.5B", "quantization_level": "Q4_K_M"]],
            ["name": "llama3:8b", "size": 4_700_000_000],
        ]]
        let models = OllamaParse.models(json)
        XCTAssertEqual(models.map(\.name), ["llama3:8b", "qwen3-coder:30b"], "sorted by name")
        let qwen = models[1]
        XCTAssertEqual(qwen.parameterSize, "30.5B")
        XCTAssertEqual(qwen.quantization, "Q4_K_M")
        XCTAssertEqual(qwen.family, "qwen3-coder")
        XCTAssertEqual(qwen.tag, "30b")
        XCTAssertNotNil(qwen.modifiedAt)
    }

    func testAcceptsModelKeyInsteadOfName() {
        let models = OllamaParse.models(["models": [["model": "mistral:7b", "size": 4_000]]])
        XCTAssertEqual(models.first?.name, "mistral:7b")
    }

    func testSkipsEntriesWithoutAName() {
        XCTAssertTrue(OllamaParse.models(["models": [["size": 1]]]).isEmpty)
    }

    func testMissingModelsKeyIsEmptyNotACrash() {
        XCTAssertTrue(OllamaParse.models([:]).isEmpty)
        XCTAssertTrue(OllamaParse.running([:]).isEmpty)
    }

    func testParsesRunningWithVramShare() {
        let json: [String: Any] = ["models": [
            ["name": "qwen3-coder:30b", "size": 20_000, "size_vram": 15_000,
             "expires_at": "2026-08-12T22:00:00Z"],
        ]]
        let running = OllamaParse.running(json)
        XCTAssertEqual(running.count, 1)
        XCTAssertEqual(running[0].vramShare, 0.75, accuracy: 0.001)
        XCTAssertNotNil(running[0].expiresAt)
    }

    func testFullyOnCPUReportsZeroVramShare() {
        let running = OllamaParse.running(["models": [
            ["name": "m", "size": 100, "size_vram": 0]]])
        XCTAssertEqual(running[0].vramShare, 0)
    }

    func testZeroSizeDoesNotDivideByZero() {
        let running = OllamaParse.running(["models": [
            ["name": "m", "size": 0, "size_vram": 0]]])
        XCTAssertEqual(running[0].vramShare, 0)
    }

    func testByteFormatting() {
        XCTAssertEqual(fmtBytes(18_500_000_000), "18.5 GB")
        XCTAssertEqual(fmtBytes(4_700_000), "5 MB")
        XCTAssertEqual(fmtBytes(512), "512 B")
    }
}

final class LimitIdentityTests: XCTestCase {
    func testIdIncludesProviderSoProvidersDoNotCollide() {
        let claude = LimitWindow(provider: "Claude", key: "seven_day",
                                 utilization: 4, resetsAt: nil)
        let codex = LimitWindow(provider: "Codex", key: "seven_day",
                                utilization: 17, resetsAt: nil)
        XCTAssertNotEqual(claude.id, codex.id,
                          "a shared key must not produce a shared identity")
        XCTAssertEqual(claude.id, "Claude|seven_day")
    }
}
