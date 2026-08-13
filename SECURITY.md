# Security

RedLine reads local files and, optionally, one Anthropic endpoint. This document states
exactly what it touches so you can decide whether to trust it, and how to report a problem.

## What it reads

| Path | Why | Contains |
| --- | --- | --- |
| `~/.claude/projects/**/*.jsonl` | token and cost totals | your Claude Code transcripts |
| `~/.codex/sessions/**/*.jsonl` | limit windows and token counts | your Codex transcripts |
| `~/.local/share/redline/ollama.jsonl` | Ollama volume | counts written by the ollama shim |
| `~/.config/redline/config.json` | settings | no credentials |
| Keychain item `redline` | its own OAuth token, if you sign in | access and refresh token |
| Keychain item `Claude Code-credentials` | **only if you set `useCLIToken: true`** | the Claude CLI's token |

Transcripts contain your prompts and model output. RedLine parses them for `usage` fields
and **never stores, copies, or transmits their content**. Only counts reach the snapshot.

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

## The undocumented endpoint

`https://api.anthropic.com/api/oauth/usage` is not a published API. It is what the Claude Code
CLI uses internally for its own limit display. RedLine calls it only when you enable Claude
rate limits, and only with a token you already hold.

There is no published permission for third-party use, so it may fall outside Anthropic's terms.
That is a decision for the person running the software, and the README states it plainly before
install. Everything else RedLine shows is derived from local files.

## Where it connects

Three hosts, all Anthropic's, and only when Claude rate limits are enabled:

- `https://claude.ai/oauth/authorize` — sign-in, in your browser
- `https://console.anthropic.com/v1/oauth/token` — token exchange and refresh
- `https://api.anthropic.com/api/oauth/usage` — rate-limit percentages

There is no analytics, telemetry, crash reporting, or update check. Codex and Ollama are
read entirely from disk and make no network calls. Config overrides for these URLs are
rejected unless they are `https`.

## Credentials

- The app's own token lives in the Keychain (`kSecAttrAccessibleWhenUnlocked`), never in a
  file or environment variable.
- Sign-in uses OAuth 2.0 with PKCE (`S256`). The `state` parameter is verified on the
  callback, and the loopback listener binds `127.0.0.1` only, so it is unreachable from the
  network.
- **`useCLIToken` is off by default.** Turning it on lets RedLine read the Claude CLI's
  Keychain item, which is another application's credential. macOS requires you to grant
  that access explicitly, once. RedLine uses the token only as a bearer token against the
  usage endpoint above; it never copies it anywhere.
- No `client_id` ships with the app. Sign-in stays disabled until you set one, because this
  project is not registered with Anthropic.

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
  processes running as your user.

## Distribution

Releases are ad-hoc signed and **not notarized**, so macOS quarantines downloads. Verify
what you run:

```sh
codesign -dv --verbose=2 /Applications/Redline.app
shasum -a 256 Redline-<version>.dmg   # compare with the release notes
```

Building from source is the strongest option, and it is two commands.

## Reporting

Open an issue at https://github.com/goriparthi/redline/issues. For anything you would rather
not post publicly, say so in the issue without details and a private channel will be
arranged. There is no bounty; this is a personal project.
