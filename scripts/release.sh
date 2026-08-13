#!/bin/bash
# Cuts a GitHub release: tests, builds a DMG, uploads it, and prints the cask sha256.
# Usage: scripts/release.sh [version]   (defaults to Resources/Info.plist version)
source "$(dirname "$0")/lib/common.sh"

TAG="v${1:-$VERSION}"
command -v gh >/dev/null || die "gh CLI not found; install with: brew install gh"

"$REPO_ROOT/scripts/test.sh"
"$REPO_ROOT/scripts/package-dmg.sh"

DMG="$DIST_DIR/$APP_NAME-$VERSION.dmg"
[[ -f "$DMG" ]] || die "DMG not found at $DMG"

git -C "$REPO_ROOT" diff --quiet || die "working tree is dirty; commit before releasing"

info "Creating release $TAG"
gh release create "$TAG" "$DMG" --title "$APP_NAME $TAG" --generate-notes

SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
info "Release published. Update Casks/redline.rb:"
echo "  version \"$VERSION\""
echo "  sha256 \"$SHA\""
