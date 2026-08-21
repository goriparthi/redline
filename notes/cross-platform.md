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

## Phase 2 and 3

The native shells. Nothing has started.

## Deliberately not in the core

File watching. `DispatchSource.makeFileSystemObjectSource` is used only in
`Sources/redline/AppDelegate.swift`, so the core has no watcher to port. When the refresh loop
moves into `RedlineService` in Phase 1 it needs one watcher protocol with three backends:
FSEvents or vnode sources, inotify, and `ReadDirectoryChangesW`.
