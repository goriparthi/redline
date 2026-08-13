# Architecture

## Layout

```
Sources/RedlineCore/     Pure parsing and aggregation. No AppKit, no network, no Keychain.
  Config.swift           Config load/validate, pricing lookup, provider selection
  Usage.swift            Entry, Agg, aggregate(), token/cost formatting
  Limits.swift           LimitWindow plus per-provider limit parsers
  UsageStore.swift       Claude transcript scanner
  CodexSessions.swift    Codex rollout scanner (limits + tokens)
  OllamaUsage.swift      Ollama shim-log scanner
  CredentialScan.swift   Finds an access token in an undocumented JSON blob

Sources/redline/         The app. AppKit, network and Keychain live here only.
  main.swift             Entry point and the LaunchAgent CLI flags
  AppDelegate.swift      Menu bar UI, refresh loop, provider wiring
  OAuth.swift            Keychain token storage, CLI-token borrowing, PKCE sign-in

Tests/RedlineCoreTests/  38 tests over the core
scripts/                 Build, test, bundle, install, DMG, release, Ollama shim
Casks/redline.rb         Homebrew cask
```

The split exists for one reason: **SwiftPM cannot share a source file between targets**, so
anything that needs a unit test has to live in a library. The rule that follows is worth
keeping: if logic can be tested, it belongs in `RedlineCore`; if it touches AppKit, the
network, or the Keychain, it belongs in `redline`.

## Why the core is pure

All three input formats are undocumented and can change under us without notice. Parsers
that take injectable inputs (`UsageStore(root:)`, `CodexStore(root:)`, `OllamaStore(log:)`,
and a `now:` parameter on every `scan`) can be tested against synthetic fixtures in a temp
directory. That is why the tests can pin real shapes instead of mocking.

Nothing in the core reads the real home directory unless you let it default.

## Refresh loop

`AppDelegate.refresh()` runs two independent halves:

- `refreshLocal()` scans transcripts on a utility queue and aggregates into three buckets
  (today, last 5 hours, last 7 days). Codex limits fall out of the same scan, since they
  are already on disk.
- `refreshLimits()` fetches Claude rate limits over the network.

They are separate because one is a file read that always works and the other needs a token
that may not exist. A failure in one must never blank the other.

Polling is `pollIntervalSeconds` (default 300), plus a refresh on wake and on menu open if
the data is over 60s old.

## Auth, and why it is shaped this way

Claude rate limits need an OAuth token. Two sources, tried in order:

1. **The CLI's token**, read from the `Claude Code-credentials` Keychain item.
2. **This app's own grant**, PKCE sign-in stored under Keychain service `redline`.

The CLI's token is preferred because of a failure discovered the hard way: this app and the
Claude CLI can end up sharing one OAuth client, and the CLI's constant token rotation
invalidates the app's refresh token. The app's grant would die roughly daily while the CLI
stayed healthy. Borrowing the already-refreshed token sidesteps that entirely.

Constraints that shaped the implementation:

- **Keychain reads can block on a consent prompt**, so the probe runs on `probeQueue`,
  never the main thread. Blocking the main thread beachballs the whole menu bar.
- **The probe caches misses as well as hits** for 60s, so a denied prompt is not re-asked
  on every poll.
- **A background `LSUIElement` agent is denied silently** rather than prompted, so failure
  here is expected until access is granted once in Keychain Access.app.
- **A 401/403 while using the CLI token** marks it unusable and retries once with the app's
  own grant, in case the CLI token lacks the needed scope.
- **`invalid_grant` on refresh is terminal.** The dead token is cleared so the app falls
  back to offering Sign In. Without this it stays "signed in" and retries a dead token
  forever, which is exactly the bug that motivated this work.
- **A rebuild changes the ad-hoc code identity**, so the old Keychain item's ACL rejects the
  new binary. `TokenStore.save()` therefore handles `errSecDuplicateItem` by updating in
  place, and reports failure instead of claiming a successful sign-in.

## Display rules that are deliberate

- **The menu bar never shows tokens in the limits slot.** If limits are unavailable it shows
  `sign in` or `…`. A token count where a percentage belongs reads as real limit data; that
  ambiguity previously hid an expired sign-in for a day.
- **With several providers, the title shows the worst.** The binding constraint is whichever
  provider is nearest its cap. Per-provider detail is in the dropdown.
- **Unpriced models are counted but not costed**, and the total is marked `+`. Falling back
  to a guessed price tier silently misreports spend.

## Data notes per provider

**Claude** (`~/.claude/projects/**/*.jsonl`): one JSON object per line; usage lives at
`message.usage`. Files are cached on mtime+size so unchanged transcripts are not re-read.
Resumed sessions duplicate messages across files, so entries dedupe on
`message.id` + `requestId`. `<synthetic>` models are skipped.

**Codex** (`~/.codex/sessions/**/*.jsonl`): records are `{type, timestamp, payload}`.
`payload.type == "token_count"` carries both `rate_limits` and `info`.

- `rate_limits.primary` / `.secondary` give `used_percent`, `window_minutes` and
  `resets_at`. Windows are identified by **length**, not name: 300 → `five_hour`,
  10080 → `seven_day`. `resets_at` is **epoch seconds**, unlike Claude's ISO8601 string.
- Tokens come from `info.last_token_usage`, the **per-turn delta**. Do not sum
  `total_token_usage`: it is cumulative per session and double counts.
- `cached_input_tokens` is reported **inside** `input_tokens`, so it is split out to avoid
  billing cache reads at the full input rate.
- `info` is null on some events; those still supply usable `rate_limits`.

**Ollama** (`~/.local/share/redline/ollama.jsonl`): written by the ollama shim
(`scripts/ollama-shim.sh`, installed as `~/.local/bin/ollama` from the menu).
Ollama persists no usage history, so anything that bypasses the shim is invisible
by design. `prompt_eval_count` maps to input, `eval_count` to output.
