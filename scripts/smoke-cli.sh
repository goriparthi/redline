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
# Runs the binary and checks the exit code, since the codes are part of the contract
run() {
    local want="$1"; shift
    local out; local code=0
    out="$("$BIN" "$@" 2>&1)" || code=$?
    [[ "$code" == "$want" ]] || fail "'$*' exited $code, wanted $want: $out"
    printf '%s' "$out"
}

echo "version"
run 0 --version | grep -q "^redline " || fail "--version printed nothing recognisable"

echo "help"
run 0 help | grep -q "redline <command>" || fail "help lost its usage line"

echo "empty machine"
# 30 is "no data", and saying so beats inventing a zero
run 30 status >/dev/null

echo "ingest"
TS="$(date -u -v-30M +"%Y-%m-%dT%H:%M:%S.000Z" 2>/dev/null \
      || date -u -d '30 minutes ago' +"%Y-%m-%dT%H:%M:%S.000Z")"
printf '{"timestamp":"%s","requestId":"req_a","message":{"id":"a","model":"claude-sonnet-5","usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":0}}}\n' \
    "$TS" > "$HOME_DIR/.claude/projects/demo/session.jsonl"
run 0 ingest --json | grep -q '"added" : 1' || fail "ingest did not record the transcript"

echo "the same transcript twice adds nothing"
run 0 ingest --json | grep -q '"added" : 0' || fail "ingest was not incremental"

echo "history reads it back"
run 0 history | grep -q "1.1K" || fail "history lost the 1100 tokens"

echo "OK: $BIN"
