#!/bin/bash
# Removes the app, LaunchAgent and Keychain token. Leaves the config unless --purge.
source "$(dirname "$0")/lib/common.sh"

launchctl bootout "gui/$(id -u)" "$PLIST" 2>/dev/null || true
rm -f "$PLIST"
rm -rf "$INSTALLED_APP"
# A DMG or cask install lands in /Applications, which make install never writes to
SYSTEM_APP="/Applications/$APP_NAME.app"
if [[ -d "$SYSTEM_APP" ]]; then
    if brew list --cask redline >/dev/null 2>&1; then
        warn "$SYSTEM_APP came from Homebrew; remove it with: brew uninstall --cask redline"
    else
        rm -rf "$SYSTEM_APP"
        info "Removed $SYSTEM_APP"
    fi
fi
info "Removed app and LaunchAgent"

security delete-generic-password -s "$BIN_NAME" -a oauth >/dev/null 2>&1 \
    && info "Removed Keychain token" || true

# Puts ~/.claude/settings.json back the way the feed found it: restores whatever statusline
# was chained behind the wrapper, or drops the entry entirely if there was nothing there
# before. Only ever touches an entry that points at our own script.
unwire_usage_feed() {
    local settings="$HOME/.claude/settings.json"
    [[ -f "$settings" ]] || return 0
    command -v python3 >/dev/null 2>&1 || {
        warn "python3 not found; remove the statusLine entry from $settings by hand"
        return 0
    }
    python3 - "$settings" <<'PY'
import json, os, re, sys
path = sys.argv[1]
try:
    with open(path) as fh:
        settings = json.load(fh)
except (OSError, ValueError):
    sys.exit(0)

line = settings.get("statusLine")
command = line.get("command") if isinstance(line, dict) else None
if not command or "claude-statusline.sh" not in command:
    sys.exit(0)

# The original command was preserved verbatim in REDLINE_STATUSLINE_CHAIN='...'
match = re.search(r"REDLINE_STATUSLINE_CHAIN='(.*?)' ", command, re.S)
if match:
    line["command"] = match.group(1).replace("'\\''", "'")
    settings["statusLine"] = line
else:
    settings.pop("statusLine", None)

tmp = path + ".redline.tmp"
with open(tmp, "w") as fh:
    json.dump(settings, fh, indent=2, sort_keys=True)
    fh.write("\n")
os.replace(tmp, path)
print("restored" if match else "removed")
PY
    info "Unwired the Claude usage feed from ~/.claude/settings.json"
}

if [[ "${1:-}" == "--purge" ]]; then
    # Unwire the Claude usage feed before deleting the script it points at. A statusLine
    # entry aimed at a file that no longer exists breaks the statusline on every draw, and
    # takes any command chained behind it down too.
    unwire_usage_feed
    rm -rf "$CONFIG_DIR"
    rm -f "$HOME/Library/Logs/$BIN_NAME.log" "$HOME/Library/Logs/$BIN_NAME.err"
    # Snapshot and the Ollama usage log, which nothing else records
    rm -rf "${REDLINE_DATA_DIR:-$HOME/.local/share/$BIN_NAME}"
    rm -f "$HOME/.local/bin/ollama-run.sh"
    # The shim only if it is ours; never delete a real binary someone parked there
    SHIM="$HOME/.local/bin/ollama"
    if [[ -f "$SHIM" ]] && head -c 300 "$SHIM" 2>/dev/null | grep -q "RedLine ollama shim"; then
        rm -f "$SHIM"
    fi
    info "Purged config, logs and history"
else
    echo "Config kept at $CONFIG_DIR (use --purge to remove)"
fi
