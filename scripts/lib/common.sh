#!/bin/bash
# Shared settings and helpers for every script in this repo. Sourced, not executed.
# Single source of truth for names and paths so a rename touches one file.

set -euo pipefail

APP_NAME="Redline"
# Bundle, target and file names keep the plain spelling; DISPLAY_NAME is the brand as a
# person reads it, used for anything user-facing such as the DMG volume.
DISPLAY_NAME="RedLine"
BIN_NAME="redline"
BUNDLE_ID="com.goriparthi.redline"
LAUNCH_LABEL="com.goriparthi.redline"

# Repo root, resolved from this file so scripts work from any cwd
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

BUILD_DIR="$REPO_ROOT/.build"
DIST_DIR="$REPO_ROOT/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"

# Overridable so a machine that keeps RedLine in /Applications, next to the DMG install,
# does not end up with a second bundle in ~/Applications that only the LaunchAgent knows about
INSTALL_DIR="${INSTALL_DIR:-$HOME/Applications}"
INSTALLED_APP="$INSTALL_DIR/$APP_NAME.app"
PLIST="$HOME/Library/LaunchAgents/$LAUNCH_LABEL.plist"
CONFIG_DIR="$HOME/.config/$BIN_NAME"

# Legacy identifiers from the pre-rename claude-usage-monitor build, used only to clean up
LEGACY_LABEL="com.claude-usage-monitor"
LEGACY_PLIST="$HOME/Library/LaunchAgents/$LEGACY_LABEL.plist"
LEGACY_BIN="$HOME/.local/bin/claude-usage-monitor"
LEGACY_CONFIG_DIR="$HOME/.config/claude-usage-monitor"

VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" \
    "$REPO_ROOT/Resources/Info.plist" 2>/dev/null || echo "0.0.0")"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# XCTest ships with Xcode, not the Command Line Tools. Prefer a full Xcode when the
# selected developer dir lacks XCTest, so `swift test` works without a sudo xcode-select.
resolve_developer_dir() {
    if [[ -n "${DEVELOPER_DIR:-}" ]]; then
        echo "$DEVELOPER_DIR"
        return
    fi
    local selected
    selected="$(xcode-select -p 2>/dev/null || true)"
    if [[ "$selected" == *"CommandLineTools"* && -d /Applications/Xcode.app ]]; then
        echo "/Applications/Xcode.app/Contents/Developer"
    else
        echo "$selected"
    fi
}

# Prefer a Developer ID cert (required for notarized distribution). Falls back to ad-hoc,
# which works locally but leaves downloads quarantined by Gatekeeper.
find_signing_identity() {
    if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
        echo "$CODESIGN_IDENTITY"
        return
    fi
    security find-identity -v -p codesigning 2>/dev/null \
        | grep "Developer ID Application" \
        | head -1 \
        | sed -E 's/.*"(.*)".*/\1/' \
        || true
}
