// What a failed refresh of RedLine's own grant means.
//
// The bug these pin: only the literal string "invalid_grant" was treated as terminal, and
// Anthropic's token endpoint answers in its API envelope instead. A rejected refresh was
// retried on every poll forever, the app stayed "signed in", the percentages never came back,
// and nothing offered Sign In.
import XCTest
@testable import RedlineCore

final class GrantRefreshTests: XCTestCase {
    /// The body shape a user reported, from the endpoint's own error envelope. The
    /// correlation id is synthetic; only the shape matters here.
    private let reported = """
        {"type":"error","error":{"type":"invalid_request_error",\
        "message":"Invalid request format"},"request_id":"req_EXAMPLE0000000000000000"}
        """

    func testTheReportedRejectionIsTerminalRatherThanRetriedForever() {
        let outcome = ClaudeAuthPolicy.classifyRefresh(status: 400, body: reported)
        XCTAssertTrue(outcome.isTerminal,
                      "a 400 the endpoint will give again is not worth repeating; the grant "
                      + "has to be cleared so Sign In is offered")
        XCTAssertFalse(outcome.message.contains("{"),
                       "the message is for a person, so it carries no JSON")
    }

    func testAnOAuthExpiredGrantIsNamedAsExpired() {
        let outcome = ClaudeAuthPolicy.classifyRefresh(
            status: 400, body: #"{"error":"invalid_grant"}"#)
        XCTAssertTrue(outcome.isTerminal)
        XCTAssertEqual(outcome.message, "Sign-in expired; sign in again")
    }

    func testARejectedClientIdPointsAtTheClientId() {
        for body in [#"{"error":"invalid_client"}"#, #"{"error":"unauthorized_client"}"#] {
            let outcome = ClaudeAuthPolicy.classifyRefresh(status: 401, body: body)
            XCTAssertTrue(outcome.isTerminal)
            XCTAssertTrue(outcome.message.contains("clientId"), body)
        }
    }

    func testRateLimitingIsRetriedAndKeepsTheGrant() {
        let outcome = ClaudeAuthPolicy.classifyRefresh(status: 429, body: "slow down")
        XCTAssertFalse(outcome.isTerminal,
                       "a lifted rate limit makes the same request succeed, so the grant stays")
    }

    func testServerFaultsAreRetriedAndKeepTheGrant() {
        for status in [500, 502, 503] {
            XCTAssertFalse(ClaudeAuthPolicy.classifyRefresh(status: status, body: "").isTerminal,
                           "HTTP \(status)")
        }
    }

    func testATransportFailureIsRetriedAndKeepsTheGrant() {
        // Status 0 means the request never got an answer, so nothing was rejected
        XCTAssertFalse(ClaudeAuthPolicy.classifyRefresh(
            status: 0, body: "The Internet connection appears to be offline.").isTerminal)
    }

    func testASuccessWithNoTokenKeepsTheGrantBecauseTheGrantIsNotWhatFailed() {
        let outcome = ClaudeAuthPolicy.classifyRefresh(status: 200, body: #"{"ok":true}"#)
        XCTAssertFalse(outcome.isTerminal)
    }

    func testEveryOtherClientErrorIsTerminalAndNamesTheStatus() {
        for status in [400, 401, 403, 404, 422] {
            let outcome = ClaudeAuthPolicy.classifyRefresh(status: status, body: "nope")
            XCTAssertTrue(outcome.isTerminal, "HTTP \(status)")
            XCTAssertFalse(outcome.message.isEmpty)
        }
    }

    func testEveryOutcomeSaysSomethingAPersonCanAct(on: Void = ()) {
        let cases = [(0, ""), (429, ""), (500, ""), (200, "{}"), (400, reported),
                     (401, #"{"error":"invalid_client"}"#)]
        for (status, body) in cases {
            let message = ClaudeAuthPolicy.classifyRefresh(status: status, body: body).message
            XCTAssertFalse(message.isEmpty, "HTTP \(status)")
            // Nothing here should read as a stack trace or a payload
            XCTAssertFalse(message.contains("request_id"), "HTTP \(status)")
        }
    }
}

final class FormBodyTests: XCTestCase {
    func testKeysAreSortedSoTheSameBodyIsAlwaysTheSameBytes() {
        XCTAssertEqual(FormBody.encoded(["b": "2", "a": "1", "c": "3"]), "a=1&b=2&c=3")
    }

    /// The reason this is not left to the URL character sets: both characters are legal in a
    /// query string, so neither would be escaped, and a refresh token carrying either would be
    /// silently truncated or split by the server.
    func testPlusAndAmpersandAreEscapedBecauseTheyAreLegalInAQuery() {
        XCTAssertEqual(FormBody.encoded(["t": "a+b"]), "t=a%2Bb")
        XCTAssertEqual(FormBody.encoded(["t": "a&b=c"]), "t=a%26b%3Dc")
    }

    func testUnreservedCharactersSurviveUnchanged() {
        let token = "AbYz09-._~"
        XCTAssertEqual(FormBody.encoded(["t": token]), "t=\(token)")
    }

    func testSpacesAndSlashesAndColonsAreEscaped() {
        XCTAssertEqual(FormBody.encoded(["u": "http://x/y z"]),
                       "u=http%3A%2F%2Fx%2Fy%20z")
    }

    func testAnEmptyValueStillProducesItsKey() {
        XCTAssertEqual(FormBody.encoded(["a": ""]), "a=")
    }

    func testAnEmptyBodyIsAnEmptyString() {
        XCTAssertEqual(FormBody.encoded([:]), "")
    }

    /// A real PKCE verifier and a base64url token are exactly the shapes this has to carry
    /// without alteration.
    func testBase64URLPayloadsPassThroughUnaltered() {
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        XCTAssertEqual(FormBody.encoded(["code_verifier": verifier]),
                       "code_verifier=\(verifier)")
    }
}
