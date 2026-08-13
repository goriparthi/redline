# Redline

**Know your limit.**

Redline is a private macOS usage monitor for Claude, Codex, and Ollama. It shows what has
been used, what remains, and when limits reset, in the menu bar and an optional desktop
widget.

Brand assets, tokens, and voice rules live in [brand/](brand/); `brand/BRAND.md` is the
source of truth for colour, type, and copy. Note that it explicitly rules out racing
metaphors and fear language, so keep the wording calm and factual.

> **Not affiliated with, endorsed by, or supported by Anthropic, OpenAI, or Ollama.**

## Read this before you install

Redline's **token and cost totals** come from transcript files those tools already write to
your disk. That part uses no API and no credentials.

Redline's **Claude rate-limit percentages** are different, and you should understand what you
are opting into:

- They come from `https://api.anthropic.com/api/oauth/usage`, which is **not a documented or
  published API**. It is an internal endpoint the Claude Code CLI uses for itself.
- Because it is undocumented, **there is no published permission to use it**. Doing so may fall
  outside Anthropic's Terms of Service or acceptable use policy. Nobody here has a ruling
  either way, and this project is not in a position to give you one.
- Reading the Claude CLI's stored OAuth token (`useCLIToken`, off by default) means using
  **your own credential** in a way Anthropic has not documented or endorsed.
- The endpoint can change, start refusing requests, or disappear without notice. It already
  rate-limits aggressively, and Redline backs off when it does.

**You are responsible for deciding whether that is acceptable for your account.** If you would
rather not touch it at all, Redline is still useful: leave `useCLIToken` false and
`oauth.clientId` empty, which is the shipped default. In that state Redline makes **no network
requests whatsoever** and still reports tokens, cost, model mix, and history for Claude, plus
everything for Codex and Ollama. You lose only the Claude percentages.

Codex and Ollama involve no such question. Codex is read entirely from local files, and Ollama
is your own machine.

If Anthropic publishes a real usage API, this all becomes moot and Redline should move to it.

## What it shows

- **Usage and remaining capacity** per provider for the session and week windows, coloured by the brand thresholds (Clear, Amber, Signal).
- **The nearest limit in the menu bar**, or a provider you pick. By default the title shows
  whichever provider is closest to its limit, since that is the one that will interrupt you
  first. Choose a single provider from **Menu bar shows** if you would rather watch one. The
  dropdown always breaks down every provider.
- **Token and cost totals** for today, the last 5 hours, and the last 7 days, grouped under
  each provider with inline share bars, so a model can never be listed under the wrong
  provider and relative weight is visible without reading the numbers.

## Install

### Homebrew

```sh
brew install --cask ./Casks/redline.rb
```

Or from a tap, once published:

```sh
brew tap goriparthi/tap && brew install --cask redline
```

### From source

```sh
git clone git@github.com:goriparthi/redline.git && cd redline
make install
```

`make install` builds a release binary, assembles `Redline.app`, copies it to
`~/Applications`, and loads a LaunchAgent so the menu bar item returns after a restart.

### Gatekeeper will block a downloaded build

Releases are **ad-hoc signed and not notarized**, because notarizing needs a paid Developer ID
Application certificate. macOS will refuse to open the app on first launch and may say it is
damaged. It is not; it is unsigned by a recognised authority.

```sh
xattr -dr com.apple.quarantine /Applications/Redline.app
```

**Building from source avoids this entirely** and is two commands, so it is the recommended
route for anyone who has Xcode. Do not run that `xattr` command on software you have not
checked; the point of the quarantine flag is to make you stop and think.

## Providers

Pick which ones to read from the **Providers** submenu in the dropdown, or set `providers`
in the config. Each is independent; none is required.

| Provider | Source | Auth | Rate limits | Tokens |
| --- | --- | --- | --- | --- |
| Claude | `~/.claude/projects/**/*.jsonl` | none for tokens; see below for limits | yes, via API | yes |
| Codex | `~/.codex/sessions/**/*.jsonl` | none | yes, from disk | yes |
| Ollama | `~/.local/share/redline/ollama.jsonl` | none | n/a | yes, via wrapper |

### What works immediately, and what needs a decision

Out of the box, with no configuration:

| Works now | Needs you to opt in |
| --- | --- |
| Claude tokens, cost, model mix, history | Claude session and week percentages |
| Codex limits, tokens, history | |
| Ollama models, status, volume | |

The percentages are the only part that needs the undocumented endpoint described above.

### Claude rate limits need a token

Token and cost totals come from transcripts on disk and need no credentials. The
**rate-limit percentages** come from an undocumented endpoint that requires an OAuth token,
obtained one of two ways:

1. **Borrow the CLI's token** by setting `useCLIToken: true`. **Off by default**, because
   that Keychain item belongs to another application and reading it should never be a silent
   default. Once enabled, macOS asks for permission; a background agent is denied silently
   rather than prompted, so grant it once in Keychain Access.app: find
   `Claude Code-credentials`, open **Access Control**, and add `Redline.app`.
2. **Its own sign-in.** Requires `oauth.clientId` in the config. **No client id ships by
   default** and Sign In stays disabled until you set one, because this project is not
   registered with Anthropic and shipping someone else's client id is not ours to do.

If neither is available the menu bar shows `sign in` rather than a number. It deliberately
never falls back to showing token counts in the limits slot, because a plausible-looking
number in that position reads as real limit data.

### Ollama needs a wrapper

Ollama keeps no usage history, so there is nothing to read retroactively. Route calls
through the bundled wrapper and it records each one:

```sh
scripts/ollama-run.sh qwen3-coder:30b <<'PROMPT'
Summarize this build log.
PROMPT
```

It prints only the model's response on stdout, so it is a drop-in replacement for
`ollama run <model>` in a heredoc. Local inference has no dollar cost, so these entries are
counted as tokens and left out of spend on purpose.

## Configuration

`~/.config/redline/config.json`, written with defaults on first launch. Reloaded on every
poll, so edits apply without a restart. **Edit Config…** in the dropdown opens it.

| Key | Default | Meaning |
| --- | --- | --- |
| `pollIntervalSeconds` | 300 | Refresh interval, minimum 10 |
| `menuBarDisplay` | `limits` | `limits`, `cost`, `tokens`, `both`, `session` |
| `limitYellowPct` / `limitRedPct` | 60 / 85 | Color thresholds |
| `providers` | all three | Which sources to read |
| `useCLIToken` | `false` | Read the Claude CLI's Keychain token instead of signing in |
| `menuBarProvider` | `auto` | Which provider the menu bar reports: `auto`, `Claude`, `Codex`, `Ollama` |
| `pricingPerMTok` | see below | USD per million tokens, matched by substring of model name |
| `oauth.clientId` | empty | Required for the app's own Sign In |

Pricing keys match by substring, so `opus` covers `claude-opus-5`. A model with no matching
key is **counted but excluded from cost**, and the dropdown marks the total with `+`.
Guessing a price tier would silently misreport spend, so it does not.

## Development

```sh
make help       # list targets
make test       # 46 tests
make build      # release binary
make bundle     # dist/Redline.app, signed
make widget     # app + desktop widget via Xcode
make dmg        # dist/Redline-<version>.dmg
make uninstall  # remove app, LaunchAgent and Keychain token
```

Tests need XCTest, which ships with Xcode rather than the Command Line Tools.
`scripts/test.sh` finds a usable Xcode automatically, so no `sudo xcode-select` is needed.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the layout and
[docs/EXTENDING.md](docs/EXTENDING.md) for how to add a provider.

## Usage dashboard

**Open Usage Dashboard…** (⌘D) in the menu, or just double-click `Redline.app`, opens a native
window with charts built from the same data the menu bar summarises:

- a provider dropdown: all of them at once, or focus one
- tokens per day by provider, over 7, 14, or 30 days
- estimated cost per day
- the last 24 hours, hour by hour
- the model mix for the last 7 days, with unpriced models shown as `—` rather than `$0.00`
- current limit rails, each ending at its threshold

Focusing **Ollama** adds a control panel: server status and version, models loaded in memory
with their size and how much sits on the GPU, every downloaded model, and Start / Stop for
each. Stop unloads from memory with `keep_alive: 0`; it never deletes a download, and nothing
in Redline removes model weights.

History needs no extra recording: transcript entries already carry timestamps, so the range is
derived from files already on disk. A 30 day scan touches a lot of them, so it runs off the
main thread with a progress state.

## Desktop widget

`make widget` builds the app with a WidgetKit extension you can drop on the desktop or in
Notification Centre. It shows the worst session and week gauges, and on the large size the
token and cost totals.

The widget renders a snapshot the menu bar app publishes to a shared App Group container.
It never parses transcripts itself, because a widget process has a hard time and memory
budget. Two consequences worth knowing:

- **It is not live.** WidgetKit decides when to reload. The view labels itself stale when
  the snapshot is over 15 minutes old rather than implying the number is current.
- **If the menu bar app is not running, the snapshot goes stale** and the widget says so.

Install it with `WIDGET=1 make install`, then add Redline from the desktop widget gallery.

The widget is **configurable**: right-click it, choose **Edit Widget**, and pick a track. Add
it more than once to watch several at the same time, for example one for Claude, one for
Codex, and one for Ollama.

| Track | Shows |
| --- | --- |
| All providers | Session and week for whichever provider is nearest its limit, plus totals |
| Claude | Claude's own windows, tokens and estimated cost |
| Codex | Codex's own windows, tokens and estimated cost |
| Ollama | Server status and version, models loaded with how much sits on the GPU, models downloaded and their size on disk |

The large size adds a per-window breakdown. Ollama state travels in the snapshot rather than
being fetched by the widget, so the extension needs no network access of its own.

No Apple ID is needed to run it locally: the build ad-hoc signs the bundle with the App
Group entitlement, which macOS honours on this machine. Distributing the widget to anyone
else does require a Developer ID Application certificate, and the build switches to real
signing automatically once one exists.

## Site

`site/index.html` is a single self-contained page, deployed to GitHub Pages by
`.github/workflows/pages.yml` on any push that touches it.

Note that `redline.github.io` is not available to this project; that hostname would require
owning a GitHub account or org named `redline`. The project URL is
`https://goriparthi.github.io/redline/`. Pages also requires either a public repo or a paid
plan, so deployment stays off until one of those is true.

## Track badges

Each provider carries an original badge and colour, used identically in the menu, the dashboard
and the widgets: a ring and satellite for Claude (a hosted endpoint), chevrons for Codex
(source code), stacked bars for Ollama (weights held locally), and the Redline mark for all
providers at once.

These are deliberately **not** vendor logos. Anthropic, OpenAI and Ollama all restrict
third-party use of their marks, and a traced approximation would breach the requirement that a
mark be reproduced unaltered.

## Security

[SECURITY.md](SECURITY.md) lists every path Redline reads and writes, the three Anthropic
hosts it can reach, and how credentials are handled. In short: transcripts are parsed for
token counts and never copied or transmitted, there is no telemetry, and reading the Claude
CLI's Keychain token is opt-in.

## License

Code is MIT. See [LICENSE](LICENSE).

The contents of `brand/` (the mark, wordmark, app icon, and colour tokens) are original
artwork for this project and are **not** covered by the MIT grant. Fork the code freely; please
use your own name and mark rather than shipping something that looks like Redline.

Track badges in the UI are original iconography, deliberately not vendor logos. Anthropic,
OpenAI, and Ollama all restrict third-party use of their marks.
