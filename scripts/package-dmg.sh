#!/bin/bash
# Builds a distributable DMG. Notarizes when Developer ID + notary creds are available.
#
# Notarization needs a Developer ID Application cert plus one of:
#   NOTARY_PROFILE=<keychain-profile>   (from: xcrun notarytool store-credentials)
#   NOTARY_APPLE_ID + NOTARY_TEAM_ID + NOTARY_PASSWORD  (app-specific password)
source "$(dirname "$0")/lib/common.sh"

# Ship the widget in the DMG. bundle.sh uses SwiftPM, which cannot build app extensions, so
# a DMG built that way silently omits the widget entirely.
if [[ -d /Applications/Xcode.app ]]; then
    "$REPO_ROOT/scripts/build-widget.sh"
else
    warn "Xcode not found: building without the desktop widget"
    "$REPO_ROOT/scripts/bundle.sh"
fi

DMG="$DIST_DIR/$APP_NAME-$VERSION.dmg"
STAGE="$DIST_DIR/dmg-stage"

info "Staging DMG contents"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP_BUNDLE" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

if [[ -d /Applications/Xcode.app ]] \
   && [[ ! -d "$APP_BUNDLE/Contents/PlugIns/RedlineWidget.appex" ]]; then
    die "widget missing from the bundle; refusing to package a DMG without it"
fi

info "Creating $DMG"
hdiutil create -volname "$APP_NAME $VERSION" -srcfolder "$STAGE" \
    -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

IDENTITY="$(find_signing_identity)"
if [[ -z "$IDENTITY" ]]; then
    warn "Unsigned DMG: no Developer ID Application cert."
    warn "You have Apple Development and Apple Distribution certs, but neither works for"
    warn "distribution outside the App Store. Create a Developer ID Application cert at"
    warn "https://developer.apple.com/account/resources/certificates then re-run."
    warn "Users will need: xattr -dr com.apple.quarantine /Applications/$APP_NAME.app"
    info "Built (unsigned): $DMG"
    exit 0
fi

info "Signing DMG with: $IDENTITY"
codesign --force --sign "$IDENTITY" "$DMG"

NOTARY_ARGS=()
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
    NOTARY_ARGS=(--keychain-profile "$NOTARY_PROFILE")
elif [[ -n "${NOTARY_APPLE_ID:-}" && -n "${NOTARY_TEAM_ID:-}" && -n "${NOTARY_PASSWORD:-}" ]]; then
    # Command-line arguments are readable by any process running as this user, so this path
    # briefly exposes the app-specific password. NOTARY_PROFILE keeps it in the Keychain.
    warn "NOTARY_PASSWORD is passed as an argument and is visible in the process list."
    warn "Prefer: xcrun notarytool store-credentials, then set NOTARY_PROFILE."
    NOTARY_ARGS=(--apple-id "$NOTARY_APPLE_ID" --team-id "$NOTARY_TEAM_ID"
                 --password "$NOTARY_PASSWORD")
else
    warn "Signed but not notarized: set NOTARY_PROFILE or NOTARY_APPLE_ID/TEAM_ID/PASSWORD."
    info "Built (signed, not notarized): $DMG"
    exit 0
fi

info "Submitting for notarization (this can take several minutes)"
xcrun notarytool submit "$DMG" "${NOTARY_ARGS[@]}" --wait
xcrun stapler staple "$DMG"
info "Notarized and stapled: $DMG"
shasum -a 256 "$DMG"
