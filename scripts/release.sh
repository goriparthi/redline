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

# A release that is signed but not notarized is worse than no release: Gatekeeper warns on
# it, and the in-place updater refuses anything not notarized, so it cannot even be replaced
# from inside the app. package-dmg.sh only warns, because a local build has no such promise.
if [[ "${ALLOW_UNNOTARIZED:-0}" != "1" ]] && ! spctl -a -t install "$DMG" 2>/dev/null; then
    die "$(basename "$DMG") is not notarized. Rebuild with:
    NOTARY_PROFILE=<profile> make dmg
See docs/SIGNING.md. Set ALLOW_UNNOTARIZED=1 only for a release nobody will install."
fi

git -C "$REPO_ROOT" diff --quiet || die "working tree is dirty; commit before releasing"

# A version with a prerelease suffix ships to the beta channel: GitHub marks the release
# prerelease, so releases/latest, stable-channel updaters and the stable cask never see it.
PRERELEASE_FLAG=""
CASK_FILE="Casks/redline.rb"
if [[ "$VERSION" == *-* ]]; then
    PRERELEASE_FLAG="--prerelease"
    CASK_FILE="Casks/redline-beta.rb"
    info "Prerelease version; publishing $TAG to the beta channel"
fi

info "Creating release $TAG"
gh release create "$TAG" "$DMG" --title "$APP_NAME $TAG" --generate-notes $PRERELEASE_FLAG

SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
info "Release published. Update $CASK_FILE:"
echo "  version \"$VERSION\""
echo "  sha256 \"$SHA\""
