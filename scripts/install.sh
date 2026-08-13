#!/bin/bash
# Builds, installs to ~/Applications, and registers the LaunchAgent.
source "$(dirname "$0")/lib/common.sh"

# WIDGET=1 builds through Xcode so the desktop widget is included
if [[ "${WIDGET:-0}" == "1" ]]; then
    "$REPO_ROOT/scripts/build-widget.sh"
else
    "$REPO_ROOT/scripts/bundle.sh"
fi

# Retire the pre-rename install so two menu bar icons cannot coexist
if [[ -f "$LEGACY_PLIST" ]]; then
    info "Removing legacy LaunchAgent $LEGACY_LABEL"
    launchctl bootout "gui/$(id -u)" "$LEGACY_PLIST" 2>/dev/null || true
    rm -f "$LEGACY_PLIST"
fi
[[ -f "$LEGACY_BIN" ]] && rm -f "$LEGACY_BIN" && info "Removed legacy binary"

# Carry the old config forward so settings and pricing survive the rename
if [[ -f "$LEGACY_CONFIG_DIR/config.json" && ! -f "$CONFIG_DIR/config.json" ]]; then
    mkdir -p "$CONFIG_DIR"
    cp "$LEGACY_CONFIG_DIR/config.json" "$CONFIG_DIR/config.json"
    info "Migrated config from $LEGACY_CONFIG_DIR"
fi

mkdir -p "$INSTALL_DIR" "$HOME/Library/LaunchAgents"
launchctl bootout "gui/$(id -u)" "$PLIST" 2>/dev/null || true
rm -rf "$INSTALLED_APP"
cp -R "$APP_BUNDLE" "$INSTALLED_APP"
info "Installed $INSTALLED_APP"

# Built with PlistBuddy so paths are XML-escaped rather than interpolated raw
rm -f "$PLIST"
/usr/libexec/PlistBuddy -c "Add :Label string $LAUNCH_LABEL" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :ProgramArguments array" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :ProgramArguments:0 string $INSTALLED_APP/Contents/MacOS/$BIN_NAME" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :RunAtLoad bool true" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :KeepAlive bool true" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :StandardOutPath string $HOME/Library/Logs/$BIN_NAME.log" "$PLIST"
/usr/libexec/PlistBuddy -c "Add :StandardErrorPath string $HOME/Library/Logs/$BIN_NAME.err" "$PLIST"
plutil -lint "$PLIST" >/dev/null

launchctl bootstrap "gui/$(id -u)" "$PLIST"

if [[ -d "$INSTALLED_APP/Contents/PlugIns/RedlineWidget.appex" ]]; then
    # Nudge macOS to notice the extension in its new location
    pluginkit -a "$INSTALLED_APP/Contents/PlugIns/RedlineWidget.appex" 2>/dev/null || true
    info "Widget installed; add it from the desktop widget gallery"
fi

info "Started. Look for the menu bar item."
echo "Config:  $CONFIG_DIR/config.json"
echo "Logs:    $HOME/Library/Logs/$BIN_NAME.log"
echo "Stop:    launchctl bootout gui/\$(id -u)/$LAUNCH_LABEL"
