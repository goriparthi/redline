# Cross-platform state

Where the Windows and Linux ports actually are, and what is claimed but not yet proven.
The full plan lives in `~/i3logix/claude_notes/redline_2026-08-20_cross-platform-parity.md`.

## The rule

One engine, three shells. `RedlineCore` is Foundation only and builds everywhere.
`RedlineUI` and `Sources/redline` are AppKit and stay macOS. The parsers are never
reimplemented in another language, because every format they read is undocumented and three
parsers chasing a moving target means three different answers to the same question.

`Package.swift` enforces this: the macOS-only targets are added under `#if os(macOS)`, so a
Linux or Windows build cannot pull AppKit in by accident.

## Done

- `RedlineUI` split out of `RedlineCore`. The split is by symbol, not by file: `ProviderMark`,
  `ProviderIdentity` and `RLStatus` are data the CLI needs, so they stay in the core while
  their colours, SF Symbol names and views moved out.
- `AppPaths` replaces eleven hardcoded `.local/share/redline` literals. Honours XDG on Linux
  and LocalAppData on Windows, and an explicit home overrides both so the end to end suite
  sees one layout everywhere.
- SQLite vendored for non-Darwin as `Sources/CSQLite`. macOS still links the SDK's copy.
- `SnapshotStore`'s App Group, widget container and Application Support paths gated to macOS.
- `SingleInstance` gained a Windows named-mutex branch, and `ClaudeFleet` gained per-platform
  process probes.
- CI gained `core-linux` and `core-windows` jobs, both green.

## Verified

All three build and all three test suites pass on real CI.

| Platform | Toolchain | Tests | State |
|---|---|---|---|
| macOS | Xcode | 347 | `make ci` green, widget builds, bundle verifies |
| Linux | Swift 6.0, ubuntu | 343 | green |
| Windows | Swift 6.3.3, windows-latest | 336 | green |

The counts differ only by the tests that are gated to a platform. Every build compiles the
vendored SQLite through SwiftPM off macOS, so `import CSQLite` is proven on both.

Reproduce the Linux run with:

    docker run --rm -v "$PWD":/src -w /src swift:6.0-jammy \
      bash -c 'swift test --scratch-path /tmp/build'

## What the Linux build caught

Three real defects that reading the source had missed, which is the argument for running it
rather than reasoning about it.

- `ClaudeFleet` read the process table with BSD `sysctl`, `kinfo_proc` and `devname`. Now
  three implementations behind one `ProcessProbe`: sysctl on Darwin, `/proc/<pid>/stat` plus
  `btime` on Linux, `GetProcessTimes` on Windows. Windows has no controlling terminal to
  name, so `ttyPath` is nil there and the shell falls back to focusing the window.
- `ServiceStatus` uses `URLSession`, which lives in `FoundationNetworking` off Apple
  platforms. Worth noting that this makes the "no network in the core" line in
  `docs/ARCHITECTURE.md` not quite true; the core does fetch status pages.
- **`fmtCost` printed `$12.274` on Linux.** swift-corelibs-foundation's `NumberFormatter`
  ignored `maximumFractionDigits`. Rewritten to round with `String(format:)` and group by
  hand, which is deterministic everywhere and drops a locale dependency. This is the exact
  failure the one-engine rule exists to prevent, and it would have shipped.

Two `SnapshotTests` cases assert macOS sandbox behaviour and are now gated, with an off-macOS
counterpart asserting the user path is the only location in both directions.

## NOT verified

- **Windows.** Nothing has ever compiled there. The `SingleInstance` named-mutex branch and
  the `AppPaths` LocalAppData branch are written from the documentation and have not run.
  The `core-windows` CI job is what will tell us.

## Testing Windows

Not possible in a container on this Mac. Windows containers need a Windows kernel host, and
Docker Desktop here is a linux/arm64 VM. The host is an M3 Pro, so x64 Windows would mean
full CPU emulation and a Swift build measured in hours.

The routes that work:

- **`core-windows` on GitHub Actions.** Free, genuine x64 Windows Server 2022, already in
  `ci.yml`. This is the gate; it only needs a push.
- **Windows 11 ARM64 in UTM or Parallels**, for Phase 2 when the WinUI work and the MSIX
  widget need an interactive desktop that no CI runner can give. Swift ships an ARM64 Windows
  toolchain. ARM64 rather than x64, so x64-specific packaging still wants CI or a cloud VM.

## The binaries

Both CI runners publish a command line binary that runs on a machine with no Swift toolchain,
because "it compiles" is not the same claim as "it runs".

| Artifact | Shape | Size | Needs |
|---|---|---|---|
| `redline-linux-x64` | ELF x86-64, static Swift stdlib, stripped | 59 MB | `libcurl.so.4` only |
| `redline-windows-x64` | PE32+ x86-64 plus the Swift runtime DLLs | 71 MB | nothing, the VC runtime is in the folder |

Linux links the Swift stdlib statically, so only ordinary system libraries remain. Windows
cannot do that, so `scripts/package-windows.ps1` copies the runtime DLLs next to the exe and
lets Windows resolve them from the binary's own directory. CI proves it by running the
packaged copy with the toolchain taken off PATH entirely, which is the only version of that
claim worth making.

Both are verified by `scripts/smoke-cli.sh` and its PowerShell twin, which are not
version-print checks: they write a Claude transcript into a scratch home, ingest it, assert the
second ingest adds nothing, and read 1.1K tokens back out of the SQLite warehouse.

The Windows folder is heavy mostly because `_FoundationICU.dll` is 37 MB. Trimming to the real
import closure is possible later; over-copying is the safe default.

## Phase 0 is done

## Phase 1: what a Windows or Linux machine can now do

Everything below is verified on all three platforms by CI, not reasoned about.

| Capability | How | Verified by |
|---|---|---|
| Read Claude, Codex and Ollama from disk | RedlineCore | the core suite |
| Live Claude limits | `scripts/claude-statusline.ps1` beside the bash one | `StatuslineFeederTests` drives whichever fits the platform |
| Count Ollama usage | `scripts/ollama-shim.ps1` beside the bash one | `OllamaShimTests`, which also covers the bash shim that had no tests before |
| Notice a file change | `DirectoryWatcher`: vnode, inotify, ReadDirectoryChangesW | `DirectoryWatcherTests` |
| Keep a token | `CredentialStore`: Keychain, Credential Manager, Secret Service, 0600 file | one shared contract, run against each real backend |
| Start at login | `Autostart`: LaunchAgent, systemd user unit, Run key | `AutostartTests`, against a scratch root |
| Keep history without an app | `redline watch` | `WatchLoopTests` |
| Publish numbers for a UI | `SnapshotBuilder` plus `redline watch` | manual end to end, plus the loop tests |

Two things worth knowing about the watch loop, because both were bugs before they were features:

- **Watching the roots is not enough.** Claude keeps transcripts one directory below
  `~/.claude/projects` and Codex three below `~/.codex/sessions`, and no backend here watches
  a subtree. The loop walks the tree, re-walks it on every sweep so a new project is picked
  up, and caps the watch count rather than opening a descriptor per directory.
- **Publishing wakes the loop that published.** The snapshot lands in the directory being
  watched, so only the two files we read, `ollama.jsonl` and `claude-usage.json`, count as a
  change. This is the same trick the app plays with the feed sidecar's mtime.

### Still to do

- Notifications are deliberately not here. The engine already produces alert events in
  `Alerting.swift`; rendering them is the shell's job, and on Windows that shell is C#.
- `redlined` and its IPC, if the WinUI app cannot simply read `snapshot.json` and shell out
  to `redline.exe`. Start by assuming it can.

## Phase 2: the Windows shell

Split so that most of it can be tested without a Windows machine.

- **`windows/RedLine.Core`** targets plain `net9.0`, not a Windows TFM, on purpose: it finds
  the engine, reads what the engine publishes, formats it, and decides what a tray shows. All
  of that runs under `dotnet test` on any machine, and in CI against the real `redline.exe`
  the same job just built.
- **`windows/RedLine.App`** is the WinUI 3 app. It cannot be exercised headlessly, so CI's
  gate is that it compiles, which is worth having: a WinUI project breaks on package and SDK
  drift far more often than on anything we wrote.

Nothing in C# parses a transcript. That would be a second implementation of a format nobody
documents, and the two would eventually report different numbers for the same day.

### Four contracts across the language boundary

None can be caught by a compiler, so each is a file that both sides assert against.

- `windows/RedLine.Core.Tests/fixtures/snapshot-headless.json` is real engine output.
  `SnapshotContractTests` exists in both languages and reads the same file. This is what
  turned up the missing Codex window: an incremental pass reports a limit once and then never
  again, so `Ingest` now records what it reads and the next pass reads it back.
- `Tests/Fixtures/formatting.json` pins every figure a person reads. The C# port formats in
  invariant culture, because a German locale would otherwise render `$1,234,567.89` as
  `$1.234.567,89` on one platform and not the other.
- `windows/RedLine.Core.Tests/fixtures/config-settings.json` is real `redline config --json`
  output. `SettingsContractTests` exists in both languages: the Swift side asserts the engine
  still publishes exactly that, the C# side asserts it can still render a control for every
  row. Regenerate it with `REDLINE_HOME=<empty dir> redline config --json`.
- `windows/RedLine.Core.Tests/fixtures/trends.json` is what the dashboard reads, for fixed
  inputs so it holds still. `TrendsContractTests` exists in both languages, and the Swift side
  carries the inputs it was made from.

### What the app is now

A tray icon with a context menu (open, refresh, quit), a window listing every limit window,
and a `SnapshotMonitor` that watches the published file and redraws when it moves. CI
publishes `redline-windows-app-x64`: a self-contained folder, about 113 MB compressed, holding
`RedLine.App.exe`, the engine `redline.exe`, and the Windows App SDK. Nothing needs installing
first.

**It runs, and CI proves the whole chain.** `RedLine.App.exe --selftest` starts the app for
real, waits until something has actually been drawn, reports what it saw, and exits. The
Windows job asserts the report:

    report: ok: tray=created engine=running title=42% windows=1

Read that backwards and it is the entire product: a Claude transcript and a live feed on disk,
the app starting its own engine watcher, the watcher ingesting and publishing a snapshot, the
monitor noticing, and the window drawing 42%. Nothing in it is planted except the transcript.

An earlier version of this test planted a finished snapshot instead, which proved nothing once
the app started its own watcher: the watcher immediately republished over it.

What is still unverified is everything a person would do next: clicking the icon, opening the
flyout, whether any of it looks right.

The self test paid for itself on its first run. The tray icon had been set from a `BitmapImage`
through `IconSource`, which compiles perfectly and then throws
`Argument 'picture' must be a picture that can be used as a Icon` at startup, because a
BitmapImage decodes asynchronously and the conversion runs before it has finished. Nothing
short of launching it would have caught that.

Three things cost a CI cycle each and are worth not rediscovering:

- A control library built against a different Windows App SDK fails the XAML compiler with
  `WMC9999` and no useful message. Keeping third-party controls out of XAML and building them
  in code-behind avoids it entirely.
- Any public type reachable from XAML gets bindable type info generated for it, and the
  generator assigns to properties, so an init-only record fails the build with `CS8852`.
- The combination that agrees is net9.0-windows, Windows App SDK 1.8 and H.NotifyIcon.WinUI
  2.3.2. The 2.4 line is net10 only, and 1.6's XAML compiler does not understand net10 at all.

`TaskbarIcon` has both `IconSource` (an `ImageSource`) and `Icon` (a `System.Drawing.Icon`).
Use `Icon`: it takes an HICON that is ready immediately. `scripts/make-windows-icon.sh` builds
`Resources/RedLine.ico` from the appiconset, writing the small sizes as BMP frames rather than
PNG, because a PNG-compressed frame is legal in an .ico and Explorer reads it but
`System.Drawing.Icon` rejects it.

### The app keeps its own data current

On macOS the app is the watcher, so history stays current because RedLine is open. On Windows
the engine is a separate process, so `EngineHost` runs `redline watch` for as long as the app
is up. Without it the app would show whatever was last published and quietly go stale, which
is worse than showing nothing.

That makes two watchers possible, because someone may also have a startup entry pointing at
one. `watch` now claims a lock and a second copy bows out with exit zero, and the host treats
a clean exit as a decision rather than a crash, so it does not restart it into a loop.

`redlined` and a named pipe are not needed and will not be built: the app reads `snapshot.json`
and shells out to `redline.exe`, which is all `RedLine.Core` ever did.

### Seen on a real desktop, 2026-08-21

PG ran it on a throwaway EC2 Windows box over RDP, because there is no Windows machine here.
Everything under the UI was right first time: the app started, ran its own engine, ingested a
planted transcript, and drew 42%, both limit windows and the cost. Every number correct.

The UI was the problem. It was a 1500x870 white window with the text in one corner and none of
the brand on it. Fixed since: carbon ground, chalk and steel text, the reading coloured by
status with the phrase still carrying the meaning in words, a rail per window, and a size and
position suited to a status readout.

The percentage is now drawn into the tray icon itself. Windows has no menu-bar text the way
macOS does, and taskbar deskbands were removed in Windows 11, so an icon that renders the
number is the only equivalent, and it is what every other taskbar meter does. `GetHicon` hands
back a GDI handle the `Icon` wrapper never frees, so each redraw destroys the one it replaces.

### Settings, without a second validator

The engine owns the rules and the shell asks. `ConfigEditor` applies a change through
`Config.apply`, the same path a hand-edited file takes, and keeps it only if it survived, so
whatever the engine would refuse to load is refused here. `redline config --json` publishes
every setting with its current value and a kind (`bool`, `number` with bounds, `choice`,
`list`), which is enough for a shell to render a control per setting without knowing a single
key by name. Keys this build does not recognise survive a write rather than being dropped.

A change reports what it did rather than only exiting non-zero, because "already that" and
"refused" are different answers: `--json` prints `changed`, `unchanged`, `rejected` with the
engine's own words for what it wanted, `unknownKey`, or `failed`. The refusal goes to stdout
with the rest, so a shell reads one stream and never parses prose. Exit codes are unchanged,
since a script still depends on them.

On the C# side `SettingsStore` shells out for both halves and knows nothing else: no key
names, no defaults, no bounds. `SettingsCatalog` carries either the settings or the reason
there are none, because a page showing nothing looks like an app with no settings. A kind this
build has never seen is kept and marked unknown rather than dropped, so a newer engine gains a
setting here instead of quietly losing one.

Two things this turned up. `EngineResult.Ran` used to mean "the exit code was one of the
status codes", which made `config` refusing a value look like an engine that would not start;
it now means the process exited at all, and the raw code and stderr travel with the result.
And `Engine.Run` read only stdout, so a stderr big enough to fill its pipe would have wedged
the call: both are drained together now.

### The settings window

Built in code-behind from what the engine publishes, with an almost empty XAML shell. Nothing
in that file knows a setting by name, what it defaults to or what it accepts: it reads a kind
and renders the control for it. Bool is a switch, number a NumberBox with the engine's own
bounds, choice a ComboBox of the words it published, list a box per option. A kind this build
has never seen is shown as text rather than hidden.

Code-behind rather than a DataTemplate for two reasons. The controls depend on kinds the XAML
cannot know, and any public type reachable from XAML gets bindable type info generated whose
setters fail on an init-only member, so a template would need a parallel set of mutable row
classes for no gain.

Three things worth keeping in mind if this is edited:

- A change runs off the UI thread, because it starts a process, and **the control's value has
  to be read before that**. Reading `toggle.IsOn` inside the background lambda is a crash, not
  a wrong answer.
- Setting a value raises the same event as someone changing it, so a `building` flag guards
  every handler. Without it, loading the page would write every setting straight back.
- A refusal is answered by reloading from the engine rather than by putting the control back
  by hand, since the engine is the only thing that knows what is now stored.

Autostart and the usage feed are on the same page but are commands rather than config, so they
are the only two things asked for by name. `redline autostart` and `redline setup` grew a
`--json` shape for it, with the same `status` / `changed` / `unchanged` / `failed` vocabulary a
config change uses. Autostart from the app starts the app, not `redline watch`: the app runs a
watcher of its own and a second one would only lose the race for the lock.

The tray now follows the configured thresholds instead of the built-in defaults, which is a
bug the settings page made visible: changing the yellow threshold used to leave the icon
colouring itself by 60 and 85 while the engine used the new numbers.

The self test builds the page and reports how many controls it made, and CI fails if that is
under twelve. A settings page that renders nothing looks exactly like one that rendered fine.

### The dashboard

Three tiles from the published snapshot, a daily chart, and where the tokens went, with a
range picker for 7, 14, 30 or 90 days.

The engine gained a `trends` command for it, which is also a real CLI command with a sparkline
table of its own. It publishes the daily series already added up across providers, the same
split per provider, the model mix, and `label_every_days`, because the axis cadence is a
decision and two shells working it out separately would label one range two ways. A quiet day
is a zero in the series rather than a missing date: dropping it slides every later day left
and draws a week that never happened.

`redline trends --json` is a fourth contract, with `TrendsContractTests` in both languages. The
fixture is output for fixed inputs rather than a live run, because the numbers move with the
clock and the shape must not. It is compared after being parsed rather than as text: Linux
writes a cost as `0.045` where macOS writes `0.044999999999999998`, the same double printed two
ways, and a text comparison fails on whichever machine did not make the file.

What the C# decides is pixels. `TrendChart` sizes bars against the busiest day, and labels
counting **back** from the newest, so today is always named; counting forward labels the
oldest and can leave the right hand end bare, which is the end anyone is looking at. A day with
nothing keeps a two pixel sliver in the hairline colour, so it reads as a quiet day rather than
a missing one. An unpriced model reads as `n/a`, never as zero, and any window holding one says
"at least" in front of every cost, including the per day tooltip.

A machine with no history at all publishes no buckets rather than a row of zeros, because with
no provider there is nothing to bucket. The dashboard says so in words instead of drawing a
flat fortnight.

### Distribution: exe or MSI

Ship **both**, for different audiences.

- The self-contained folder is what CI already publishes and what a beta tester should get:
  unzip, run, delete. No installer, no uninstall entry, nothing left behind.
- **MSIX** is the real answer for a released app, not MSI. It gives a proper install and
  uninstall, per-user install without admin, background auto-update from a URL, and it is the
  only packaging that can carry a Windows 11 widget. MSI is the older story and buys nothing
  here.

Signing is not optional at that point. The macOS equivalent of notarization is
**Authenticode**, and the important difference is that a fresh certificate carries no
reputation: SmartScreen warns anyway until enough installs accumulate. An **EV** certificate
skips that wait but needs hardware token or cloud HSM key storage. Budget roughly $200 to $400
a year for OV, more for EV. Azure Trusted Signing is the cheapest current route if the
eligibility rules fit.

### Still to do

First run, toasts, MSIX packaging, the widget provider, Authenticode, winget. The dashboard
has the daily chart and the model mix; the hourly chart, the cadence panel and findings are
on the macOS one and not here.

## Linux

Dropped, on 2026-08-21. There is no Linux shell and there will not be one.

What stays is the engine: `RedlineCore` builds and tests on Linux, `redline` runs there, and
`core-linux` is still in CI. Keeping it is not sentiment. It is the cheap canary that catches
a macOS-only API landing in the core, in about two minutes, where the Windows job takes seven.
The Windows build only works because the core is portable, so the thing that guards that is
worth its runtime.

## Deliberately not in the core

File watching. `DispatchSource.makeFileSystemObjectSource` is used only in
`Sources/redline/AppDelegate.swift`, so the core has no watcher to port. When the refresh loop
moves into `RedlineService` in Phase 1 it needs one watcher protocol with three backends:
FSEvents or vnode sources, inotify, and `ReadDirectoryChangesW`.
