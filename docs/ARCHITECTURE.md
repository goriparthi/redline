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
  Ollama.swift           Local server model list and running-model shapes
  Availability.swift     Which providers are installed on this Mac
  Trends.swift           Per-day, per-hour and model-mix series for the dashboard
  Sparkline.swift        Point series for the menu bar and dashboard sparklines
  Snapshot.swift         Wire format the app writes and the widget renders
  ServiceStatus.swift    Status-page parsing, plus the shared health glyph vocabulary
  SingleInstance.swift   The lock that keeps one copy in the menu bar
  StatuslineFeed.swift   Claude's limit windows as Claude Code itself reports them
  CredentialScan.swift   Finds a credential in an undocumented JSON blob, plus hex decoding
  ClaudeAuth.swift       What a failed credential read or refresh means, and how long to wait
  Brand.swift/BrandUI    Colour tokens and the shared track badges

Sources/redline/         The app. AppKit, network and Keychain live here only.
  main.swift             Entry point, instance guard, and the LaunchAgent CLI flags
  AppDelegate.swift      Menu bar UI, refresh loop, provider wiring
  Dashboard.swift        The charts window
  OllamaService.swift    Live local-server state and start/stop
  Updates.swift          Release check and the verified in-place install
  FirstRun.swift         The providers and limits setup window
  MenuRowView.swift      Menu rows that read as information, not controls
  OAuth.swift            Keychain token storage, CLI-token borrowing, PKCE sign-in
  ClaudeCredentialSource Reads the CLI's credential from file, Keychain, or security(1)
  DelegatedRefresh.swift Asks Claude Code to renew its own token instead of doing it for it
  StatuslineInstaller    Installs the usage feed and the settings.json entry pointing at it

Sources/RedlineWidget/   The WidgetKit extension. Renders the snapshot, parses nothing.

Tests/RedlineCoreTests/  146 tests over the core
scripts/                 Build, test, bundle, install, DMG, release, Ollama shim,
                         Claude usage feed
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
- `refreshLimits()` reads Claude rate limits, from the usage feed on disk when it is
  installed and reporting, and over the network otherwise.

They are separate because one is a file read that always works and the other may need a token
that does not exist. A failure in one must never blank the other.

The feed is checked first and returns early on a hit, so the whole token path is skipped
whenever Claude Code has already reported its own windows. Windows whose reset has passed are
dropped on read, which is what lets a feed that has gone quiet fall through to the token path
without anything having to notice that it went quiet.

Polling is `pollIntervalSeconds` (default 300), plus a refresh on wake and on menu open if
the data is over 60s old.

## Auth, and why it is shaped this way

Claude rate limits are read from four sources, tried in order, each one cheaper and less
intrusive than the one below it:

0. **The usage feed.** Claude Code passes its own `rate_limits` to whatever `statusLine`
   command is configured. `scripts/claude-statusline.sh` files that block to
   `~/.local/share/redline/claude-usage.json`, and `StatuslineFeed` parses it. No token, no
   Keychain, no network. When this reports, nothing below it runs.
1. **The CLI's credential**, via `ClaudeCredentialSource`: the file at
   `~/.claude/.credentials.json`, then the Keychain API, then `/usr/bin/security`.
2. **Delegated refresh.** If that credential has expired, `claude auth status` asks Claude
   Code to renew its own, and the credential is read again.
3. **Minting**, exchanging the CLI's refresh token directly, then this app's own PKCE grant
   under Keychain service `redline`.

Order is the whole design. Each rung costs more and carries more risk than the last, so the
common case never reaches the expensive ones.

### Why the ladder exists

The original single-source version needed a manual **Reconnect** once or twice a day. The
cause was not the Keychain grant expiring, which was the earlier theory recorded here and is
wrong: `Claude Code-credentials` is updated in place, its creation date does not move, and a
Developer ID signature keeps the ACL valid across RedLine's own rebuilds.

The real cause was two things compounding. `CredentialScan` returned nil for an *expired*
token, indistinguishable from an absent one, and `refreshCLIProbe` cached that nil and then
short-circuited on `have` forever. Claude Code renews its token only when it runs, so an idle
CLI plus one expired read latched RedLine off until the user clicked Reconnect.

Constraints that shaped the implementation:

- **Nothing latches except a signed-out CLI.** `CredentialOutcome` separates `notFound` from
  `accessDenied` and `unreadable`; only the first is durable. The rest retry on a timer.
  Discarding the `OSStatus` is what caused the original bug, twice.
- **The credential is cached with its expiry**, not as a bare string, so it is re-read when it
  dies rather than after a request fails.
- **Attribute-only Keychain reads cost nothing.** A query without `kSecReturnData` never
  decrypts the secret, so it neither consults the ACL nor prompts. Watching
  `kSecAttrModificationDate` is therefore a free signal that the CLI rotated the token.
- **Keychain reads can block on a consent prompt**, so the probe runs on `probeQueue`, never
  the main thread. Blocking the main thread beachballs the whole menu bar. For the same
  reason the `security` subprocess waits 90 seconds: a shorter kill fires while the dialog is
  still open and reads a waiting user as a denial.
- **`security -w` hex-encodes** any payload it cannot return as a clean C-string, and Claude
  Code's blob line-wraps, which triggers it. `SecurityCLIOutput.decode` handles that.
- **Delegated refresh measures itself.** Whether `claude auth status` renews an expired token
  is not documented, so the code compares the stored expiry before and after and stops
  spawning the process once it proves ineffective on this machine.
- **Minting forks the refresh chain.** Anthropic rotates refresh tokens, so minting can leave
  the CLI's copy stale and force a fresh `/login`. That is why it is last, why the result goes
  in RedLine's own Keychain item rather than back over the CLI's, and why a refusal naming the
  grant stops the attempt permanently.
- **The endpoint requires the OAuth scope `user:profile`**; only the token Claude Code writes
  at `/login` carries it, which is why `claude setup-token` output is rejected.
- **`invalid_grant` on refresh is terminal.** The dead token is cleared so the app falls
  back to offering Sign In. Without this it stays "signed in" and retries a dead token
  forever.
- **A rebuild changes an ad-hoc code identity**, so the old Keychain item's ACL rejects the
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
