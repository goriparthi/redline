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
