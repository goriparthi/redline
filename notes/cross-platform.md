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
- `SingleInstance` gained a Windows branch.
- CI gained `core-linux` and `core-windows` jobs.

## Verified

- macOS: `make ci` green, 347 tests, widget builds and the bundle verifies.
- Linux: `swift test` under `swift:6.0-jammy` green, 343 tests, 0 failures. That build
  compiled the vendored SQLite through SwiftPM, so `import CSQLite` is proven too.

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

## Still to do in Phase 0

Push, and let `core-windows` turn the Windows column from reasoning into fact.

## Deliberately not in the core

File watching. `DispatchSource.makeFileSystemObjectSource` is used only in
`Sources/redline/AppDelegate.swift`, so the core has no watcher to port. When the refresh loop
moves into `RedlineService` in Phase 1 it needs one watcher protocol with three backends:
FSEvents or vnode sources, inotify, and `ReadDirectoryChangesW`.
