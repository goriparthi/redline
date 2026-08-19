# Security

RedLine reads local files and, optionally, one Anthropic endpoint. This document states
exactly what it touches so you can decide whether to trust it, and how to report a problem.

## What it reads

| Path | Why | Contains |
| --- | --- | --- |
| `~/.claude/projects/**/*.jsonl` | token and cost totals | your Claude Code transcripts |
| `~/.codex/sessions/**/*.jsonl` | limit windows and token counts | your Codex transcripts |
| `~/.local/share/redline/ollama.jsonl` | Ollama volume | counts written by the ollama shim |
| `~/.local/share/redline/claude-usage.json` | Claude limit windows | percentages and reset times, written by the usage feed |
| `~/.config/redline/config.json` | settings | no credentials |
| `~/.claude/settings.json` | to find and preserve an existing statusline | your Claude Code settings |
| `~/.claude/.credentials.json` | **only if you set `useCLIToken: true`** | the Claude CLI's token, on installs that keep it in a file |
| `~/.claude.json` | which MCP servers are configured, for the findings checks | server names only, never their arguments or environment |
| `~/.claude/skills`, `agents`, `commands` | which are defined, for the findings checks | names only |
| `~/.claude/CLAUDE.md` and a project's own `CLAUDE.md` | their size, for the findings checks | measured in characters; the text is never copied or transmitted |
| whatever `externalUsagePath` points at | another tool's usage sidecar, when you set one | limit percentages and reset times |
| Keychain item `redline` | its own OAuth token, if you sign in | access and refresh token |
| Keychain item `Claude Code-credentials` | **only if you set `useCLIToken: true`** | the Claude CLI's token |

Transcripts contain your prompts and model output. RedLine parses them for `usage` fields
and **never stores, copies, or transmits their content**. Only counts reach the snapshot.

## The Claude usage feed

Claude Code passes a JSON payload to whatever `statusLine` command is configured, and that
payload already carries the rate-limit windows. **Set Up Claude Tracking** points that
setting at `~/.local/share/redline/claude-statusline.sh`, which files those windows where
RedLine can read them. This path needs no token, no Keychain access, and no network request.

The payload also carries your working directory, session id, transcript path, model name and
cost figures. **The feed writes only the `rate_limits` block.** Everything else is read from
stdin and discarded. The file it writes is `0600` and holds percentages and reset times only.

It composes rather than replaces. Any statusline command already configured is carried in
`REDLINE_STATUSLINE_CHAIN`, still receives the untouched payload, and still draws your line.
Note that the chained command is run through `sh -c`, so treat that setting as you would any
other shell command in your own config: it is whatever was already in your `settings.json`,
and RedLine never invents one.

`~/.claude/settings.json` is a file another application owns, so RedLine first writes to it
only when you choose that menu item. After that one consent, each poll re-checks the wiring
and restores the `statusLine` entry if something else overwrote it, because a Claude Code
session started before the install holds the old settings in memory and writes them back on
any settings change, silently unwiring the feed. The wrapper script on disk is the marker of
that consent: uninstalling removes it, and with it the repair. Every write goes through a
temporary file and an atomic replace, because a half-written `settings.json` would break
every Claude Code session on the machine.

## What it writes

| Path | Contents | Mode |
| --- | --- | --- |
| `~/.local/share/redline/snapshot.json` | percentages, token counts, cost estimate | `0600` |
| `~/Library/Containers/com.goriparthi.redline.widget/…/redline/snapshot.json` | the same snapshot, so the sandboxed widget can read it | `0600` |
| `~/Library/Group Containers/group.com.goriparthi.redline/snapshot.json` | the same snapshot, when an App Group resolves | `0600` |
| `~/.local/share/redline/ollama.jsonl` | per-call token counts and timings | `0600` |
| `~/.config/redline/config.json` | your settings | default |
| `~/Library/Logs/redline.{log,err}` | stdout and stderr, normally empty | default |
| `~/.local/bin/ollama` | the ollama shim, written only when you run **Set Up Ollama Tracking** and never overwriting a file that is not RedLine's | `0755` |
| `~/.local/share/redline/claude-statusline.sh` | the usage feed, written only when you run **Set Up Claude Tracking** | `0755` |
| `~/.local/share/redline/claude-usage.json` | Claude limit percentages and reset times, written by that feed | `0600` |
| `~/.claude/settings.json` | its `statusLine` entry only, and only on that same explicit action, preserving any command already there | unchanged |
| `~/.local/share/redline/history/daily.jsonl` | a daily rollup by provider and model, so history outlives Claude Code's own cleanup | `0600` |
| `~/.local/share/redline/history/limits.jsonl` | limit readings, kept 60 days, used for burn rate | `0600` |
| `~/.local/share/redline/usage-snapshot.json` | the current windows, for other local tools to read; removed when you turn the setting off | `0600` |
| `~/.local/share/redline/alerts.json` | which notifications have already been posted | `0600` |

## The undocumented endpoint

`https://api.anthropic.com/api/oauth/usage` is not a published API. It is what the Claude Code
CLI uses internally for its own limit display. RedLine calls it only when you enable Claude
rate limits, only with a token you already hold, and only when the usage feed is not already
supplying the same windows.

There is no published permission for third-party use, so it may fall outside Anthropic's terms.
That is a decision for the person running the software, and the README states it plainly before
install. Everything else RedLine shows is derived from local files.

**The usage feed is the way to avoid this question entirely.** It reads the same windows from
the payload Claude Code hands its own statusline command, which is a documented extension
point rather than an undocumented endpoint. With the feed installed and reporting, RedLine
makes no request to Anthropic at all.

## Where it connects

Three hosts, all Anthropic's, and only when Claude rate limits are enabled **and** the usage
feed above is not already supplying the windows:

- `https://claude.ai/oauth/authorize`, sign-in, in your browser
- `https://console.anthropic.com/v1/oauth/token`, token exchange and refresh of RedLine's
  own grant, never of a borrowed one
- `https://api.anthropic.com/api/oauth/usage`, rate-limit percentages

One more host is reached on RedLine's own initiative:

- `https://api.github.com`, once a day, to ask whether a newer release exists. It sends no
  identifying information beyond an ordinary HTTPS request, and `autoCheckUpdates: false`
  stops it. **This is the only request RedLine makes without being asked.**

There is no analytics, telemetry, or crash reporting. Codex and Ollama are read entirely
from disk and make no network calls. Config overrides for these URLs are rejected unless
they are `https`.

Nothing added in 0.6.0 connects: local history, the published sidecar, the findings checks,
the command line tool and the notifications are all files on this machine.

## Credentials

- The app's own token lives in the Keychain (`kSecAttrAccessibleWhenUnlocked`), never in a
  file or environment variable.
- Sign-in uses OAuth 2.0 with PKCE (`S256`). The `state` parameter is verified on the
  callback, and the loopback listener binds `127.0.0.1` only, so it is unreachable from the
  network.
- **`useCLIToken` is off by default.** Turning it on lets RedLine read the Claude CLI's
  credential, which belongs to another application. RedLine uses the token as a bearer token
  against the usage endpoint above and never copies it anywhere.
- Reading that credential is tried three ways, least intrusive first: the file at
  `~/.claude/.credentials.json`, then `/usr/bin/security`, then the Keychain API. The
  `security` step is a subprocess spawn of an Apple-signed binary, used because the item's
  `apple-tool:` partition already trusts it, so it usually reads with no consent prompt at
  all; the API call, the one path that prompts, is last. Each read reports whether the item
  was absent, refused, or unreadable, so a locked Keychain is retried and only a signed-out
  CLI is treated as final.
- **A borrowed token is read, never refreshed.** When it has expired, RedLine runs
  `claude auth status` so Claude Code renews its own credential, which keeps one refresh
  chain and cannot disturb your CLI login. That is the whole recovery. An earlier build went
  one step further and exchanged the CLI's refresh token itself ("minting"); Anthropic
  rotates refresh tokens on use, so every mint left Claude Code holding a consumed token and
  forced a fresh `/login`. That code is deleted, not just disabled: nothing in RedLine can
  spend a credential another tool depends on. When the CLI will not renew, RedLine shows the
  last reading drained to grey with its timestamp rather than a number pretending to be live.
- The usage feed avoids all of the above. If it is supplying the windows, none of these token
  paths run at all.
- No `client_id` for RedLine's own sign-in ships with the app. That sign-in stays disabled
  until you set one, because this project is not registered with Anthropic. The Claude Code
  `client_id` used for the renewal above is a public identifier rather than a secret, and
  `REDLINE_CLAUDE_CLIENT_ID` overrides it.

## Deliberately not done

- **No sandbox on the app.** Reading `~/.claude` and `~/.codex` is the whole function, and a
  sandboxed app cannot do it without prompting you to pick folders.
- **The widget extension is sandboxed**, as macOS requires, and sees only the snapshot. App
  Group containers resolve only for code signed with a real Team ID, so on an ad-hoc build the
  app writes a copy of the snapshot into the widget's own container, which a sandboxed process
  can always read. No path exceptions are granted. With a Developer ID cert the App Group is
  used instead.
- **No prompt content in any log.** The Ollama shim passes your prompt to `curl` on stdin
  rather than as an argument or environment variable, because both are readable by other
  processes running as your user. The usage feed reads the statusline payload on stdin for
  the same reason, and writes only the rate-limit block.
- **The findings checks measure, they do not copy.** They count tool calls, read the size of
  your memory files, and list the names of configured servers, skills and commands. No file
  content is copied, stored or transmitted, and the report itself is held in memory rather
  than written to disk.
- **Notifications carry percentages, not work.** An alert says which window, what percentage
  and when it resets. Nothing about what you were doing goes into it, and none of it leaves
  the machine.
- **No writing back into another tool's credential.** RedLine reads
  `Claude Code-credentials` and never updates it, even when doing so would keep its own
  borrowed token alive longer.
- **Two subprocesses, both explicit.** `/usr/bin/security` reads the Keychain item, and
  `claude auth status` asks Claude Code to renew its own token. Neither is given your prompt,
  your transcripts, or any argument derived from them. The second is single-flight and rate
  limited so a stale token cannot spawn one process per poll.

## Distribution

Releases are signed with a Developer ID Application certificate and **notarized**;
`scripts/release.sh` refuses to publish a DMG that fails `spctl`, and the in-place updater
refuses to install one. Verify what you run:

```sh
codesign -dv --verbose=2 /Applications/Redline.app
spctl -a -t install Redline-<version>.dmg
shasum -a 256 Redline-<version>.dmg   # compare with the release notes
```

Building from source is the strongest option, and it is two commands.

## Reporting

Open an issue at https://github.com/goriparthi/redline/issues. For anything you would rather
not post publicly, say so in the issue without details and a private channel will be
arranged. There is no bounty; this is a personal project.
