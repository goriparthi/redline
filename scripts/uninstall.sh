#!/bin/bash
# Removes the app, LaunchAgent and Keychain token. Leaves the config unless --purge.
source "$(dirname "$0")/lib/common.sh"

launchctl bootout "gui/$(id -u)" "$PLIST" 2>/dev/null || true
rm -f "$PLIST"
rm -rf "$INSTALLED_APP"
info "Removed app and LaunchAgent"

security delete-generic-password -s "$BIN_NAME" -a oauth >/dev/null 2>&1 \
    && info "Removed Keychain token" || true

if [[ "${1:-}" == "--purge" ]]; then
    rm -rf "$CONFIG_DIR"
    rm -f "$HOME/Library/Logs/$BIN_NAME.log" "$HOME/Library/Logs/$BIN_NAME.err"
    info "Purged config and logs"
else
    echo "Config kept at $CONFIG_DIR (use --purge to remove)"
fi
