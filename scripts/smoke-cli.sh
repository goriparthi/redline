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
contains "$(run 0 autostart --json)" '"outcome" : "status"' \
    "autostart --json did not report the state"

echo "settings can be read and changed, and nonsense refused"
contains "$(run 0 config)" "limitRedPct" "config did not list the settings"
contains "$(run 0 config limitRedPct 90)" "-> 90" "config did not change the value"
contains "$(run 0 config limitRedPct)" "90" "the change did not stick"
# 2 is the refusal, and it has to refuse: the engine would not load this either
run 2 config limitRedPct 500 >/dev/null

# The shape a shell reads, which is the only thing the Windows settings page has to go on
contains "$(run 0 config --json)" '"kind" : "number"' "config --json lost the control kinds"
contains "$(run 0 config limitRedPct 80 --json)" '"outcome" : "changed"' \
    "config --json did not report the change"
contains "$(run 2 config limitRedPct 500 --json)" '"outcome" : "rejected"' \
    "config --json did not report the refusal on stdout"

echo "the usage feed can be wired and unwired"
# A scratch home, so this never touches the real ~/.claude
contains "$(run 20 setup)" "off" "setup did not report a state"
# Off is an answer, and it comes back as one rather than as an exit code to interpret
contains "$(run 20 setup --json)" '"on" : false' "setup --json did not report the state"
contains "$(run 0 setup claude)" "on" "setup claude did not wire the feed"
contains "$(run 0 setup)" "on" "setup did not stay wired"
contains "$(run 0 setup off)" "off" "setup off did not unwire"
contains "$(run 0 setup off --json)" '"outcome" : "unchanged"' \
    "setup --json called a second off a change"

echo "history reads it back"
contains "$(run 0 history)" "1.1K" "history lost the 1100 tokens"

echo "OK: $BIN"
