#!/bin/bash
# RedLine Claude usage feed. Claude Code hands its statusline command a JSON payload on stdin,
# and that payload already carries the rate-limit windows RedLine shows. This writes just those
# to a sidecar file. No token, no Keychain, no network, nothing undocumented.
#
# Installed by "Set Up Claude Tracking" in the RedLine menu, which points the `statusLine`
# entry in ~/.claude/settings.json at this file.
#
# Composes rather than replaces: whatever statusline command was already configured is carried
# in REDLINE_STATUSLINE_CHAIN and still receives the same stdin and still draws the line. With
# no chain configured this prints nothing, which is what an unset statusLine already showed.
#
# Privacy: only the rate-limit block is written. The cwd, session id, transcript path and cost
# figures in the payload are read and discarded. See SECURITY.md.
set -uo pipefail

OUT="${REDLINE_CLAUDE_USAGE:-$HOME/.local/share/redline/claude-usage.json}"
CHAIN="${REDLINE_STATUSLINE_CHAIN:-}"

payload="$(cat)"

# The statusline must draw even when the sidecar cannot be written, so every failure here is
# swallowed deliberately. A missing usage figure in a menu bar is not worth a broken prompt.
write_sidecar() {
    local dir tmp filter
    dir="$(dirname "$OUT")"
    mkdir -p "$dir" 2>/dev/null || return 0
    tmp="$OUT.$$.tmp"

    # Absent rate_limits means this build of Claude Code did not send them, or there are none
    # yet this session. Keeping the previous file beats overwriting a real reading with nulls.
    filter='if .rate_limits then {
        updated_at: (now | todate),
        five_hour: (.rate_limits.five_hour // null),
        seven_day: (.rate_limits.seven_day // null),
        model_scoped: (.rate_limits.model_scoped // null)
    } else empty end'

    if command -v jq >/dev/null 2>&1; then
        printf '%s' "$payload" | jq -c "$filter" >"$tmp" 2>/dev/null || { rm -f "$tmp"; return 0; }
    else
        printf '%s' "$payload" | python3 -c '
import json, sys, time
try:
    p = json.load(sys.stdin)
except Exception:
    sys.exit(1)
r = p.get("rate_limits")
if not r:
    sys.exit(1)
json.dump({
    "updated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "five_hour": r.get("five_hour"),
    "seven_day": r.get("seven_day"),
    "model_scoped": r.get("model_scoped"),
}, sys.stdout)
' >"$tmp" 2>/dev/null || { rm -f "$tmp"; return 0; }
    fi

    # An empty file means the filter selected nothing; leave the previous reading in place
    [ -s "$tmp" ] || { rm -f "$tmp"; return 0; }
    chmod 600 "$tmp" 2>/dev/null
    mv -f "$tmp" "$OUT" 2>/dev/null || rm -f "$tmp"
}

write_sidecar

# The chained command owns the visible line. It gets the untouched payload, exactly as Claude
# Code sent it, so it cannot tell this wrapper is in front of it.
if [ -n "$CHAIN" ]; then
    printf '%s' "$payload" | sh -c "$CHAIN"
fi
