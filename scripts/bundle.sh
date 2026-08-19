#!/bin/bash
# Assembles dist/Redline.app from the release binary and signs it.
source "$(dirname "$0")/lib/common.sh"

"$REPO_ROOT/scripts/build.sh"

info "Assembling $APP_BUNDLE"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
cp "$BUILD_DIR/release/$BIN_NAME" "$APP_BUNDLE/Contents/MacOS/$BIN_NAME"
cp "$REPO_ROOT/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
# Brand assets: app icon plus the template mark the status item loads by name
cp "$REPO_ROOT/Resources/Redline.icns" "$APP_BUNDLE/Contents/Resources/"
cp "$REPO_ROOT/Resources/RedlineTemplate.png" \
   "$REPO_ROOT/Resources/RedlineTemplate@2x.png" "$APP_BUNDLE/Contents/Resources/"
# The Ollama shim ships inside the app so every install route has it, not just a clone
cp "$REPO_ROOT/scripts/ollama-shim.sh" "$APP_BUNDLE/Contents/Resources/"
chmod 755 "$APP_BUNDLE/Contents/Resources/ollama-shim.sh"
plutil -lint "$APP_BUNDLE/Contents/Info.plist" >/dev/null

# The same entitlements the Xcode path signs with. Without this the two build paths differ
# in what the app is allowed to do, and the hardened runtime silently kills every Apple
# event, which is how the fleet pane reaches a session's own terminal tab.
ENTITLEMENTS="$REPO_ROOT/Resources/Redline.entitlements"
[[ -f "$ENTITLEMENTS" ]] || die "missing $ENTITLEMENTS"

IDENTITY="$(find_signing_identity)"
if [[ -n "$IDENTITY" ]]; then
    info "Signing with: $IDENTITY"
    # Hardened runtime is required for notarization
    codesign --force --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP_BUNDLE"
else
    warn "No Developer ID Application cert found; signing ad-hoc."
    warn "Ad-hoc builds run locally but downloads stay Gatekeeper-quarantined."
    codesign --force --entitlements "$ENTITLEMENTS" \
        --sign - --identifier "$BUNDLE_ID" "$APP_BUNDLE"
fi
codesign --verify --strict "$APP_BUNDLE" && info "Signature verified"
