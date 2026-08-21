#!/bin/bash
# Proves a built command line binary actually works: it reads a Claude transcript, writes the
# SQLite warehouse, and reads it back. Takes the path to the binary.
set -euo pipefail
BIN="${1:?usage: smoke-cli.sh <path-to-redline>}"
[[ -x "$BIN" ]] || { echo "not executable: $BIN" >&2; exit 1; }

HOME_DIR="$(mktemp -d)"
trap 'rm -rf "$HOME_DIR"' EXIT
mkdir -p "$HOME_DIR/.claude/projects/demo"
export REDLINE_HOME="$HOME_DIR"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Asserts on captured text rather than by piping into grep. Under pipefail, grep -q closes the
# pipe the moment it matches, the writer takes SIGPIPE, and the pipeline reports failure for a
# test that actually passed. It only shows up once the output is big enough to still be
# writing, which makes it the worst kind of flake.
contains() {
    [[ "$1" == *"$2"* ]] || fail "${3:-expected to find \"$2\"}"
}
# Runs the binary and checks the exit code, since the codes are part of the contract
run() {
    local want="$1"; shift
    local out; local code=0
    out="$("$BIN" "$@" 2>&1)" || code=$?
    [[ "$code" == "$want" ]] || fail "'$*' exited $code, wanted $want: $out"
    printf '%s' "$out"
}

echo "version"
contains "$(run 0 --version)" "redline " "--version printed nothing recognisable"

echo "help"
contains "$(run 0 help)" "redline <command>" "help lost its usage line"

echo "empty machine"
# 30 is "no data", and saying so beats inventing a zero
run 30 status >/dev/null

echo "ingest"
TS="$(date -u -v-30M +"%Y-%m-%dT%H:%M:%S.000Z" 2>/dev/null \
      || date -u -d '30 minutes ago' +"%Y-%m-%dT%H:%M:%S.000Z")"
printf '{"timestamp":"%s","requestId":"req_a","message":{"id":"a","model":"claude-sonnet-5","usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":0}}}\n' \
    "$TS" > "$HOME_DIR/.claude/projects/demo/session.jsonl"
contains "$(run 0 ingest --json)" '"added" : 1' "ingest did not record the transcript"

echo "the same transcript twice adds nothing"
contains "$(run 0 ingest --json)" '"added" : 0' "ingest was not incremental"

echo "autostart reports without changing anything"
# Status only. Turning it on here would edit whatever the machine actually starts at login,
# which a smoke test has no business doing; AutostartTests covers on and off against a
# scratch root.
AUTOSTART="$(run 0 autostart)"
[[ "$AUTOSTART" == *": on"* || "$AUTOSTART" == *": off"* ]] \
    || fail "autostart did not report a state: $AUTOSTART"

echo "the usage feed can be wired and unwired"
# A scratch home, so this never touches the real ~/.claude
contains "$(run 20 setup)" "off" "setup did not report a state"
contains "$(run 0 setup claude)" "on" "setup claude did not wire the feed"
contains "$(run 0 setup)" "on" "setup did not stay wired"
contains "$(run 0 setup off)" "off" "setup off did not unwire"

echo "history reads it back"
contains "$(run 0 history)" "1.1K" "history lost the 1100 tokens"

echo "OK: $BIN"
