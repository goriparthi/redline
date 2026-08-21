# Architecture

## Layout

```
Sources/RedlineCore/     Pure parsing and aggregation. Foundation only: no AppKit, no SwiftUI,
                         no network, no Keychain. Builds on macOS, Linux and Windows.
  Config.swift           Config load/validate, pricing lookup, provider selection
  Usage.swift            Entry, Agg, aggregate(), token/cost formatting
  Limits.swift           LimitWindow plus per-provider limit parsers
  UsageStore.swift       Claude transcript scanner
  CodexSessions.swift    Codex rollout scanner (limits + tokens)
  ClaudeFleet.swift      Claude Code's live session registry: who is running, who is blocked
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
  ClaudeAuth.swift       What a failed credential read means, plus the usage-endpoint backoff
  Brand.swift            Fixed brand tones as plain RGB, and the utilization thresholds
  AppPaths.swift         The two directories RedLine owns, resolved per platform
  ProviderMark.swift     The provider marks as vector data
  ProviderIdentity.swift Which mark and name belong to a provider
  Status.swift           The status vocabulary as data, with no colour and no symbol name
  ProviderOverview.swift What a provider's overview card says, and which warnings earn a place
  ClaudeLimitsChoice     Where Claude's percentages come from, and which route is current

Sources/CSQLite/         The SQLite amalgamation, compiled only off macOS. See its README.

Sources/RedlineUI/       The shared SwiftUI component set. macOS only.
  DesignSystem.swift     Every colour, space, radius and text style the UI may use
  Components.swift       The shared cards, badges, tiles, rails, status marks and states
  BrandUI.swift          The RedLine mark, the limit rail and the track badges
  ProviderGlyph.swift    Draws a ProviderMark: the template loader and the view

Sources/redline/         The app. AppKit, network and Keychain live here only.
  main.swift             Entry point, instance guard, and the LaunchAgent CLI flags
  AppDelegate.swift      Menu bar UI, refresh loop, provider wiring
  Dashboard.swift        The charts window
  OllamaService.swift    Live local-server state and start/stop
  Updates.swift          Release check and the verified in-place install
  FirstRun.swift         The providers and limits setup window
  MenuRowView.swift      Menu rows that read as information, not controls
  ProviderCards.swift    The overview cards and the warnings above them
  SettingsWindow.swift   Settings in named sections, driving the app's own actions
  Previews.swift         Sample data and every UI state, DEBUG only
  TerminalFocus.swift    Finds the app and tab a session runs in, by process tree and tty
  OAuth.swift            Keychain token storage, CLI-token borrowing, PKCE sign-in
  ClaudeCredentialSource Reads the CLI's credential from file, Keychain, or security(1)
  DelegatedRefresh.swift Asks Claude Code to renew its own token instead of doing it for it
  StatuslineInstaller    Installs the usage feed and the settings.json entry pointing at it

Sources/RedlineWidget/   The WidgetKit extension. Renders the snapshot, parses nothing.

Tests/RedlineCoreTests/  The core suite
Tests/RedlineUITests/    The three tests that need AppKit to answer
scripts/                 Build, test, bundle, install, DMG, release, Ollama shim,
                         Claude usage feed
Casks/redline.rb         Homebrew cask
```

Two splits, two reasons.

`RedlineCore` exists because **SwiftPM cannot share a source file between targets**, so
anything that needs a unit test has to live in a library. If logic can be tested it belongs
there; if it touches the network or the Keychain it belongs in `redline`.

`RedlineUI` exists because the core has to compile where there is no AppKit. It holds the
components the app and the widget share, which is why they are not simply part of the app.
The boundary is drawn by symbol rather than by file: `ProviderMark`, `ProviderIdentity` and
`RLStatus` are data the CLI needs, so they stay in the core, while their colours, SF Symbol
names and views live in `RedlineUI` as extensions. `Package.swift` adds the macOS-only
targets under `#if os(macOS)`, so a Linux build cannot pull AppKit in by accident.
See `notes/cross-platform.md`.

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

The feed is checked first and wins outright while it is **fresh**: within
`StatuslineFeed.freshFor` (15 minutes) of its own `updated_at` stamp. Presence alone is not
enough, because a week window stays formally valid for days while its percentage drifts; an
hours-old sidecar once shadowed a live sign-in by five points. Past the gate a live fetch
takes over, and the feed's last unexpired reading is only the fallback when no live source
answers, carried with its timestamp so every surface draws it stale. Windows whose reset has
passed are dropped on read regardless.

`refresh()` also runs `StatuslineInstaller.repairIfNeeded()` each poll: a Claude Code session
that predates the feed holds `settings.json` in memory and writes it back without the
`statusLine` chain (saving a model choice is enough), so the wiring is restored whenever the
wrapper script on disk outlives the settings entry. The script is the marker of intent:
install writes it, uninstall removes it.

Polling is `pollIntervalSeconds` (default 300), plus a refresh on wake and on menu open if
the data is over 60s old. The feed does not wait for any of that: a dispatch source watches
the app's data directory and re-reads limits when the sidecar's mtime moves, so the title
tracks Claude Code in near real time while it runs. The directory is watched rather than the
file because the feeder replaces the file atomically, which would orphan a file-level
watcher, and the mtime comparison keeps the app's own snapshot writes into the same
directory from becoming a refresh loop.

## The agent fleet

`ClaudeFleetStore` reads `~/.claude/sessions/<PID>.json`, Claude Code's live session registry:
one record per running session, deleted on exit. Read only, always; RedLine never writes into
`~/.claude`, never opens the `<PID>.<hash>.key` siblings, and never touches the messaging
sockets the records name.

Two things about it are not obvious:

- **Two watchers, not one.** A session starting or exiting changes the directory, so a
  directory watcher sees it. A *status* change rewrites the record in place, which never
  touches the directory, so each live record is watched individually as well. A 30 second
  sweep sits under both, because a killed session writes nothing at all and its leftover
  record has to be reaped on a timer.
- **`procStart` names no timezone.** It is written in UTC, which is not what a ctime string
  usually means; reading it as local rejected every live session on this machine. Both
  readings are now accepted. The check exists only to catch a recycled PID, and a guard that
  can empty the whole pane is worse than the rare leftover it prevents.

Every field except `pid` and `cwd` is optional, and `status` and `entrypoint` are kept as
open strings rather than enums: this is undocumented internals, and an upstream addition
should cost a row's detail rather than the row.

Focusing a session goes one step further than raising its app. A record names no window, but
the process has a controlling terminal, and iTerm2 and Terminal both publish the tty of every
tab, so the tty is the join. `TerminalFocus` reads it from the kernel and asks the terminal
over AppleScript to select that tab. The app is raised first and unconditionally, so a
refused consent dialog or an unscriptable terminal degrades to "the right app came forward"
rather than to nothing, and the menu item says which of the two it will do. This is the one
place RedLine sends an Apple event, which is why the hardened runtime entitlement
`com.apple.security.automation.apple-events` exists; both build paths sign with it.

Scope is local sessions only. Cloud sessions and sessions on other Macs have no public API,
and Codex publishes no live registry at all, so `CodexStore` reads finished transcripts and
the two must not be unified.

## Auth, and why it is shaped this way

Claude rate limits are read from these sources, tried in order, each one cheaper and less
intrusive than the one below it:

0. **The usage feed, while fresh.** Claude Code passes its own `rate_limits` to whatever
   `statusLine` command is configured. `scripts/claude-statusline.sh` files that block to
   `~/.local/share/redline/claude-usage.json`, and `StatuslineFeed` parses it. No token, no
   Keychain, no network. When this reports fresh figures, nothing below it runs.
1. **The CLI's credential** (opt-in, `useCLIToken`), via `ClaudeCredentialSource`: the file
   at `~/.claude/.credentials.json`, then `/usr/bin/security`, then the Keychain API. The
   `security` tool sits in the item's `apple-tool:` partition and reads without consent UI on
   most installs; the API call is the one path that prompts, so it goes last.
2. **Delegated refresh.** If that credential has expired, `claude auth status` asks Claude
   Code to renew its own, and the credential is read again. **This is the ceiling**: the
   borrowed refresh token is never spent. An earlier build exchanged it directly ("minting");
   Anthropic rotates refresh tokens on use, so every mint left the CLI holding a consumed
   token and forced a fresh `/login`. That code is deleted, not gated.
3. **This app's own PKCE grant** under Keychain service `redline`, when the user signed in.
   Its refresh chain is its own; whatever happens to it, the CLI's login is untouched.
4. **The feed's last unexpired reading**, drawn stale, when nothing live answered.

Order is the whole design. Each rung costs more and carries more risk than the last, so the
common case never reaches the expensive ones, and no rung anywhere can end another tool's
session.

### Why the ladder exists

The original single-source version needed a manual **Reconnect** once or twice a day, and it
had two causes that were each, at different times, mistaken for the whole story.

One was RedLine's own handling: `CredentialScan` returned nil for an *expired* token,
indistinguishable from an absent one, and `refreshCLIProbe` cached that nil and then
short-circuited on `have` forever. Claude Code renews its token only when it runs, so an idle
CLI plus one expired read latched RedLine off until the user clicked Reconnect.

The other was real after all: an earlier revision of this file claimed the Keychain grant
survives Claude Code's rewrites because the item is updated in place. Observed on 2026-08-17,
it does not hold in practice; **Always Allow** was granted repeatedly and the consent prompt
kept returning as Claude Code rewrote its item. That observation is why the prompting API
read moved to the back of the credential read order rather than being reasoned about further.

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
- **Borrowed tokens are read, never spent.** Refreshing a lineage another application owns
  signs that application out, because Anthropic rotates refresh tokens on use. Every peer
  tool surveyed enforces the same rule; the one that refreshes at all does so only when the
  CLI is provably idle and writes the rotated credential back. RedLine's answer is simpler:
  when the CLI will not renew, the display goes stale, honestly drawn.
- **The endpoint requires the OAuth scope `user:profile`**; only the token Claude Code writes
  at `/login` carries it, which is why `claude setup-token` output is rejected. RedLine's own
  PKCE sign-in requests the same scope, and the endpoint accepts it.
- **A rejected refresh is terminal, not only `invalid_grant`.** The dead token is cleared so
  the app falls back to offering Sign In. The original test looked for the literal string
  `invalid_grant`, and this endpoint answers in Anthropic's API envelope instead
  (`invalid_request_error`), so a rejected refresh was retried on every poll forever: the app
  stayed "signed in", the percentages never came back, and nothing offered Sign In.
  `ClaudeAuthPolicy.classifyRefresh` inverts the rule. Only a lifted rate limit, a recovered
  server or a working network can make an identical request succeed later, so those three
  retry and everything else in the 4xx family clears the grant.
- **Token requests are form-encoded, with a JSON retry.** RFC 6749 specifies
  `application/x-www-form-urlencoded` for the token endpoint, and this code only ever sent
  JSON. Which of the two this undocumented endpoint honours is not established, so the
  spec-compliant body goes first, a 4xx is retried the other way, and whichever answers is
  remembered for the rest of the launch.
- **A rebuild changes an ad-hoc code identity**, so the old Keychain item's ACL rejects the
  new binary. `TokenStore.save()` therefore handles `errSecDuplicateItem` by updating in
  place, and reports failure instead of claiming a successful sign-in.

## Display rules that are deliberate

- **The menu bar never shows tokens in the limits slot.** If limits are unavailable it shows
  `sign in` or `…`. A token count where a percentage belongs reads as real limit data; that
  ambiguity previously hid an expired sign-in for a day.
- **Stale is a palette, not a footnote.** A Claude window older than the staleness threshold
  drains from its status color to steel on every surface (menu bar title, dropdown, dashboard
  rail, widget), with its capture time beside it in amber. The number survives; what it loses
  is the claim to be current. The snapshot carries `claudeLimitsAsOf` so the widget can make
  the same call without parsing anything.
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
