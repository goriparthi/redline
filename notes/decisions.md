# Decision log

Why things are the way they are. Append, don't rewrite.

## 2026-08-12: origin, and the bug that started it

The predecessor (`claude-usage-monitor`) showed `loading…` plus a raw
`HTTP 400 invalid_grant / Refresh token expired` in its dropdown, and `0 $0.00` in the menu
bar. Diagnosis:

- The app's OAuth refresh token, written Aug 10 20:26 UTC, was dead. Its Keychain `mdat`
  never advanced, proving it had **never** refreshed successfully even once.
- The app used Claude Code's own public `client_id`. The CLI's credential rotated constantly
  (last Aug 12 07:49 UTC) while the app's sat untouched. Working hypothesis: one
  refresh-token family per user+client, so the CLI's rotation invalidated the app's grant.
  Unproven, but it fits every observation.

Three distinct defects, all fixed:

1. **No `invalid_grant` handling.** The dead token stayed in the Keychain, `isSignedIn` stayed
   true, and the app retried it forever. Now cleared, so the UI offers Sign In.
2. **`save()` discarded `SecItemAdd`'s status.** A rebuild changes the ad-hoc code identity, so
   `clear()` fails on the old ACL and `SecItemAdd` returns `errSecDuplicateItem`; sign-in
   silently never persisted. Now falls back to `SecItemUpdate` and reports failure.
3. **The menu bar silently fell back to tokens+cost.** Because usage happened to be zero, it
   read as a broken app rather than a signed-out one. This is the origin of the "never show a
   number that will be misread as something else" rule.

`0 $0.00` was, incidentally, correct: there genuinely was no usage before 09:32 that day. The
displayed number was accurate and still deeply misleading. Worth remembering.

## 2026-08-12: prefer the CLI's token

Chosen over keeping our own grant, because our own grant demonstrably dies about daily while
the CLI's stays fresh. Falls back to our own sign-in when the CLI token is missing or gets a
401/403. Probe runs off the main thread because a Keychain read can block on consent, and
caches misses so a denial is not re-asked every poll.

Known cost: a background `LSUIElement` agent is denied silently rather than prompted, so the
user must grant access once in Keychain Access.app. Not discoverable on its own; documented in
the README.

## 2026-08-12: no default `client_id`

The predecessor hardcoded Anthropic's. For anything published, that is not ours to ship: it is
someone else's OAuth client against an undocumented endpoint, revocable at any time. Now empty
by default with Sign In disabled until set. Token and cost totals need no credentials at all,
so the app is useful without it.

## 2026-08-12: Mac App Store ruled out for now

- MAS mandates App Sandbox; a sandboxed app cannot read another app's login-Keychain item, so
  CLI-token borrowing is impossible there.
- Sandbox also blocks reading `~/.claude` and `~/.codex` without user-selected folder grants.
- Shipping against an undocumented endpoint using a borrowed client id is a ToS risk.

Decision: personal GitHub project, Developer ID + notarization when a cert exists. Revisit MAS
if Anthropic publishes a real usage API.

## 2026-08-12: unpriced models are not priced

The predecessor fell back to Sonnet pricing for unknown models. Codex and Ollama models have
no entry, so that would have invented spend figures. Now `price(for:)` returns nil, tokens are
still counted, cost excludes them, and the total is marked `+`.

## 2026-08-12: menu bar shows the worst provider

With three providers, showing every percentage is too wide. The number that matters is
whichever provider will stop you first, so the title shows the max and the dropdown breaks it
down.

## 2026-08-12: Ollama needs a wrapper

Ollama persists no token accounting. `~/.ollama/history` is REPL history;
`/opt/homebrew/var/log/ollama.log` was stale by six months and the running server held no log
file open. Options were a logging wrapper, a launchd-managed server with `OLLAMA_DEBUG`, or
live status only. Wrapper chosen: simplest, no change to how the machine runs Ollama, and it
captures the delegated calls that are the point of the question. Consequence: manual
`ollama run` is invisible, by design.

## 2026-08-12: brand kit adopted

Applied the supplied kit: app icon (`Resources/Redline.icns`, generated from
`brand/AppIcon.appiconset` with `iconutil`), the menu bar template mark replacing the
placeholder asterisk, brand threshold colours via `Brand.swift`, and a rebuilt site.

Two things worth recording:

- The kit forbids **racing metaphors**, which the first site was built on (a tachometer
  hero with a needle sweep). Rebuilt around the actual mark instead: three streams meeting
  one limit line, expressed as rails running toward a red threshold.
- The supplied wordmark SVG is **not outlined** despite the kit README saying to use the
  outlined file; it is `<text>` with `font-family: Inter`, so it renders in a fallback face
  wherever Inter is absent. The site inlines the true vector symbol and matches the
  wordmark's exact type specs in HTML instead. Worth asking for an outlined wordmark.

Also noted: the brand's preferred copy leads with **remaining** ("64% remaining") while the
app displays utilisation ("13%"). The site now shows both so the bar and the number cannot
contradict each other. Aligning the app's menu bar on the same vocabulary is still open.

## 2026-08-12: security sweep before sharing

Four findings, all fixed:

1. **`useCLIToken` defaulted to true**, so the app read the Claude CLI's Keychain item by
   default. That item is another application's credential; silently reaching for it is the
   single most alarming thing a shared app could do. Now opt-in. Note this changes behaviour
   for existing installs whose config omits the key, so the local config here was updated to
   set it explicitly.
2. **The Ollama wrapper leaked prompt content.** `curl -d "$REQ"` put the whole request,
   prompt included, into argv, and the prompt also travelled through an environment
   variable. Both are readable by any process running as the same user. Now piped on stdin
   via a private temp file.
3. **The snapshot was 0644.** It holds usage and cost figures. Now 0600, as is the Ollama log.
4. **`notarytool --password` exposes the app-specific password in argv.** Kept for
   convenience but now warns loudly and points at `NOTARY_PROFILE`.

Verified clean: no hardcoded secrets, no token logging, no telemetry, egress limited to three
Anthropic hosts, Keychain uses `kSecAttrAccessibleWhenUnlocked`, PKCE with `state`
verification, and the OAuth loopback listener binds `127.0.0.1` only.

Added SECURITY.md documenting every path read and written.

## 2026-08-12: why the widget showed "Usage unavailable"

The widget was sandboxed and ad-hoc signed. `containerURL(forSecurityApplicationGroupIdentifier:)`
returns nil in that situation, because App Group containers resolve only for code signed with
a real Team ID (`codesign` showed `TeamIdentifier=not set`). The read fell through to a path
that did not exist, so the view rendered its empty state.

Two dead ends worth recording:

- Removing the sandbox let it read the file, but **macOS then refuses to register the widget
  at all** and it disappeared from `pluginkit`. Widget extensions must be sandboxed.
- Writing only to the App Group container is useless without a Team ID.

The fix: keep the sandbox, declare the App Group for the future, and add
`com.apple.security.temporary-exception.files.home-relative-path.read-only` scoped to
`/.local/share/redline/`. The app now writes the snapshot to both the user path and the group
container, and the widget reads whichever it can reach. `scripts/build-widget.sh` fails the
build if either the sandbox or the exception goes missing on an ad-hoc build.

## 2026-08-12: dashboard window

Charts come from `Trends.swift` in the core: entries already carry timestamps, so daily and
hourly series are derived from transcripts with nothing extra recorded. Buckets are pre-filled
with zeros so a quiet day renders at the baseline instead of vanishing from the axis. A test
asserts the chart cost and the menu bar cost agree, since two aggregation paths over the same
data is exactly how they would silently drift.

Traps hit while building it:

- **XcodeGen writes an explicit file list.** A new source file was invisible to `xcodebuild`
  while `swift build` (which globs) succeeded, so `Dashboard.swift` failed to resolve only in
  the Xcode build. `scripts/build-widget.sh` now regenerates every time rather than comparing
  timestamps.
- **Launch Services registers widgets from DerivedData.** A second `RedlineWidget` was running
  out of `dist/xcode/...` alongside the installed one. The build now deletes the DerivedData
  copy after staging.
- `DashboardModel` cannot be `@MainActor`: `AppDelegate` drives it from non-isolated methods,
  so published changes are dispatched to main explicitly instead.
- Double-clicking the app used to do nothing visible, which is genuinely confusing for a menu
  bar accessory. `applicationShouldHandleReopen` now opens the dashboard.

## 2026-08-12: grouped menu with inline bars

The dropdown listed every model in one flat list, so `codex` appeared among the Claude
models. `Agg` now nests models inside `ProviderUsage`, which makes that mistake impossible to
express rather than merely avoided. Tests assert a Codex model cannot be reached under Claude.

Bars are text, since an NSMenu renders strings. `Sparkline` uses eighth-width block glyphs for
sub-character resolution, and two rules matter:

- A share above zero always renders at least a sliver, and a sub-1% share prints `<1%` rather
  than `0%`, which would read as unused.
- Only the name column indents. Bars, percentages, cost and tokens sit at a fixed offset and
  every bar is the same width, so a provider row and a model row are visually comparable.
  Nesting by shifting the whole row made equal shares look unequal.

Unpriced models show `—` rather than `$0.00` in this view too.

## 2026-08-12: dashboard branding, provider focus, Ollama controls

Two bugs the first dashboard screenshot exposed:

- `nimbus quill` appeared in the Limits panel. The filter had been applied to the menu and the
  snapshot but not to the window, which took `allLimits` straight from the delegate. Filtering
  now lives on `DashboardData`, so all three read the same rule.
- **"Claude · Week" rendered twice.** `ForEach(id: \.key)` collided because Claude and Codex
  both use `seven_day`. `LimitWindow` is now `Identifiable` with `provider|key`, so a shared
  key cannot produce a shared identity.

Also: a `.segmented` picker takes the system accent, which showed up as macOS blue against a
Carbon panel. The range control is now plain buttons carrying the brand tint.

Ollama gained real controls. Parsing for `/api/tags` and `/api/ps` sits in the core with tests;
`OllamaService` in the app does the HTTP against `OLLAMA_HOST`, defaulting to loopback, and a
malformed override falls back to loopback rather than silently pointing elsewhere. Start loads
a model with an empty prompt; Stop sends `keep_alive: 0` to unload. **No delete:** removing
downloaded weights is destructive and was not asked for.

Process note: launching a second app instance to screenshot the window re-triggers the Keychain
prompt every time, because each rebuild changes the ad-hoc code identity. It also captures
whatever else is on screen. Not worth it; ask instead.

## 2026-08-12: information rows must not look like controls

Turning off `autoenablesItems` to fix contrast had a side effect: every row became enabled, so
non-actionable text highlighted blue on hover. "Reading limits with the Claude CLI's token"
looked like a button.

Menu rows now come in two kinds:

- **Controls** stay ordinary `NSMenuItem`s with targets, and highlight as expected.
- **Information** (limits, usage tables, status lines) uses `item.view = MenuRowView`. An item
  carrying a view draws only what the view draws, so it keeps full contrast with no selection
  highlight. `hitTest` returns nil so the row cannot even take the mouse.

The trade-off is that view-backed rows do not inherit AppKit's text inset, so `MenuRowView`
hardcodes 22pt to line up with the real controls above and below.

## 2026-08-12: configurable widget

The widget is now an `AppIntentConfiguration` with a `TrackChoice` parameter, so one widget
definition covers every provider and can be added several times with different settings. That
is simpler than one widget kind per provider and gives the Edit Widget picker for free.
Verified by the presence of `Metadata.appintents` in the built appex; without it the picker
never appears.

**Ollama state travels in the snapshot.** The obvious approach was letting the widget call
`127.0.0.1:11434` directly, but that needs `com.apple.security.network.client` on the
extension. Since the app already polls, it now writes server status, loaded models with their
GPU share, and download totals into the snapshot, and the widget only renders. The extension
stays completely offline.

`Snapshot` gained `todayByProvider`, `weekByProvider` and `ollama`, all optional so a file
written by an older build still decodes instead of leaving the widget blank. There is a test
for exactly that.

Brand pieces (`RedlineMark`, palette, `LimitRail`) moved into `RedlineCore/BrandUI.swift`. The
app and widget are separate binaries and were drawing their own copies; one shared definition
is the only way they cannot drift. This is the single place the core reaches past parsing.

Bug worth remembering: a scripted edit to `publishSnapshot` silently did not match, so the
Ollama section was collected but never written. The build passed and the widget just showed
nothing. Assert on the match when patching by script.

## 2026-08-12: widget type scale

The first widget was sized like a dense panel: 8 to 11pt text with a 13 to 15pt number. A
widget is read at a glance, so it now has one `Metrics` scale that grows with the family, with
the percentage as the hero at 34pt small, 40pt medium, 44pt large, and supporting text at 11
to 14pt. Rails thickened to 8 to 11pt and the mark to 16 to 20pt.

The small size cannot hold two full blocks at that hero size, so it shows one hero block for
the session and the week as a single line. Everything that can overflow carries
`minimumScaleFactor`, so a long model name or a four-figure cost shrinks rather than truncates.

Note for future work: `swift build` does not compile the widget at all, since the target lives
only in the Xcode project. A widget-only change can look like it built when nothing happened.

## 2026-08-12: widget legibility and two repeat bugs

Type scale raised again after seeing it in place: hero percentages are now 48pt small, 54
medium, 60 large, with supporting text 12 to 17. Medium drops the second totals row, because
two blocks plus two rows no longer fit once the number is that size.

The **duplicate row bug came back in a second place.** `LimitWindow` was made `Identifiable`
on `provider|key`, but `Snapshot.Window` is a separate type and the widget's `ForEach` passed
`id: \.key` explicitly, so Codex's `seven_day` collided with Claude's and Claude rendered
twice. `Snapshot.Window` is now `Identifiable` the same way, with a test asserting ids are
unique across a typical snapshot. Lesson: fixing identity on one type does not fix the wire
type that mirrors it.

**Placeholder rendered as grey skeleton bars.** `placeholder(in:)` returned a nil snapshot,
which WidgetKit draws redacted, so a freshly added widget looked broken rather than loading.
It now reads the real snapshot.

The large size was packing everything into the top third. Spacers now sit between sections
rather than all at the end, and each window in the breakdown gets its own rail.

## 2026-08-12: widgets must not clip, so stop guessing heights

Raising the type scale overflowed every family: the large widget clipped its own header at the
top and its last row at the bottom, medium lost the totals row, small lost the week line.
Picking smaller numbers would only have moved the threshold, since the content varies with how
many windows a provider reports.

Both bodies now wrap their layout in `ViewThatFits(in: .vertical)` over a `Detail` ladder,
full then trimmed then lean, and the system renders the first that fits:

- **trimmed** drops the reset lines, the trailing header note, the second totals row, and the
  large breakdown.
- **lean** drops the totals block and the loaded-model list entirely.

The scale was also moderated to 42/46/52pt heroes, but the important part is that the layout is
now measured rather than assumed, so a future type change cannot reintroduce clipping. The
Ollama body caps its model list per family and shows "+N more loaded" rather than overflowing.

## 2026-08-13: track badges, and why they are not vendor logos

Asked for Anthropic, OpenAI and Ollama logos in the widgets. Declined and built original track
badges instead, for two reasons worth keeping:

- All three restrict third-party use of their marks, and this project is headed for public
  distribution. Nominative use is arguable, but their guidelines are not.
- Drawing them from memory produces approximations, and those same guidelines require a mark be
  reproduced unaltered. A wrong logo is worse than no logo.

`TrackGlyph` maps each track to generic iconography that belongs to nobody: a ring with a
satellite dot for a hosted endpoint (Claude), opposed chevrons for source code (Codex), stacked
bars for weights held locally (Ollama), and the RedLine mark itself for "all providers". Each
sits in a tile tinted with the provider colour already used in the menu and charts, so colour
and glyph reinforce each other.

The RedLine mark stays in every widget header at reduced opacity, as the signature rather than
the subject: the track is what the reader needs first.

Badges are shared from `BrandUI`, so the widget, the dashboard limit rows, the model mix, the
focus picker and the Ollama panel cannot drift apart.

If vendor marks are ever wanted, get written permission first and add the real SVGs to
`brand/`; do not trace them.

## Open

- **Widget signing**: the widget builds, but the App Group entitlement needs a
  provisioning profile, so open Redline.xcodeproj in Xcode once with an Apple ID
  signed in. See widget-design.md.
- **Developer ID cert**: needed for notarization and for a stable Keychain ACL identity.
- Nothing outstanding on identity: this repo commits as
  `Prashanth Goriparthi <github@goriparthi.com>` via repo-local git config, matching the
  other personal projects. History was rewritten once to remove a work address.

## 2026-08-15: the Keychain re-grant, and why the percentages stay

Woke to `Connect` in the menu bar. Diagnosed with timestamps rather than guesswork: the
`Claude Code-credentials` item's `mdat` was 02:29 that morning while `cdat` was still March,
so the item was **rewritten in place, not recreated**, and RedLine's read broke at exactly
that moment. The grant had been working hours earlier against the same bundle, and replacing
RedLine's bundle repeatedly did not break it, which rules out RedLine's own code identity and
leaves the CLI's rewrite as the cause.

**Claude Code rewriting its credential on token refresh clears the item's access list.** One
consent prompt per refresh is therefore the floor for the borrowing approach; no amount of
caching, read-timing, or token reuse on this side lowers it. Documented in ARCHITECTURE.md
and EXTENDING.md, which previously claimed a Developer ID cert "stops the re-prompting".

Three escapes were investigated and all are closed:

- **Clone the access token into RedLine's own item.** Useless: the copy expires at the same
  moment the CLI refreshes, because expiry is why the CLI refreshes.
- **Clone the refresh token and refresh independently.** Mechanically possible, rejected. If
  Anthropic rotates refresh tokens (RedLine's own code already handles rotation, implying they
  do), whichever side refreshes last owns the session and the other is logged out: a menu bar
  app that randomly ends your Claude Code session. It also means presenting Claude Code's
  client id as if RedLine were Claude Code. (2026-08-17: this exact failure shipped anyway as
  the mint rung and was observed live; see that day's entries. The rejection was right.)
- **`claude setup-token`, the documented one-year token.** Tested against the endpoint:
  `403 · OAuth token does not meet scope requirement user:profile`. It is scoped to model
  requests. Only the `/login` token carries `user:profile`.

**Anthropic's position is published.** Since February 2026 the authentication policy states
that Free/Pro/Max OAuth credentials are for Claude Code and Claude.ai, that products should
use a Console API key, and that misrepresenting identity to Anthropic's servers is prohibited;
server-side enforcement has rejected subscription credentials outside Claude Code since
January 2026. Anthropic does not register OAuth clients for third-party apps, so RedLine's
browser sign-in route (complete, PKCE, requesting `user:profile`) cannot be shipped enabled.
It stays in the tree because it is the correct design the day a client id exists.

Decision: **keep the percentages and disclose plainly.** They remain opt-in, off by default,
read-only, and consume no quota. The README's "nobody here has a ruling either way" was
retired as no longer true. The alternative considered and rejected was dropping the Claude
percentages entirely; everything else in the app is local files and would have been unaffected.

Also rejected: caching the last reading to survive the gap. The dead menu bar is the honest
signal, and `Connect` is preferable to a number whose provenance the user has to reason about.

## 2026-08-15: update checks on by default, once a day

Previously opt-in and twice daily, on the reasoning that any unasked network call breaks the
"no network unless you ask" promise. Reversed: an app that installs updates in place has to
learn that updates exist, and a security fix nobody hears about is not a fix. One call a day
to the GitHub releases API, silent unless there is news, one click to switch off.

The cost is honesty upkeep, and it was paid: README, SECURITY.md, and the site each claimed
there was no update check. SECURITY.md now lists `api.github.com` alongside the three
Anthropic hosts and marks it as the only request RedLine makes without being asked. Existing
installs keep whatever their `config.json` already says, so the new default reaches only
fresh ones.

## 2026-08-17: minting is deleted, not gated

The mint rung, exchanging the CLI's refresh token when delegation failed, did exactly what its
own comment warned: Anthropic rotates refresh tokens on use, so each mint left Claude Code
holding a consumed token and forced a fresh `/login`. Observed live: the CLI's login expired a
day after signing in, with nothing connecting the two events. Nobody enabling "read my CLI
token" consented to "and periodically sign my CLI out".

A survey of five peer tools found none that spend a lineage they do not own. CodexBar encodes
it as a type: credentials carry an owner, and owner==claudeCLI is never refreshed, only
delegated. codeburn has no refresh path at all and re-reads the file on 401. Claude-Usage-
Tracker refreshes only when the token is provably idle and writes the rotated credential back
so the CLI keeps the live chain. Deleted rather than flagged off: a rung this destructive
should not exist behind any setting. Delegation (`claude auth status`) is now the ceiling, and
past it the answer is stale data honestly drawn, not a forked chain.

## 2026-08-17: the 2026-08-11 "one family per client" hypothesis has a counter-test

Defect 2 of that incident (save() silently dropping rotated tokens) fully explains the dead
grant without the hypothesis, and people run Claude Code on several machines with one account
without mutual logouts, which argues chains are per authorization. A browser sign-in (owner-
supplied client id, this machine only) went live tonight alongside heavy CLI use. If the
grant survives a few days of CLI rotation, the hypothesis is dead and the 08-12 "prefer the
CLI's token" reasoning with it. Its worst case is self-contained either way: only RedLine's
own grant can die, never the CLI's.

## 2026-08-17: a non-empty feed is not necessarily a current one

The sidecar won the source race whenever it was non-empty, and a reading hours old shadowed a
live sign-in by five points (feed said 29% while the account was at 34%). Windows already
carry reset-based expiry, but a week window stays "valid" for days. Now: the feed wins only
within freshFor (15 min) of its own stamp; past that a live fetch takes over, and the feed's
last unexpired reading is only a fallback, drawn stale. Staleness is a palette, not a
footnote: percentages and rails drain to steel, the age rides beside them in amber, in the
menu bar, dropdown, dashboard, and widget alike. The snapshot carries claudeLimitsAsOf so the
widget can tell a fresh snapshot with old limits from a fresh everything.

## 2026-08-17: the feed re-wires itself

A Claude Code session that predates the feed holds settings.json in memory and writes it back
without the statusLine chain; saving a model choice is enough. Observed live: the feed went
quiet mid-evening and nobody knew until the percentages misled. The wrapper script on disk is
the marker of intent (install writes it, uninstall removes it), so every poll now re-wires
settings.json when the script outlives the entry, chaining whatever command sits there at
repair time. Worst case is one poll of stale data instead of an unbounded silent gap.

## 2026-08-17: the prompting Keychain path goes last

Reads of Claude Code's credential ran file → API → security tool; the API is the only path
that shows a consent prompt, and Claude Code rewriting its item revoked the grant repeatedly,
three prompts in eight hours despite Always Allow. Every peer tool reads via the Apple-signed
security binary, which the item's apple-tool partition trusts, and prompts never. Order is
now file → security tool → API, so the prompt is reserved for the case where the quiet paths
failed on an item that exists. The README paragraph claiming the access list survives rewrites
was retired; observation beat the theory.

## 2026-08-18: betas have to end in a promotion

The beta channel shipped without any pressure to leave it, which is how a beta channel
becomes the only channel: every build goes out as a prerelease, stable users quietly stop
receiving anything, and the tested-then-promoted path stops being a path. `scripts/release.sh`
now refuses a fourth prerelease on one core version, or any prerelease more than 14 days after
that version's first, and the answer to either is the promotion. Both limits are needed: a
count alone lets a build sit on beta.2 forever, an age alone lets a whole run ship in an
afternoon. The mirror gate refuses a stable release for a version that never saw a beta, or
the cheapest way past the first gate would be to skip beta entirely. `GATE_ANYWAY=1` overrides
both, because a hotfix cannot wait on process.

A third nightly channel was considered and dropped. The cost is not the plumbing: the updater
requires notarization, so a nightly means a Developer ID cert and notarytool credentials living
in CI, which is a larger surface than the feature earns. Semver also sorts `nightly` above
`beta` on identical cores, so a nightly would outrank a later beta of the same version, and the
beta channel's "newest wins" rule would drag beta users onto nightlies.

Release notes lost their escape hatch in the same pass. `NOTES_ANYWAY=1` fell back to a
generated commit list, which is exactly what the written notes exist instead of;
`scripts/check-notes.sh` now enforces the shape (a summary line, titled sections, 40 words of
prose, no commit dump, no em dashes) and CI runs it on every push.

## 2026-08-18: a cached parse only answers for the window it was parsed with

Picking 30 days in the dashboard scanned 30 days and still drew 14. `UsageStore` caches parsed
entries per file on mtime plus size, and `parse` drops entries older than the cutoff, so every
transcript touched in the last two weeks answered the wider scan from a cache that had already
been truncated at 14 days. Only files nobody had touched recently came back with 30 days of
data, which is to say the quiet ones. The cutoff is now part of the cache key, reusable only
when it reaches at least as far back as the scan asks, and the window is enforced again on the
way out so a wider cache cannot inflate a narrower caller.

The dashboard hid how long that lasted. The 14 day scan still in flight published its results
and cleared the loading state, so the older data appeared as the answer to the newer click. A
generation counter now drops any scan the user has already moved past.

## 2026-08-18: history is written before it is needed

Claude Code prunes its own transcripts, so every chart in this app has a horizon that moves
forward without anyone noticing. The warehouse (`~/.local/share/redline/history`) records a
daily rollup as RedLine polls, and it went in before the features that will read it, because
a day nobody wrote down cannot be recovered afterwards.

Three choices worth keeping:

- **UTC days.** A record whose span depends on where it was written cannot be summed. The
  dashboard's own charts still bucket by local day, and the two are deliberately not blended
  yet: a boundary day would be double counted. The export says `day_basis: UTC`.
- **The fullest reading of a day wins.** Merge replaces a stored record only when the incoming
  one carries at least as many tokens. A plain overwrite would have let a pruned transcript
  erase a week at a time, which is the exact failure the file exists to prevent.
- **Limit readings are sampled, not polled into.** A sample is written when the percentage
  moves, the window rolls over, or fifteen minutes pass. Without that, a five minute poll
  writes a file full of identical rows and the burn rate gains nothing from any of them.

## 2026-08-18: two rates, and saying which one you got

Time to limit needs a rate, and there are two. Differencing stored readings inside one window
instance describes what is happening now; utilization over elapsed time describes the window
as a whole. The first is better and is not always available, so `Pace.Basis` carries which one
produced the answer and the UI puts it in the tooltip.

Guards that matter more than the arithmetic: no projection without a known window length, none
from readings less than ten minutes apart, and none differenced across a rollover. That last
one is why `LimitSample.sameWindowInstance` keys on the reset time; without it, a window that
had just reset would produce a large negative rate and a confident nonsense answer.

The pace marker on the rails came out of the same work and is the cheapest part of it: the
elapsed fraction of the window, drawn as a tick. Fill level with the tick is break-even. It
needs no history at all, which is why it appears even on the first reading.

## 2026-08-18: notifications are opt-in, and permission is asked late

macOS asks once per app, and an app that asks before it has ever had anything to say gets
refused permanently. The prompt is therefore raised when the setting is switched on, not at
launch. Alerts stay off by default for the same reason the percentages do: interrupting
someone is a decision that belongs to them.

The rules live in `Alerting`, away from the notification centre, so they can be tested. Two of
them are the whole point: once per threshold per window instance, and never from a stale
reading. A stale poll still records its utilization, or the next fresh one would read the gap
as a reset and announce a rollover that never happened.

## 2026-08-18: findings report what can be counted, and no more

The new panel is the first thing in RedLine that gives advice rather than a number, which is
exactly where a monitor starts inventing figures to look useful. The rule adopted instead: a
finding with nothing honest to attach carries no figure. An unused MCP server pays for its
tool schemas in every session that loads it, that cost is real, and it is not visible from
here, so it is described and left uncosted.

Where a figure exists it says how it was made. Characters are measured and divided by four to
estimate tokens; session counts are measured. A project's `CLAUDE.md` is priced against the
sessions that ran in that project rather than every session on the machine, which was the
first version and overstated it by the number of projects you work in.

The scan is separate from the usage scan because it reads more of each transcript, and runs at
most every six hours: configuration changes at the speed of someone editing a file.

## 2026-08-18: the sidecar is a contract, so publish it too

ClaudeHUD, claude-monitor and CodexBar independently arrived at the same file: current windows
as JSON, written by whoever has them. RedLine has read that shape since the usage feed landed.
Writing it costs almost nothing and turns a competitor into a consumer, so
`~/.local/share/redline/usage-snapshot.json` now carries the standard keys, with both the
`used_percentage` and `utilization` spellings because readers exist for each, and everything
of ours namespaced under `redline` where a foreign parser will ignore it.

Reading someone else's is the same trade in reverse, with one extra rule: it is used only
while fresh. A sidecar another tool stopped updating is exactly as misleading as one of ours
would be, and that is a lesson this project already paid for once with the feed.

The bundled CLI exists for the same reason. Its exit codes are the part to keep stable:
`0/10/11/20/30`, so a shell prompt or a CI step never has to parse prose.

## 2026-08-18: the cost chart drew a line that was never data

The estimated cost panel had a diagonal band running across it since the panel was built.
`AreaMark` was styled with a flat colour rather than `foregroundStyle(by:)`, so it carried no
series identity, and Charts closed one shape across every provider's points: the last Claude
day joined back to the first Codex day. The lines above it were correct the whole time because
they did group by series, which is why it read as a rendering quirk rather than a bug.

Lesson worth keeping: in Swift Charts, a mark's colour and a mark's series are the same
argument, and choosing a colour by hand quietly opts out of grouping. Anything drawn per
series has to say so.

## 2026-08-18: a rate needs a span proportional to its window

Time to limit shipped with one minimum span, ten minutes, for every window. Ten minutes of
readings describes a five hour window usefully and says nothing about a weekly one: the first
build reported "23h to limit" on a week that was 5% used with six days to run, because a busy
quarter of an hour had been extrapolated across seven days. The minimum now scales with the
window length, so a weekly rate needs hours of evidence before it will claim anything and the
session window still catches a burst.

The general rule this is an instance of: a projection's confidence has to come from the ratio
between the evidence and the horizon, never from the evidence alone.

## 2026-08-18: charts answer shape questions, readouts answer value questions

Added hover readouts to all three charts. A chart is good at "when was it busy" and bad at
"how much exactly", and every question anybody actually asked of these panels was the second
kind. The pointer snaps to the nearest bucket rather than reading the raw x position, which is
what makes the gap between two bars answerable, and the annotation is clamped inside the chart
so it cannot draw over the panel above it.

## 2026-08-18: a button whose only feedback is a clock looks broken

"Scan again" worked from the first build and was reported as not working. The scan finished
inside a second on a warm cache, and the only thing that changed was a timestamp rendered to
the minute. Feedback has to be visible on the timescale of the action, not the timescale of
the data: the button now becomes a spinner and the timestamp carries seconds.
