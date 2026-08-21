# The Windows shell

`RedLine.Core` is the half of the Windows app that has nothing to do with WinUI: it finds the
engine, reads what the engine publishes, and turns it into something a view can bind to. Pure
.NET, so it builds and tests on any machine, which is the whole reason it is a separate
project from the app.

The engine is the same Swift `redline.exe` every platform uses. Nothing here reparses a
transcript; that would be a second implementation of a format nobody documents, and the two
would eventually disagree about the same day.

Two ways to ask it something:

- `snapshot.json`, written by `redline watch`, for the numbers on screen. Cheap, already
  current, no process to start.
- `redline.exe <command> --json`, for anything the snapshot does not carry.

## Testing it without a Windows machine

`RedLine.Core` targets `net9.0` rather than a Windows TFM precisely so this works. The
integration tests in `RealEngineTests` run the real engine when `REDLINE_TEST_BIN` points at
one, and skip when it does not, so they are useful on a Linux box and in CI alike:

```sh
# a self-contained Linux engine, then the shell tests against it
docker run --rm -v "$PWD/..":/src -w /src swift:6.0-jammy \
  bash -c 'swift build -c release --static-swift-stdlib --scratch-path /tmp/sb \
           && cp /tmp/sb/release/redline /src/windows/redline-test-engine'
docker run --rm -v "$PWD":/w -w /w mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c 'REDLINE_TEST_BIN=/w/redline-test-engine dotnet test RedLine.Core.Tests -v n'
```

Static because the engine otherwise wants the Swift runtime, which the .NET image has no
reason to carry.
