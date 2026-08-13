# RedLine

**Know your limit.**

RedLine is a private macOS usage monitor for Claude, Codex, and Ollama. It shows what has
been used, what remains, and when limits reset, in the menu bar and an optional desktop
widget.

Brand assets, tokens, and voice rules live in [brand/](brand/); `brand/BRAND.md` is the
source of truth for colour, type, and copy. Note that it explicitly rules out racing
metaphors and fear language, so keep the wording calm and factual.

> **Not affiliated with, endorsed by, or supported by Anthropic, OpenAI, or Ollama.**

## Read this before you install

RedLine's **token and cost totals** come from transcript files those tools already write to
your disk. That part uses no API and no credentials.

RedLine's **Claude rate-limit percentages** are different, and you should understand what you
are opting into:

- They come from `https://api.anthropic.com/api/oauth/usage`, which is **not a documented or
  published API**. It is an internal endpoint the Claude Code CLI uses for itself.
- Because it is undocumented, **there is no published permission to use it**. Doing so may fall
  outside Anthropic's Terms of Service or acceptable use policy. Nobody here has a ruling
  either way, and this project is not in a position to give you one.
- Reading the Claude CLI's stored OAuth token (`useCLIToken`, off by default) means using
  **your own credential** in a way Anthropic has not documented or endorsed.
- The endpoint can change, start refusing requests, or disappear without notice. It already
  rate-limits aggressively, and RedLine backs off when it does.

**You are responsible for deciding whether that is acceptable for your account.** If you would
rather not touch it at all, RedLine is still useful: leave `useCLIToken` false and
`oauth.clientId` empty, which is the shipped default. In that state RedLine makes **no network
requests whatsoever** and still reports tokens, cost, model mix, and history for Claude, plus
everything for Codex and Ollama. You lose only the Claude percentages.

Codex and Ollama involve no such question. Codex is read entirely from local files, and Ollama
is your own machine.

If Anthropic publishes a real usage API, this all becomes moot and RedLine will move to it.

## Screenshots

| Menu bar | Dashboard |
| --- | --- |
| <img src="site/img/menubar.png" alt="Menu bar dropdown showing Claude and Codex limits and a per provider usage table" width="420"> | <img src="site/img/dashboard.png" alt="Dashboard window with limit rails and tokens per day chart" width="420"> |

Widgets, one per track, configurable from the widget's own settings:

| Claude | Codex | Ollama |
| --- | --- | --- |
| <img src="site/img/claude-widget.png" alt="Claude widget showing session and week percentages" width="230"> | <img src="site/img/codex-widget.png" alt="Codex widget showing week percentage" width="230"> | <img src="site/img/ollama-widget.png" alt="Ollama widget showing loaded and downloaded models" width="230"> |

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

Three routes. **Building from source is the one to prefer**, and the reason is below.

### 1. Homebrew, from this repo

```sh
git clone https://github.com/goriparthi/redline.git
cd redline
brew install --cask ./Casks/redline.rb
```

### 2. From source (recommended)

```sh
git clone https://github.com/goriparthi/redline.git
cd redline
make install            # menu bar app
WIDGET=1 make install   # menu bar app and the desktop widget
```

Needs Xcode for the widget; the menu bar app alone builds with the Command Line Tools. You
end up running a binary you compiled from source you can read, which is the whole point.

### 3. Download the DMG

[Latest release](https://github.com/goriparthi/redline/releases/latest) → open the DMG → drag
**Redline.app** to Applications → launch it. It opens normally: releases are **signed with a
Developer ID certificate and notarized by Apple**, so Gatekeeper lets them through without any
workaround.

Verifying is still worth a moment, and here it actually tells you something:

```sh
spctl -a -vvv -t install /Applications/Redline.app
#   expect: accepted, source=Notarized Developer ID

codesign -dv --verbose=2 /Applications/Redline.app
#   expect: Authority=Developer ID Application: Prashanth Goriparthi (QX3NQYWX6F)
```

If macOS ever *does* block a build of this app, treat that as a signal something is wrong with
the download rather than something to work around. The command that strips the quarantine flag
is easy to find, and it is the same step malware distributors ask victims to perform, so
reserve it for software you have actually verified. Building from source remains the only route
where you can read what you are about to run.

### Which providers to read

On first launch RedLine asks which of Claude, Codex, and Ollama to read, listing only what it
finds installed. Change it later from **Choose Providers…** in the menu.

RedLine works with any one of them. With a single tool installed the provider pickers
disappear, since there is nothing to choose between.

### Updates

**Check for Updates…** in the menu compares your version against the latest release. It runs
only when you ask, because a background poll would break the promise that RedLine makes no
network requests unless you opt into Claude limits.

Installed with Homebrew? `brew upgrade --cask redline`. Built from source? `git pull &&
make install`.

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
through the wrapper and it records each one:

```sh
ollama-run.sh qwen3-coder:30b <<'PROMPT'
Summarize this build log.
PROMPT
```

It prints only the model's response on stdout, so it is a drop-in replacement for
`ollama run <model>` in a heredoc. Local inference has no dollar cost, so these entries are
counted as tokens and left out of spend on purpose.

The wrapper ships inside the app, so a DMG or Homebrew install has it too. **Install Ollama
Wrapper…** in the dropdown copies it to `~/.local/bin/ollama-run.sh` and tells you if that
directory is not on your `PATH`. The menu item appears only when Ollama is installed, and
re-running it updates an older copy. From a clone, `scripts/ollama-run.sh` works directly,
and the copy inside an installed app is at
`/Applications/Redline.app/Contents/Resources/ollama-run.sh`.

### Tell your coding agent about it

Coding agents reach for `ollama run` by default, and those calls are never counted. Paste
this into whatever instruction file your agent reads (`CLAUDE.md`, `AGENTS.md`, a skill, a
rules file) and its local calls start showing up in RedLine:

```text
Route every local Ollama call through the RedLine wrapper, never `ollama run`.
It records the usage and prints only the model's response, so it is a drop-in
replacement:

~/.local/bin/ollama-run.sh qwen3-coder:30b <<'PROMPT'
Summarize this build log.
PROMPT

If the wrapper is missing, say so rather than falling back to `ollama run`.
Install it from the RedLine menu with "Install Ollama Wrapper".
```

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

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the layout,
[docs/EXTENDING.md](docs/EXTENDING.md) for how to add a provider, and
[docs/SIGNING.md](docs/SIGNING.md) for signing and notarizing a release.

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
in RedLine removes model weights.

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

Install it with `WIDGET=1 make install`, then add RedLine from the desktop widget gallery.

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

The project homepage is at https://goriparthi.github.io/redline/.

## Track badges

Each provider carries an original badge and colour, used identically in the menu, the dashboard
and the widgets: a ring and satellite for Claude (a hosted endpoint), chevrons for Codex
(source code), stacked bars for Ollama (weights held locally), and the RedLine mark for all
providers at once.

These are deliberately **not** vendor logos. Anthropic, OpenAI and Ollama all restrict
third-party use of their marks, and a traced approximation would breach the requirement that a
mark be reproduced unaltered.

## Security

[SECURITY.md](SECURITY.md) lists every path RedLine reads and writes, the three Anthropic
hosts it can reach, and how credentials are handled. In short: transcripts are parsed for
token counts and never copied or transmitted, there is no telemetry, and reading the Claude
CLI's Keychain token is opt-in.

## Disclaimer

There is no Terms of Service here, and none is needed: RedLine is not a service. Nothing is
hosted, no account is created, and no data leaves your Mac except the optional Claude
rate-limit call. The **MIT licence is the governing document**, and it already disclaims
warranty and liability in the usual terms.

In plain language:

- **No warranty.** This is provided as-is. If it misreports something, you carry the outcome.
- **Costs are estimates**, computed from a pricing table you can edit. They are not a bill and
  will not match your invoice. Treat them as a signal, not an accounting record.
- **Your account is your responsibility.** Claude rate-limit percentages come from an
  undocumented endpoint, described at the top of this file. Whether using it is acceptable
  under Anthropic's terms is a decision only you can make for your own account.
- **Built with heavy AI assistance.** Most of this code was written by an AI agent working
  with the author. It is reviewed, tested, and shipped deliberately, and the author is
  responsible for what it does. AI involvement is disclosed because you deserve to know how
  something you run was made, not as an excuse: "an AI wrote it" would not transfer risk to
  you, and the MIT warranty disclaimer is what actually governs liability.

If you find something wrong, please open an issue.

## License

Code is MIT. See [LICENSE](LICENSE).

The contents of `brand/` (the mark, wordmark, app icon, and colour tokens) are original
artwork for this project and are **not** covered by the MIT grant. Fork the code freely; please
use your own name and mark rather than shipping something that looks like RedLine.

Track badges in the UI are original iconography, deliberately not vendor logos. Anthropic,
OpenAI, and Ollama all restrict third-party use of their marks.
