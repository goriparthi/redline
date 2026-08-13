#!/bin/bash
# Runs an Ollama prompt and records token usage, which Ollama does not persist itself.
#
# Usage:
#   scripts/ollama-run.sh <model> <<'PROMPT'
#   ...your prompt...
#   PROMPT
#
# Appends one JSON object per call to ~/.local/share/redline/ollama.jsonl, which the Ollama
# provider reads. Prints only the model's response on stdout, so it is a drop-in replacement
# for `ollama run <model>` in a heredoc.
#
# Privacy: the prompt never reaches a command-line argument or an environment variable.
# Both are readable by any other process running as this user, so the request body is piped
# on stdin instead. Only counts and timings are logged, never prompt or response text.
set -euo pipefail

MODEL="${1:-}"
[[ -n "$MODEL" ]] || { echo "usage: $0 <model> < prompt" >&2; exit 2; }

HOST="${OLLAMA_HOST:-http://127.0.0.1:11434}"
LOG_DIR="${REDLINE_DATA_DIR:-$HOME/.local/share/redline}"
LOG="$LOG_DIR/ollama.jsonl"
mkdir -p "$LOG_DIR"
# The log records usage, so keep it owner-only
chmod 700 "$LOG_DIR" 2>/dev/null || true

TMP="$(mktemp -d "${TMPDIR:-/tmp}/redline-ollama.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
chmod 700 "$TMP"

# Build the request from stdin, writing to a private temp file rather than a variable
python3 -c '
import json, sys
model = sys.argv[1]
prompt = sys.stdin.read()
if not prompt.strip():
    sys.stderr.write("empty prompt on stdin\n")
    sys.exit(2)
json.dump({"model": model, "prompt": prompt, "stream": False}, sys.stdout)
' "$MODEL" > "$TMP/req.json"

# stream:false so the response carries the final token counts in one object.
# --data-binary @- reads the body from stdin, keeping the prompt out of the process list.
if ! curl -sS --fail-with-body --max-time "${OLLAMA_TIMEOUT:-600}" \
        -H "Content-Type: application/json" \
        --data-binary @- "$HOST/api/generate" \
        < "$TMP/req.json" > "$TMP/resp.json"; then
    echo "ollama request failed against $HOST" >&2
    exit 1
fi

# Response text to stdout, accounting to the log. Both read from files, never argv.
python3 -c '
import json, sys, datetime, os
resp_path, log_path, fallback_model = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    with open(resp_path, encoding="utf-8") as f:
        o = json.load(f)
except (json.JSONDecodeError, OSError):
    sys.stderr.write("unparseable ollama response\n")
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
