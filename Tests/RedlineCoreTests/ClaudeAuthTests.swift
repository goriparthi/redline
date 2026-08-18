import XCTest
@testable import RedlineCore

final class StatuslineFeedTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_755_400_000)

    private func parse(_ json: String) -> StatuslineSnapshot? {
        StatuslineFeed.parse(data: Data(json.utf8), now: now)
    }

    /// Byte-for-byte what scripts/claude-statusline.sh emits, so a change to either side
    /// breaks here rather than silently in the menu.
    func testParsesTheFeederOutput() throws {
        let snap = try XCTUnwrap(parse("""
        {"updated_at":"2026-08-17T19:49:14Z",
         "five_hour":{"used_percentage":75,"resets_at":1755450000},
         "seven_day":{"used_percentage":41.5,"resets_at":1755900000},
         "model_scoped":[{"display_name":"Fable","utilization":12,
                          "resets_at":"2026-08-24T00:00:00Z"}]}
        """))
        XCTAssertEqual(snap.windows.count, 3)
        XCTAssertEqual(snap.windows[0].key, "five_hour")
        XCTAssertEqual(snap.windows[0].utilization, 75)
        XCTAssertEqual(snap.windows[0].provider, "Claude")
        XCTAssertEqual(snap.windows[1].key, "seven_day")
        XCTAssertEqual(snap.windows[1].utilization, 41.5)
        XCTAssertEqual(snap.windows[2].key, "seven_day_fable")
        XCTAssertNotNil(snap.updatedAt)
    }

    /// The raw statusline payload, so a feeder that files the whole block still parses
    func testAcceptsTheRawRateLimitsWrapper() throws {
        let snap = try XCTUnwrap(parse("""
        {"session_id":"x","rate_limits":{"five_hour":{"used_percentage":10,
         "resets_at":1755450000}}}
        """))
        XCTAssertEqual(snap.windows.map(\.key), ["five_hour"])
        XCTAssertEqual(snap.windows[0].utilization, 10)
    }

    /// A window that has already rolled over reports a percentage that no longer exists.
    /// Dropping it is what lets a quiet feed fall through to the token path on its own.
    func testDropsWindowsWhoseResetHasPassed() throws {
        let snap = try XCTUnwrap(parse("""
        {"five_hour":{"used_percentage":99,"resets_at":1755000000},
         "seven_day":{"used_percentage":20,"resets_at":1755900000}}
        """))
        XCTAssertEqual(snap.windows.map(\.key), ["seven_day"])
    }

    func testAcceptsEitherPercentageSpelling() throws {
        let snap = try XCTUnwrap(parse(
            #"{"five_hour":{"utilization":33,"resets_at":1755450000}}"#))
        XCTAssertEqual(snap.windows.first?.utilization, 33)
    }

    func testResetsAtAcceptsSecondsMillisecondsAndISO() {
        XCTAssertEqual(StatuslineFeed.date(1_755_450_000),
                       Date(timeIntervalSince1970: 1_755_450_000))
        XCTAssertEqual(StatuslineFeed.date(1_755_450_000_000),
                       Date(timeIntervalSince1970: 1_755_450_000))
        XCTAssertNotNil(StatuslineFeed.date("2026-08-24T00:00:00Z"))
        XCTAssertNotNil(StatuslineFeed.date("2026-08-24T00:00:00.500Z"))
        XCTAssertNil(StatuslineFeed.date(nil))
        XCTAssertNil(StatuslineFeed.date(0))
    }

    func testMalformedInputIsNotAnError() {
        XCTAssertNil(StatuslineFeed.parse(data: Data("not json".utf8), now: now))
        XCTAssertTrue(parse("{}")?.isEmpty ?? false)
    }

    func testScopedKeySortsBesideTheOtherWeeklyWindows() {
        XCTAssertEqual(StatuslineFeed.scopedKey(for: "Fable"), "seven_day_fable")
        XCTAssertEqual(StatuslineFeed.scopedKey(for: "Claude Opus"), "seven_day_claude_opus")
        XCTAssertEqual(StatuslineFeed.scopedKey(for: "!!"), "seven_day_scoped")
        // isRecognized keeps them out of the unrecognized bucket the UI hides
        XCTAssertTrue(LimitWindow(provider: "Claude", key: "seven_day_fable",
                                  utilization: 1, resetsAt: nil).isRecognized)
    }
}

final class CredentialScanExtractionTests: XCTestCase {
    private let blob: [String: Any] = [
        "claudeAiOauth": [
            "accessToken": "sk-ant-oat-live",
            "refreshToken": "sk-ant-ort-refresh",
            "expiresAt": 1_755_450_000_000,
        ],
    ]

    func testExtractsTheWholeCredentialFromANestedBlob() throws {
        let c = try XCTUnwrap(CredentialScan.credential(in: blob))
        XCTAssertEqual(c.accessToken, "sk-ant-oat-live")
        XCTAssertEqual(c.refreshToken, "sk-ant-ort-refresh")
        XCTAssertEqual(c.expiresAt, Date(timeIntervalSince1970: 1_755_450_000))
        XCTAssertTrue(c.canRefresh)
    }

    /// The whole point of the rewrite: an expired credential must still come back, because
    /// "signed in, needs renewing" is not "signed out".
    func testExpiredCredentialIsStillReturned() throws {
        let c = try XCTUnwrap(CredentialScan.credential(in: blob))
        XCTAssertFalse(c.isFresh(now: Date(timeIntervalSince1970: 1_755_460_000)))
        XCTAssertTrue(c.isFresh(now: Date(timeIntervalSince1970: 1_755_400_000)))
    }

    /// The old entry point keeps filtering on freshness, so existing callers are unchanged
    func testAccessTokenHelperStillFiltersExpired() {
        XCTAssertNil(CredentialScan.accessToken(
            in: blob, now: Date(timeIntervalSince1970: 1_755_460_000)))
        XCTAssertEqual(CredentialScan.accessToken(
            in: blob, now: Date(timeIntervalSince1970: 1_755_400_000)), "sk-ant-oat-live")
    }

    func testMissingExpiryIsTreatedAsNonExpiring() throws {
        let c = try XCTUnwrap(CredentialScan.credential(in: ["accessToken": "t"]))
        XCTAssertTrue(c.isFresh(now: .distantFuture))
        XCTAssertFalse(c.canRefresh)
    }

    func testEmptyOrAbsentTokenYieldsNothing() {
        XCTAssertNil(CredentialScan.credential(in: ["accessToken": ""]))
        XCTAssertNil(CredentialScan.credential(in: ["mcpOAuth": ["serverName": "x"]]))
    }
}

final class SecurityCLIOutputTests: XCTestCase {
    /// `security -w` hex-dumps any payload it cannot return as a clean C-string, and Claude
    /// Code's blob line-wraps, which triggers exactly that.
    func testDecodesAHexDump() {
        let json = #"{"accessToken":"x"}"#
        let hex = json.utf8.map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(SecurityCLIOutput.decode(hex), json)
        XCTAssertEqual(SecurityCLIOutput.decode(hex + "\n"), json)
    }

    func testLeavesPlainJSONAlone() {
        let json = #"{"accessToken":"x"}"#
        XCTAssertEqual(SecurityCLIOutput.decode(json), json)
    }

    /// An odd length or a non-hex character means it was never a dump
    func testAmbiguousInputIsNotDecoded() {
        XCTAssertEqual(SecurityCLIOutput.decode("abc"), "abc")
        XCTAssertEqual(SecurityCLIOutput.decode("zz"), "zz")
        XCTAssertEqual(SecurityCLIOutput.decode(""), "")
    }
}

final class ClaudeAuthPolicyTests: XCTestCase {
    func testGrantErrorsAreTerminalAndTheRestAreNot() {
        XCTAssertEqual(ClaudeAuthPolicy.disposition(
            status: 400, body: Data(#"{"error":"invalid_grant"}"#.utf8)), .terminal)
        XCTAssertEqual(ClaudeAuthPolicy.disposition(
            status: 401, body: Data(#"{"error":"invalid_client"}"#.utf8)), .terminal)
        XCTAssertEqual(ClaudeAuthPolicy.disposition(status: 400), .terminal)
        // A bare 401 is usually a stale token rather than a dead grant, so it stays retryable
        XCTAssertEqual(ClaudeAuthPolicy.disposition(status: 401), .transient)
        XCTAssertEqual(ClaudeAuthPolicy.disposition(status: 429), .transient)
        XCTAssertEqual(ClaudeAuthPolicy.disposition(status: 503), .transient)
    }

    func testRetryAfterAcceptsSecondsAndHTTPDates() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        XCTAssertEqual(ClaudeAuthPolicy.retryAfter("120", now: now),
                       now.addingTimeInterval(120))
        XCTAssertNotNil(ClaudeAuthPolicy.retryAfter("Wed, 21 Oct 2026 07:28:00 GMT", now: now))
        XCTAssertNil(ClaudeAuthPolicy.retryAfter(nil, now: now))
        XCTAssertNil(ClaudeAuthPolicy.retryAfter("", now: now))
        XCTAssertNil(ClaudeAuthPolicy.retryAfter("soon", now: now))
    }

    func testBackoffGrowsThenCaps() {
        XCTAssertEqual(ClaudeAuthPolicy.backoff(consecutiveFailures: 0), 0)
        XCTAssertEqual(ClaudeAuthPolicy.backoff(consecutiveFailures: 1), 300)
        XCTAssertEqual(ClaudeAuthPolicy.backoff(consecutiveFailures: 2), 600)
        XCTAssertEqual(ClaudeAuthPolicy.backoff(consecutiveFailures: 9), 1800)
    }
}

final class CredentialOutcomeTests: XCTestCase {
    /// The bug this whole change exists to fix: only a signed-out CLI is durable news, and
    /// treating a locked Keychain the same way is what forced a manual Reconnect daily.
    func testOnlyNotFoundIsTerminal() {
        XCTAssertTrue(CredentialOutcome.notFound.isTerminal)
        XCTAssertFalse(CredentialOutcome.accessDenied.isTerminal)
        XCTAssertFalse(CredentialOutcome.unreadable.isTerminal)
        XCTAssertFalse(CredentialOutcome.found(
            BorrowedCredential(accessToken: "t", refreshToken: nil,
                               expiresAt: nil)).isTerminal)
    }
}

/// The chain-quoting round trip lives in the app target, so this pins the pure half of it:
/// what `shellQuote` writes must be exactly what the unwire step can read back.
final class ShellQuoteRoundTripTests: XCTestCase {
    private func quote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private func unchain(_ command: String) -> String? {
        let marker = "REDLINE_STATUSLINE_CHAIN='"
        guard let start = command.range(of: marker) else { return nil }
        var rest = command[start.upperBound...]
        var out = ""
        while let q = rest.firstIndex(of: "'") {
            out += rest[rest.startIndex..<q]
            let after = rest.index(after: q)
            if rest[after...].hasPrefix("\\''") {
                out += "'"
                rest = rest[rest.index(after, offsetBy: 3)...]
                continue
            }
            return out.isEmpty ? nil : out
        }
        return nil
    }

    private func roundTrip(_ original: String) -> String? {
        unchain("REDLINE_STATUSLINE_CHAIN=\(quote(original)) '/path/claude-statusline.sh'")
    }

    func testOrdinaryCommandSurvives() {
        XCTAssertEqual(roundTrip("bash ~/.claude/statusline-command.sh"),
                       "bash ~/.claude/statusline-command.sh")
    }

    /// A statusline with quotes in it is the case that would silently truncate
    func testQuotesAndPipesSurvive() {
        let original = #"jq -r '.model.display_name' | sed "s/x/y/""#
        XCTAssertEqual(roundTrip(original), original)
    }

    func testNoChainYieldsNothingToRestore() {
        XCTAssertNil(unchain("'/path/claude-statusline.sh'"))
    }
}
