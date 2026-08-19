#!/bin/bash
# End to end tests: real fixtures on disk, the real binary, real exit codes.
#
# The unit tests prove the pieces. This proves the product: transcripts land in a home
# directory, the tool reads them, the store answers, and the numbers survive the transcripts
# being deleted. Everything runs against REDLINE_HOME so nothing here can see or touch the
# machine's own usage data.
#
# Usage: scripts/e2e.sh [path-to-binary]   (defaults to the release build)
source "$(dirname "$0")/lib/common.sh"

BIN="${1:-$BUILD_DIR/release/$BIN_NAME}"
if [[ ! -x "$BIN" ]]; then
    info "No binary at $BIN; building"
    "$REPO_ROOT/scripts/build.sh" >/dev/null
fi
[[ -x "$BIN" ]] || die "no binary to test at $BIN"

E2E_HOME="$(mktemp -d "${TMPDIR:-/tmp}/redline-e2e.XXXXXX")"
export REDLINE_HOME="$E2E_HOME"
trap 'rm -rf "$E2E_HOME"' EXIT

PASS=0
FAIL=0

ok()   { printf '  \033[1;32mok\033[0m   %s\n' "$*"; PASS=$((PASS + 1)); }
bad()  { printf '  \033[1;31mFAIL\033[0m %s\n' "$*" >&2; FAIL=$((FAIL + 1)); }

# Runs the tool and checks its exit code. Output lands in $OUT for further assertions.
run() {
    local expected="$1"; shift
    set +e
    OUT="$("$BIN" "$@" 2>&1)"
    local code=$?
    set -e
    if [[ "$code" == "$expected" ]]; then
        ok "$* exits $code"
    else
        bad "$* exits $code, expected $expected"
        printf '%s\n' "$OUT" | sed 's/^/       /' >&2
    fi
}

contains() {
    if printf '%s' "$OUT" | grep -qF "$1"; then ok "output contains \"$1\""
    else
        bad "output missing \"$1\""
        printf '%s\n' "$OUT" | sed 's/^/       /' >&2
    fi
}

missing() {
    if printf '%s' "$OUT" | grep -qF "$1"; then bad "output should not contain \"$1\""
    else ok "output omits \"$1\""; fi
}

json_field() {
    local field="$1" expected="$2"
    local actual
    actual="$(printf '%s' "$OUT" | /usr/bin/python3 -c \
        "import json,sys; print(json.load(sys.stdin).get('$field'))" 2>/dev/null || echo "<unparsable>")"
    if [[ "$actual" == "$expected" ]]; then ok "json $field is $expected"
    else bad "json $field is $actual, expected $expected"; fi
}

# Fixed so a laptop and a CI runner bucket days identically. history rolls up by UTC day
# while cadence counts local days, so a machine east or west of UTC could pass one and fail
# the other on the same fixtures.
export TZ=UTC

# Timestamps are generated relative to now so the cadence and history windows see them as
# recent, and scaled so they cannot reach back into yesterday.
#
# The assertions below expect exactly one active day. A fixture written "3 hours ago" lands
# in yesterday whenever the run starts less than 3 hours into the UTC day, which failed CI
# every night between 00:00 and 03:00Z. Offsets are therefore expressed in minutes and
# squeezed into whatever part of today has already elapsed; a run just after midnight gets a
# tighter spread rather than a fixture in the wrong day.
# One anchor for the whole run, not a fresh clock read per fixture: a suite that takes a
# minute would otherwise straddle midnight if it started at 23:59.
FIXTURE_SPAN=180
NOW_EPOCH=$(/bin/date -u +%s)
DAY_MINUTES=$(( 10#$(/bin/date -u -r "$NOW_EPOCH" +%H) * 60 + 10#$(/bin/date -u -r "$NOW_EPOCH" +%M) ))
# Never reach further back than the day is old. A minute of headroom where there is one, so
# nothing lands exactly on the boundary.
USABLE=$(( DAY_MINUTES > 1 ? DAY_MINUTES - 1 : DAY_MINUTES ))
if (( USABLE > FIXTURE_SPAN )); then USABLE=$FIXTURE_SPAN; fi

# iso <minutes-ago>, scaled. Order is preserved; only the spread shrinks.
iso() {
    /bin/date -u -r $(( NOW_EPOCH - ($1 * USABLE / FIXTURE_SPAN) * 60 )) \
        +"%Y-%m-%dT%H:%M:%S.000Z"
}

claude_line() {
    local id="$1" ts="$2" input="${3:-1000}" output="${4:-100}"
    printf '{"timestamp":"%s","requestId":"req_%s","message":{"id":"%s","model":"claude-sonnet-5","usage":{"input_tokens":%s,"output_tokens":%s,"cache_read_input_tokens":0}}}\n' \
        "$ts" "$id" "$id" "$input" "$output"
}

codex_line() {
    local ts="$1" used="$2"
    printf '{"timestamp":"%s","payload":{"type":"token_count","model":"gpt-5","rate_limits":{"primary":{"used_percent":%s,"window_minutes":300,"resets_in_seconds":7200}},"info":{"last_token_usage":{"input_tokens":500,"cached_input_tokens":100,"output_tokens":50,"reasoning_output_tokens":10}}}}\n' \
        "$ts" "$used"
}

CLAUDE_DIR="$E2E_HOME/.claude/projects/demo"
CODEX_DIR="$E2E_HOME/.codex/sessions/2026/08/18"
DATA_DIR="$E2E_HOME/.local/share/redline"
mkdir -p "$CLAUDE_DIR" "$CODEX_DIR" "$DATA_DIR" "$E2E_HOME/.config/redline"

info "End to end against $BIN"
info "REDLINE_HOME=$E2E_HOME"

# ---------------------------------------------------------------- an empty machine

echo "empty machine"
run 0 help
contains "redline <command>"
# Nothing has ever been recorded, and every command must say so rather than inventing a zero
run 30 status
run 20 history
run 20 cadence

# ---------------------------------------------------------------- first ingest

echo
echo "first ingest"
{
    claude_line a "$(iso 180)"
    claude_line b "$(iso 120)" 2000 200
} > "$CLAUDE_DIR/session.jsonl"
codex_line "$(iso 60)" 42 > "$CODEX_DIR/rollout.jsonl"

run 0 ingest --json
json_field added 3
json_field records 3

# The same transcripts a second time say nothing new. This is the property the whole
# incremental design exists for.
run 0 ingest --json
json_field added 0
json_field records 3

# ---------------------------------------------------------------- what the store answers

echo
echo "queries"
run 0 history
# Claude 1100 + 2200, plus Codex 400 fresh input and 60 output on the same UTC day. Read
# from --json rather than the table, which formats to "3.8K" and would pass on any number
# that happened to round the same way.
run 0 history --json
json_field tokens 3760
run 0 history --csv
contains "day,provider,model,input,output"
contains "Claude,claude-sonnet-5"
run 0 history --json
json_field days 1

run 0 cadence
contains "current run"
contains "days running  1"
run 0 cadence --json
json_field active_days 1
json_field records 3

# ---------------------------------------------------------------- appending

echo
echo "appending"
claude_line c "$(iso 10)" 500 50 >> "$CLAUDE_DIR/session.jsonl"
run 0 ingest --json
json_field added 1
json_field records 4

# A line being written right now ends mid record. It must not be parsed until it is whole.
printf '{"timestamp":"%s","requestId":"req_partial","message":{"id":"partial","model":"claude-sonnet-5","usage":{"input_tok' \
    "$(iso 1)" >> "$CLAUDE_DIR/session.jsonl"
run 0 ingest --json
json_field added 0
printf 'ens":900,"output_tokens":90,"cache_read_input_tokens":0}}}\n' >> "$CLAUDE_DIR/session.jsonl"
run 0 ingest --json
json_field added 1
json_field records 5

# ---------------------------------------------------------------- the promise

echo
echo "history outlives the transcripts"
BEFORE="$("$BIN" history --json)"
rm -rf "$E2E_HOME/.claude/projects"
run 0 ingest --json
json_field added 0
run 0 history --json
AFTER="$OUT"
BEFORE_TOKENS="$(printf '%s' "$BEFORE" | /usr/bin/python3 -c 'import json,sys; print(json.load(sys.stdin)["tokens"])')"
AFTER_TOKENS="$(printf '%s' "$AFTER" | /usr/bin/python3 -c 'import json,sys; print(json.load(sys.stdin)["tokens"])')"
if [[ "$BEFORE_TOKENS" == "$AFTER_TOKENS" && "$AFTER_TOKENS" != "0" ]]; then
    ok "tokens survive the transcripts being deleted ($AFTER_TOKENS)"
else
    bad "tokens changed when transcripts were removed: $BEFORE_TOKENS then $AFTER_TOKENS"
fi

# ---------------------------------------------------------------- settings are obeyed

echo
echo "settings"
cat > "$E2E_HOME/.config/redline/config.json" <<'JSON'
{ "recordHistory": false, "providers": ["Claude", "Codex", "Ollama"] }
JSON
run 30 ingest
contains "Keep Local History is off"
rm "$E2E_HOME/.config/redline/config.json"

# A provider left out of the list is not read at all
cat > "$E2E_HOME/.config/redline/config.json" <<'JSON'
{ "providers": ["Ollama"] }
JSON
mkdir -p "$CLAUDE_DIR"
claude_line z "$(iso 5)" 7000 700 > "$CLAUDE_DIR/second.jsonl"
run 0 ingest --json
json_field added 0
rm "$E2E_HOME/.config/redline/config.json"

# ---------------------------------------------------------------- the sidecar and status

echo
echo "published files"
run 0 ingest --json
# No app is running here, so nothing has published a reading. Usage records existing is not
# the same as knowing a percentage, and status says so rather than implying one from tokens.
run 30 status
contains "No reading available"

# A published snapshot is read back, and only from inside this profile.
mkdir -p "$DATA_DIR"
cat > "$DATA_DIR/snapshot.json" <<'JSON'
{
  "updatedAt": "2026-08-18T12:00:00Z",
  "limits": [{"provider":"Claude","key":"five_hour","utilization":91,
              "resetsAt":"2099-01-01T00:00:00Z"}],
  "today": {"io": 1000, "cost": 0.5, "cacheRead": 0, "cacheWrite": 0, "hasUnpriced": false},
  "week": {"io": 5000, "cost": 2.5, "cacheRead": 0, "cacheWrite": 0, "hasUnpriced": false}
}
JSON
# 91% is past the red threshold, so the exit code has to say "near a limit" without anyone
# reading the prose.
run 10 status
contains "91%"

echo
if (( FAIL )); then
    die "$FAIL failed, $PASS passed"
fi
info "$PASS checks passed"
