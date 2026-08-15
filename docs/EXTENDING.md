# Extending RedLine

Written for whoever picks this up next, including an agent with no memory of the original
session. Read [ARCHITECTURE.md](ARCHITECTURE.md) first; it explains *why* the seams are
where they are.

## Ground rules

0. **The brand kit wins.** `brand/BRAND.md` and `brand/tokens/redline-tokens.json` define
   colour, type, motion timings, and voice. Never hardcode a colour that duplicates a token;
   add it to `Sources/RedlineCore/Brand.swift` so the app and widget share one definition.
   The voice rules are binding too: no racing metaphors, no fear language, and no claim of
   exactness where a provider only exposes an estimate. Preferred vocabulary is Usage,
   Remaining, Resets, Pace, Projected, Limit.
1. **Testable logic goes in `RedlineCore`.** No AppKit, no network, no Keychain in that
   target. It exists so the parsers can be tested; keep it that way.
2. **Every scanner takes injectable inputs.** A `root:`/`log:` parameter and a `now:` on
   `scan`. Without both, the tests cannot use fixtures and become time-dependent.
3. **Never guess a number the user will read as fact.** Unpriced models are excluded from
   cost and flagged, not priced at a nearby tier. Unavailable limits show `sign in`, not a
   token count. This is the single most important convention in the codebase; two real bugs
   came from violating it.
4. **Undocumented formats get defensive parsing plus a test.** Scan for the shape you need
   rather than hardcoding a key path a vendor update can move.
5. **Failures must be visible.** Ignoring a status code caused the original bug
   (`SecItemAdd` returning `errSecDuplicateItem` was discarded, so sign-in silently never
   persisted).

## Adding a usage provider

Say you want to add a provider called `Foo`.

**1. Write the scanner** in `Sources/RedlineCore/FooUsage.swift`:

```swift
public final class FooStore {
    public static let provider = "Foo"
    private let root: URL

    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".foo/sessions")
    }

    public func scan(lookbackDays: Int, now: Date = Date()) -> [Entry] {
        // Return [] when the path is missing. A absent tool is not an error.
    }
}
```

Emit `Entry(provider: FooStore.provider, ...)`. Set `cacheRead` for tokens billed at a cache
rate and leave `cache5m`/`cache1h` at 0 unless the provider distinguishes cache-write TTLs.

**2. If it reports rate limits**, add a parser to `Limits.swift` returning
`[LimitWindow]` with `provider: "Foo"`. Map its windows onto the existing keys
(`five_hour`, `seven_day`) so the display path and sort order work unchanged. If a window
has no equivalent, `LimitParser.key(forWindowMinutes:)` will name it rather than drop it.

**3. Register it** in `Config.knownProviders` and add it to the `providers` default. The
menu picker and `wants()` pick it up automatically.

**4. Wire it into `refreshLocal()`** in `AppDelegate.swift`:

```swift
if cfg.wants(FooStore.provider) {
    entries += self.fooStore.scan(lookbackDays: 7)
}
```

**5. Add pricing** to `Config.defaultPricing` only if you know the real published rates.
Otherwise leave it unpriced; the UI already handles that honestly.

**6. Test it** in `Tests/RedlineCoreTests/StoreTests.swift`. Cover, at minimum: a normal
parse, the lookback boundary, a missing directory, and malformed lines. If it reports
limits, also cover a window whose reset time has passed. Copy the shape of
`CodexStoreTests`.

## Adding a config key

`Config.swift` only. Add the property with a default, parse it in `apply(_:to:)` **with
validation**, add it to `writeDefault`, document it in the README table, and test that an
out-of-range value falls back rather than being accepted. See
`testRejectsOutOfRangeAndUnknownValues`.

## Changing the menu bar title

`AppDelegate.updateTitle()` and `limitsTitle()`. Keep the rule in Ground Rule 3: if there is
no real limit data, do not put a number in that slot.

## Releasing

```sh
make test
# bump CFBundleShortVersionString and CFBundleVersion in Resources/Info.plist
make dmg
make release          # needs gh; creates the tag and uploads the DMG
```

Then update `version` and `sha256` in `Casks/redline.rb` with the values
`scripts/release.sh` prints.

To notarize, you need a **Developer ID Application** certificate. `Apple Development` and
`Apple Distribution` certs do not work for distribution outside the App Store. Once you have
one, set either `NOTARY_PROFILE` or `NOTARY_APPLE_ID` + `NOTARY_TEAM_ID` + `NOTARY_PASSWORD`
and `make dmg` notarizes and staples automatically.

## Known constraints

- **SwiftPM cannot build app extensions.** The widget therefore builds through
  `Redline.xcodeproj` (`make widget`), generated from `project.yml` by XcodeGen. The project
  is committed so a clone builds without XcodeGen installed. **Adding a source file means
  regenerating**: the project holds an explicit file list, so a new file is invisible to
  `xcodebuild` even though `swift build` globs the directory and succeeds. `make widget`
  regenerates every time to avoid exactly that trap; commit the result. `RedlineCore` is a framework target there
  compiled from the same files as the SwiftPM target, so the two cannot drift.
- **`swift build` and `make test` never compile the widget.** `RedlineWidget` exists only in
  the Xcode project, so a syntax error there passes both and only shows up in `make widget`.
  Run it before assuming widget changes compile.
- **A signed widget needs a provisioning profile** for the App Group, which means an Apple
  ID signed into Xcode. `SIGN=no make widget` builds unsigned for a smoke test.
- **Tests need Xcode, not just the Command Line Tools**, for XCTest. `scripts/test.sh`
  handles the selection.
- **Ad-hoc rebuilding invalidates the Keychain ACL** for both this app's item and the borrowed
  CLI item, because ad-hoc signing changes the code identity every build. A Developer ID cert
  gives a stable designated requirement and the grant then survives rebuilds; that much is
  verified. It does **not** stop the re-prompting, because Claude Code rewriting its own
  credential on refresh is what actually clears the grant, roughly daily.
- **The Claude rate-limit endpoint is undocumented** and may disappear. Token and cost
  totals do not depend on it. It requires the OAuth scope `user:profile`, which only Claude
  Code's `/login` token carries, and Anthropic does not register OAuth clients for
  third-party apps, so borrowing that token is the only mechanism available. Anthropic's
  policy since February 2026 says subscription credentials are for Claude Code and
  Claude.ai; see the README before building on this.
