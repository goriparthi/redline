# Desktop widget: design and the blocker

Status: **built.** Option C was chosen and implemented: an Xcode project builds the app and
widget, while `RedlineCore` stays a SwiftPM package so `make test` is unchanged. The one
remaining step for *distribution* is a Developer ID cert; it runs locally today.

Signing without an Apple ID: Xcode refuses to ad-hoc sign a target carrying the App Group
entitlement ("requires a provisioning profile"). The workaround in
`scripts/build-widget.sh` is to build with `CODE_SIGNING_ALLOWED=NO` and then ad-hoc sign
inside-out ourselves (framework, then appex, then app), passing each target's entitlements
file. macOS honours App Group entitlements on ad-hoc signed code locally, so the sandboxed
widget can read the snapshot. This is enough to run on this machine and is not
distributable; the script switches to real signing automatically once a Developer ID cert
exists.

Install with `WIDGET=1 make install`. Verify registration with
`pluginkit -mv -p com.apple.widgetkit-extension | grep redline`.

Implementation notes worth keeping:

- `RedlineCore` is a **framework target** in the Xcode project, not a local package
  reference. A local package at the repo root makes Xcode fail with "Missing package
  product". The framework compiles the same files, so drift is impossible.
- An embedded framework needs an explicit `Info.plist` (`Resources/RedlineCore-Info.plist`);
  a synthesized one produced a bundle-identifier validation failure.
- The widget's `Info.plist` **must** set `CFBundleIdentifier` to
  `$(PRODUCT_BUNDLE_IDENTIFIER)`. Xcode does not inject it into an explicit plist, and
  without it embedded-binary validation fails with a misleading "not prefixed with the
  parent app's bundle identifier".
- Deployment target is macOS 14 everywhere; desktop widget placement requires Sonoma.

## The blocker

A macOS desktop widget is a **WidgetKit app extension**, and SwiftPM cannot build app
extensions. There is no `.extensionTarget` in Package.swift. Adding a widget therefore means
one of:

- **A) Commit an `.xcodeproj`.** Build moves from `swift build` to `xcodebuild`. Simple,
  works, but an Xcode project is a large generated file that is painful to review and merge.
- **B) Generate the project** with XcodeGen or Tuist from a small YAML/Swift manifest. Keeps
  the repo diffable and the manifest reviewable; adds a dependency and a generate step.
- **C) Keep SwiftPM for the core, add a thin Xcode project that consumes it as a local
  package.** `RedlineCore` stays exactly as it is; the Xcode project builds the app and the
  widget extension against it. Preserves `make test` on the core.

**C is recommended.** The core library is already the natural seam, and it keeps the fast
`swift test` loop for all the parsing logic, which is where the tests actually are.

Whichever is chosen, `make build`/`make bundle` must be rewritten to drive `xcodebuild`, and
CI needs the same change.

## Signing implication

Widgets need an **App Group** to share data with the host app, and App Group entitlements
require a provisioning profile from a real team. Ad-hoc signing is no longer sufficient.

An `Apple Development` or `Apple Distribution` identity can carry an App Group for local use,
so this is workable, but the "just clone and `make install`" story gets harder for anyone else,
since they would need their own team id and App Group. Set `REDLINE_TEAM_ID` and regenerate the
project to use your own.

Suggested App Group: `group.com.goriparthi.redline`.

## Data flow

A widget cannot poll. It renders from a timeline and gets refreshed by the system on its own
schedule, with a budget. So the widget must not read transcripts or call any API.

Instead:

1. The menu bar app, at the end of every `refreshLocal()`/`refreshLimits()` cycle, writes a
   small snapshot JSON into the shared App Group container.
2. The widget's `TimelineProvider` reads that snapshot and renders. It never parses a
   transcript; scanning thousands of jsonl lines inside a widget process would blow the
   time and memory budget.
3. The app calls `WidgetCenter.shared.reloadTimelines(ofKind:)` after writing, so the widget
   updates promptly rather than waiting for the system.

Snapshot shape, deliberately tiny and already derivable from `Agg` and `[LimitWindow]`:

```json
{
  "updatedAt": "2026-08-12T16:04:00Z",
  "limits": [
    {"provider": "Claude", "key": "five_hour", "utilization": 12.0,
     "resetsAt": "2026-08-12T21:00:00Z"},
    {"provider": "Codex", "key": "seven_day", "utilization": 1.0,
     "resetsAt": "2026-08-14T09:30:00Z"}
  ],
  "today": {"io": 6044, "cost": 1.23, "hasUnpriced": true},
  "week":  {"io": 5728245, "cost": 6734.06, "hasUnpriced": true}
}
```

Put the snapshot writer/reader in `RedlineCore` so both processes share one definition and
it stays unit tested. Keep it a separate type from `Agg`; a wire format that doubles as an
internal model ossifies fast.

## Widget families

- **systemSmall**: one gauge. Worst session utilization, colored by the existing thresholds,
  with the reset time underneath.
- **systemMedium**: session and week gauges side by side, plus a per-provider row.
- **systemLarge**: adds today/week token and cost totals.

Reuse the threshold colors from `limitColor()`. Move that logic into `RedlineCore` when the
widget lands so the app and widget cannot drift apart on what counts as red.

## Honest caveats

- **Refresh is not real-time.** WidgetKit decides when to reload; a desktop widget showing a
  5-hour window will usually be fine, but it can lag by minutes. Do not present it as live.
  Render `updatedAt` so a stale reading is visibly stale, matching the "never show a number
  that reads as more current than it is" rule in EXTENDING.md.
- **If the menu bar app is not running, the snapshot goes stale** and the widget has nothing
  fresh to show. It should say so rather than showing an old percentage as if current.
