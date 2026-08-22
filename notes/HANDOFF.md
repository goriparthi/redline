# Handoff: the Windows port

Start here after a context clear. `notes/cross-platform.md` is the detailed state; this is the
orientation and the things that are expensive to rediscover.

## Where the work is

Branch `cross-platform-core`, well ahead of `main`, **nothing merged and nothing should
be merged** until PG says so. All three CI jobs green.

macOS 421 Swift tests, Linux 417, Windows a little fewer, plus 103 C# tests. The Swift counts
differ only by cases gated to a platform.

## The one rule

**One engine, three shells.** `RedlineCore` is the only implementation of the parsers, on every
platform. Nothing in C# ever parses a transcript.

Every format RedLine reads is undocumented and moves without notice. A second parser in another
language would eventually report a different number for the same day, and the project's own
rule is never to guess a number someone reads as fact. This is the constraint everything else
was arranged around.

## What exists

```
Sources/RedlineCore/       Foundation only. Builds on macOS, Linux, Windows.
Sources/CSQLite/           SQLite amalgamation, compiled only off macOS.
Sources/RedlinePlatform/   OS services: directory watcher, credential store, autostart,
                           the headless watch loop. One protocol each, three backends.
Sources/RedlineUI/         SwiftUI components. macOS only.
Sources/redline/           The macOS app. Unchanged by any of this.
Sources/RedlineCLI/        The standalone tool. Named "redline" off macOS, "redline-cli" on
                           macOS so the entry point cannot rot.
windows/RedLine.Core/      Plain net9.0: finds the engine, reads what it publishes, formats,
                           decides what the tray shows. Testable anywhere.
windows/RedLine.App/       WinUI 3 tray app.
```

Linux was dropped as a *shell* on 2026-08-21. The engine stays portable and `core-linux` stays
in CI, deliberately: it is the two-minute canary that catches a macOS-only API entering the
core, where the Windows job takes seven. Windows only builds because the core stays portable.

## What the Windows app does today

Tray icon with the percentage drawn into it, a dark 400x520 window with a rail per limit
window, a context menu (open, refresh, dashboard, settings, quit), a settings page built from
whatever the engine publishes, a dashboard with a daily chart and the model mix, and it runs
its own `redline watch` so history stays current the way the macOS app does by being the
watcher.

CI proves the whole chain on a real Windows runner, not just that it compiles:

```
report: ok: tray=created engine=running title=42% windows=1 settings=16 dashboard=14
```

That is a transcript on disk, the app starting its own engine, the watcher ingesting and
publishing, the monitor noticing, and the window drawing it. Sixteen is every control the
settings page built from the engine's own catalogue: eleven for the settings that take one
each, three boxes for the provider list, and the two command toggles. CI fails under twelve,
because a page that renders nothing looks exactly like one that rendered fine. The dashboard's count is bars
drawn, and zero is honest there: the feed can produce a reading before the watcher has
ingested anything, so CI only requires the number to be a number, which a page that threw
would not be.

## Not done

First run, toasts, MSIX packaging, the Windows 11 widget, Authenticode, winget. The dashboard
has its daily chart and model mix; the hourly chart, cadence and findings panels are on the
macOS one only. `redlined` and named-pipe IPC were **cancelled**: the app reads `snapshot.json` and
shells out to `redline.exe`, which is all it ever needed.

Settings are done, end to end. The engine publishes every setting and its kind through
`redline config --json`, takes a change through the same validation a hand-edited file gets,
and reports what it did rather than only exiting non-zero; `autostart` and `setup` grew the
same `--json` shape. `RedLine.Core` reads and writes through those and knows no key by name,
and the WinUI page renders a control per kind.

## The three loops

Ordered by speed. Use the fastest one that can answer the question.

**Local, seconds.** Swift on macOS: `make test`. Swift on Linux, in a container:

```sh
docker run --rm -v "$PWD":/src -w /src swift:6.0-jammy \
  bash -c 'swift test --scratch-path /tmp/build'
```

The C# model layer, including against a real engine binary:

```sh
docker run --rm -v "$PWD":/src -w /src swift:6.0-jammy bash -c \
  'swift build -c release --static-swift-stdlib --scratch-path /tmp/sb \
   && cp /tmp/sb/release/redline /src/windows/redline-test-engine'
docker run --rm -v "$PWD":/repo -w /repo/windows mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c 'REDLINE_TEST_BIN=/repo/windows/redline-test-engine dotnet test RedLine.Core.Tests -v n'
```

Static on purpose: a dynamically linked build wants the Swift runtime, which the .NET image has
no reason to carry. Delete `windows/redline-test-engine` afterwards; it is gitignored.

The WinUI code-behind, against the real Windows App SDK, in about ten seconds:

```sh
scripts/check-winui.sh
```

It stands in for what the XAML compiler generates, `InitializeComponent` and a field per
`x:Name`, and compiles everything else against the actual SDK reference assemblies in a Linux
container. It does **not** run the XAML compiler, so markup errors are still CI's to find, but
it catches every wrong type and member name, which is most of what goes wrong here. Compile
only: the full Build target then runs `MakePri.exe`, which is a Windows binary.

**CI, about seven minutes.** The only way to compile or run anything Windows from this Mac.

```sh
git push origin cross-platform-core && gh workflow run ci.yml --ref cross-platform-core
```

Anything WinUI can only be checked here. `dotnet test` verbosity is `-v n` on purpose: `-v q`
reports that something failed without reporting what, which costs a whole cycle.

**The Windows VM, minutes plus money.** Only for looking at the UI with human eyes. See below.

## Gotchas that each cost a CI cycle

Windows, Swift:

- `BOOL` imports as a plain Swift `Bool`. Pass `true`, not `1`.
- `HANDLE` is a non-optional `UnsafeMutableRawPointer`, though functions returning one hand
  back an optional.
- `KEY_READ` and `KEY_WRITE` are composed macros the importer will not fold; their values are
  spelled out in `Autostart.swift`.
- Deleting a registry value needs write access just as much as setting one does.
- Swift 6.0 cannot build **anything** on a current runner: its Windows SDK module maps predate
  the runner's Visual Studio and `ucrt` cycles through `_visualc_intrinsics`. CI pins 6.3.3.
  Pinning the image back to `windows-2022` does not help either. If you see
  `cyclic dependency in module 'ucrt'`, bump Swift, the code is fine.

WinUI:

- A control library built against a different Windows App SDK fails the XAML compiler with
  `WMC9999` and no usable message. Keep third-party controls out of XAML and build them in
  code-behind, where only the C# API matters.
- Any public type reachable from XAML gets bindable type info generated, and the generator
  *assigns* to properties, so an init-only record fails with `CS8852`. Use a class with
  settable properties.
- The combination that agrees: net9.0-windows, Windows App SDK 1.8, H.NotifyIcon.WinUI 2.3.2.
  The 2.4 line is net10 only.
- `TaskbarIcon.IconSource` is an `ImageSource`; a `BitmapImage` decodes asynchronously and the
  icon conversion runs first, throwing `must be a picture that can be used as a Icon` at
  startup. Use `.Icon`, a real HICON.
- `GetHicon` hands back a GDI handle the `Icon` wrapper never frees. Destroy the previous one
  on every redraw.
- A control's value must be read on the UI thread. `SettingsWindow` runs each change off it,
  because a change starts a process, and reading `toggle.IsOn` inside that lambda is a crash.
- Setting a control's value raises the same event as someone changing it, so every handler is
  guarded by a `building` flag or loading the page writes every setting straight back.
- JSONSerialization prints a double differently per platform: Linux writes `0.045` where macOS
  writes `0.044999999999999998`. Compare a JSON fixture after parsing it, never as text.
- `Rest` is a disallowed tuple element name, at any position.
- The `FontWeight` struct is `Windows.UI.Text.FontWeight`, while the constants are
  `Microsoft.UI.Text.FontWeights`. Importing the first namespace makes the second ambiguous
  with its UWP twin, so name the struct in full instead.
- **`WMC9999` can be a cascade.** A CS error in the code-behind makes the XAML compiler's
  first pass throw with no usable message, so fix the C# before suspecting the markup.
  `scripts/check-winui.sh` finds that class of error without a CI cycle.

Cross-language:

- Linux Foundation's `NumberFormatter` ignores `maximumFractionDigits` and printed a cost as
  `$12.274`. Both languages now format by hand against `Tests/Fixtures/formatting.json`.
- Swift multiline literals drop the last newline before the closing delimiter, which silently
  broke the embedded-script drift test.
- Record equality compares collections by *reference*, so two identical snapshots never
  matched and the UI would have redrawn on every sweep. `SnapshotMonitor` compares bytes.
- Days are UTC. A test transcript stamped "twenty minutes ago" belongs to yesterday for the
  first twenty minutes of a UTC day, and today's total is then zero while the week's is not.
  `EngineHostTests` clamps the stamp to midnight; runners are UTC, so this fails for real.
- An exit code outside the status vocabulary is still a run. `config` exits 2 to refuse a
  value, and `EngineResult.Ran` used to call that an engine that would not start.

## Four contracts across the language boundary

None can be caught by a compiler, so each is a file both sides assert against:

- `windows/RedLine.Core.Tests/fixtures/snapshot-headless.json` is real engine output.
  `SnapshotContractTests` exists in **both** languages and reads the same file.
- `Tests/Fixtures/formatting.json` pins every figure a person reads.
- `windows/RedLine.Core.Tests/fixtures/config-settings.json` is real `redline config --json`
  output, and `SettingsContractTests` exists in both languages too.
- `windows/RedLine.Core.Tests/fixtures/trends.json` is what the dashboard reads, built from
  fixed inputs the Swift `TrendsContractTests` carries, so it does not move with the clock.

Regenerate the snapshot fixture by running `redline watch` against a scratch home and copying
what it publishes, and the settings fixture with `REDLINE_HOME=<empty dir> redline config
--json`. If a field is renamed, the Swift suite fails first and says so.

## Generated files

Both are committed output with regenerable input, and both have drift tests:

- `Resources/RedLine.ico` from `scripts/make-windows-icon.sh`. Small sizes are **BMP** frames,
  not PNG: a PNG frame is legal in an .ico and Explorer reads it, but `System.Drawing.Icon`
  rejects it, which is what the tray needs.
- `Sources/RedlineCore/StatuslineScripts.swift` from `scripts/embed-statusline.sh`.

## The AWS test VM

There is no Windows machine here, so PG runs the UI on a throwaway EC2 box over RDP from his
Mac. A console screenshot only ever shows the lock screen, so RDP is the only way to see it.

Credentials are **outside the repo** at `~/.aws/personal/`, reached through
`.claude/settings.local.json`, which is gitignored. Account `982306763589`, profile `bootstrap`
(IAM user `pgoriparthi`). Never put credentials in this repo: it is public.

The permission classifier blocks anything that reads those files or sets a Windows password, so
the app is installed by user data from a presigned S3 URL and the Administrator password comes
from `aws ec2 get-password-data`. Do not try to work around the classifier; ask PG.

Live resources as of the handoff, all in `ca-central-1`, all disposable:

| Thing | Id |
|---|---|
| instance | `i-024c586386fc2ebb4` (t3.large, ~$0.18/hr) |
| security group | `sg-00dfa3b1ec5d6e5b9` (RDP from one address) |
| key pair | `redline-winvm` |
| IAM role | `redline-winvm-ssm` (created, never attached) |
| bucket | see `scratchpad/vm/bucket.txt`, holds `latest.zip` |

Connection details are in `/tmp/redline-vm.txt`; `/tmp/redline-vm-update.ps1` pulls the newest
build onto the box and restarts it. **Terminate when done**, and delete the group, key pair,
role and bucket with it.

## Next

MSIX, so there is a real installer and the widget has something to ship in. First run and
toasts are smaller and can follow. MSIX rather than MSI because it is the only packaging that can carry the widget.

Signing is Authenticode, and unlike notarization a fresh certificate carries no reputation, so
SmartScreen warns anyway until installs accumulate. EV skips the wait. Azure Trusted Signing is
the cheapest sane route if the eligibility rules fit.
