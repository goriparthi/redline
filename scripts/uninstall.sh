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

if [[ "${1:-}" == "--purge" ]]; then
    rm -rf "$CONFIG_DIR"
    rm -f "$HOME/Library/Logs/$BIN_NAME.log" "$HOME/Library/Logs/$BIN_NAME.err"
    # Snapshot and the Ollama usage log, which nothing else records
    rm -rf "${REDLINE_DATA_DIR:-$HOME/.local/share/$BIN_NAME}"
    rm -f "$HOME/.local/bin/ollama-run.sh"
    info "Purged config, logs and history"
else
    echo "Config kept at $CONFIG_DIR (use --purge to remove)"
fi
