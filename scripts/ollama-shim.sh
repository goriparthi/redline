#!/bin/bash
# RedLine ollama shim: a transparent stand-in for the ollama CLI that records token counts,
# which Ollama does not persist. Installed as ~/.local/bin/ollama by "Set Up Ollama
# Tracking" in the RedLine menu, so plain `ollama run` is counted with no habit change.
#
# Counted here, over the local API:
#   ollama run MODEL              (prompt piped on stdin, e.g. a heredoc)
#   ollama run MODEL "prompt"     (prompt as a single argument)
# Everything else passes through to the real binary untouched: every other subcommand,
# any `run` with flags or several prompt arguments, and interactive chat (stdin is a TTY).
# If the API call fails the prompt is replayed through the real binary, so the worst case
# is an uncounted call, never a broken one.
#
# Privacy: the prompt travels via files and stdin, never argv or the environment, and only
# counts and timings reach the log. See SECURITY.md.
set -euo pipefail

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)/$(basename "${BASH_SOURCE[0]}")"

# The real binary: explicit override first, then PATH minus this file and any copy of it,
# then the usual install locations, because a LaunchAgent's PATH is short.
find_real() {
    if [[ -n "${REDLINE_OLLAMA_BIN:-}" ]]; then echo "$REDLINE_OLLAMA_BIN"; return; fi
    local IFS=: dir candidate
    for dir in $PATH; do
        candidate="$dir/ollama"
        [[ -x "$candidate" ]] || continue
        [[ "$candidate" -ef "$SELF" ]] && continue
        head -c 300 "$candidate" 2>/dev/null | grep -q "RedLine ollama shim" && continue
        echo "$candidate"; return
    done
    for candidate in /opt/homebrew/bin/ollama /usr/local/bin/ollama; do
        [[ -x "$candidate" && ! "$candidate" -ef "$SELF" ]] && { echo "$candidate"; return; }
    done
    echo ""
}

REAL="$(find_real)"
if [[ -z "$REAL" ]]; then
    echo "redline ollama shim: no ollama binary found; install Ollama or set REDLINE_OLLAMA_BIN" >&2
    exit 127
fi

# Decide whether this exact invocation is one of the two counted shapes
INTERCEPT=""
if [[ "${1:-}" == "run" && -n "${2:-}" && "${2:-}" != -* ]]; then
    if [[ $# -eq 2 && ! -t 0 ]]; then
        INTERCEPT="stdin"
    elif [[ $# -eq 3 && "${3}" != -* ]]; then
        INTERCEPT="arg"
    fi
fi
[[ -z "$INTERCEPT" ]] && exec "$REAL" "$@"

MODEL="$2"
HOST="${OLLAMA_HOST:-http://127.0.0.1:11434}"
[[ "$HOST" != *"://"* ]] && HOST="http://$HOST"
LOG_DIR="${REDLINE_DATA_DIR:-$HOME/.local/share/redline}"
LOG="$LOG_DIR/ollama.jsonl"
mkdir -p "$LOG_DIR"
chmod 700 "$LOG_DIR" 2>/dev/null || true

TMP="$(mktemp -d "${TMPDIR:-/tmp}/redline-ollama.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
chmod 700 "$TMP"

if [[ "$INTERCEPT" == "arg" ]]; then printf '%s' "$3" > "$TMP/prompt"
else cat > "$TMP/prompt"; fi

# Nothing to send is not an error; hand the empty call to the real CLI to answer as it would
[[ -s "$TMP/prompt" ]] || exec "$REAL" "$@" < "$TMP/prompt"

python3 -c '
import json, sys
with open(sys.argv[2], encoding="utf-8", errors="replace") as f:
    prompt = f.read()
json.dump({"model": sys.argv[1], "prompt": prompt, "stream": False}, sys.stdout)
' "$MODEL" "$TMP/prompt" > "$TMP/req.json"

# stream:false so the response carries final token counts in one object
if ! curl -sS --fail-with-body --max-time "${OLLAMA_TIMEOUT:-600}" \
        -H "Content-Type: application/json" \
        --data-binary @- "$HOST/api/generate" \
        < "$TMP/req.json" > "$TMP/resp.json" 2>"$TMP/curl.err"; then
    # The API refused; replay through the real binary so the call still succeeds
    "$REAL" run "$MODEL" < "$TMP/prompt"
    exit $?
fi

# Response text to stdout, counts to the log. Both read from files, never argv.
python3 -c '
import json, sys, datetime, os
resp_path, log_path, fallback_model = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    with open(resp_path, encoding="utf-8") as f:
        o = json.load(f)
except (json.JSONDecodeError, OSError):
    sys.stderr.write("redline ollama shim: unparseable response\n")
    sys.exit(1)

text = o.get("response", "")
sys.stdout.write(text if text.endswith("\n") else text + "\n")

# Durations are nanoseconds. Missing counts mean a cached or empty eval, so default to 0.
rec = {
    "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "model": o.get("model", fallback_model),
    "prompt_eval_count": o.get("prompt_eval_count", 0),
    "eval_count": o.get("eval_count", 0),
    "total_duration_ms": round(o.get("total_duration", 0) / 1e6),
    "load_duration_ms": round(o.get("load_duration", 0) / 1e6),
    "done_reason": o.get("done_reason"),
}
fd = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
with os.fdopen(fd, "a", encoding="utf-8") as f:
    f.write(json.dumps(rec) + "\n")
' "$TMP/resp.json" "$LOG" "$MODEL"
