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
bars for weights held locally (Ollama), and the Redline mark itself for "all providers". Each
sits in a tile tinted with the provider colour already used in the menu and charts, so colour
and glyph reinforce each other.

The Redline mark stays in every widget header at reduced opacity, as the signature rather than
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
