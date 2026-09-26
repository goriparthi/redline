# Porting RedLine to Windows: conventions

The Windows build is a C# / .NET 8 port of the Swift sources in `../Sources`. The Swift code is
the specification: port behaviour 1:1, including comments' intent, edge cases and tests.

## Layout

```
windows/
  src/Redline.Core/        Port of Sources/RedlineCore. net8.0, no WPF, no network, no Credential Manager.
  src/Redline.App/         Port of Sources/redline plus the widget. WPF + WinForms tray (net8.0-windows).
  src/Redline.Cli/         `redlinectl.exe`: the CLI (status, findings, history, cadence, ingest,
                           statusline feeder, ollama shim).
  tests/Redline.Core.Tests Port of Tests/RedlineCoreTests (xunit).
```

## Naming (so parallel ports line up without coordination)

- Swift type `Foo` becomes C# type `Foo` in namespace `Redline.Core` (one file per Swift file,
  same base name: `UsageStore.swift` -> `UsageStore.cs`).
- Members go PascalCase: `lookbackDays` -> `LookbackDays`, `scan(lookbackDays:now:)` ->
  `Scan(int lookbackDays, DateTimeOffset? now = null)`. Argument labels become parameter names.
- Swift `enum` with only cases -> C# `enum`. With associated values -> `abstract record` with
  nested `sealed record` cases. With static members only (a namespace) -> `static class`.
- Swift `struct` -> `sealed record` (or `sealed class` when mutated in place).
- `Date` -> `DateTimeOffset` (UTC). `TimeInterval` -> `double` seconds. `URL` for files ->
  `string` path. `[String: Any]` JSON -> `System.Text.Json.Nodes.JsonObject`, read through the
  helpers in `Json.cs` (`Json.Num`, `Json.Str`, `Json.Bool`, `Json.Int`, ...).
- Injectable `root:`/`log:`/`home:` parameters become `string? root = null` etc. and default
  through `RedlineHome` (`RedlineHome.Url`, `RedlineHome.PathFor(".claude/projects")`,
  `RedlineHome.DataDir()`). `now:` becomes `DateTimeOffset? now = null`.
- Diagnostics: `Diag.Log.Error(code, message, new() { ["k"] = "v" })`, `Diag.Log.Attempt(...)`.
- Already ported (do not rewrite, extend only by coordination): `RedlineHome`, `Json`,
  `Diagnostics` (`Diag`, `DiagnosticsLog`, `Redaction`), `Provenance`, `Config`, `Usage`
  (`Entry`, `Agg`, `ProviderUsage`, `ModelUsage`, `Usage.Aggregate`, `Usage.FmtTokens`,
  `Usage.FmtCost`), `Limits` (`LimitWindow`, `LimitParser`), `Availability`.
- Free functions in Swift (`aggregate`, `fmtTokens`) live on a static class named after the file.

## Windows specifics

- Paths stay the same relative to the user profile (`%USERPROFILE%\.claude`,
  `%USERPROFILE%\.config\redline`, `%USERPROFILE%\.local\share\redline`), so docs, the sidecar
  and Claude Code's own layout line up with macOS.
- `chmod 0600` has no equivalent; files live under the user profile, which is already private.
- Keychain -> Windows Credential Manager (App only). Claude Code on Windows keeps its
  credential in `%USERPROFILE%\.claude\.credentials.json`.
- Bash scripts (`claude-statusline.sh`, `ollama-shim.sh`) become subcommands of `redlinectl.exe`.

## House rules (from ../CLAUDE.md)

- No em dashes anywhere. Comments and file headers: two lines maximum.
- Never guess a number the user reads as fact.
- Every scanner takes injectable inputs so tests run against a temp directory.
