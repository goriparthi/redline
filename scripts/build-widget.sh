#!/bin/bash
# Builds the app WITH the desktop widget, via the Xcode project.
#
# SwiftPM cannot build app extensions, so this path uses xcodebuild.
#
# Signing: the App Group entitlement is what lets the sandboxed widget read the snapshot the
# app writes. Xcode refuses to ad-hoc sign a target carrying that entitlement ("requires a
# provisioning profile"), so when no Developer ID is available we build unsigned and then
# ad-hoc sign inside-out ourselves. macOS honours App Group entitlements on ad-hoc signed
# code locally, which is enough to run it on this machine. Distributing to anyone else needs
# a real Developer ID cert and a registered App Group.
source "$(dirname "$0")/lib/common.sh"

cd "$REPO_ROOT"
DEV_DIR="$(resolve_developer_dir)"
[[ -d "$DEV_DIR" ]] || die "Xcode required to build the widget; only found $DEV_DIR"

# Always regenerate when XcodeGen is available. The project embeds an explicit file list, so
# a new source file would otherwise be missing from the Xcode build while `swift build`, which
# globs the directory, still succeeded. Timestamp comparisons do not catch that.
if command -v xcodegen >/dev/null; then
    info "Regenerating Redline.xcodeproj from project.yml"
    xcodegen generate >/dev/null
else
    warn "xcodegen not found; using the committed project as-is."
    warn "If you added a source file, install xcodegen or add it to the project manually."
fi

DERIVED="$DIST_DIR/xcode"
BUILT="$DERIVED/Build/Products/Release/$APP_NAME.app"
IDENTITY="$(find_signing_identity)"

# Always build unsigned, then sign inside-out ourselves.
#
# Letting Xcode sign was the problem: automatic signing picks a *Development* identity for a
# plain build action, which notarization rejects three ways at once - wrong certificate, no
# secure timestamp, and a get-task-allow entitlement that only belongs in debug builds.
ARGS=(-project Redline.xcodeproj -scheme "$APP_NAME" -configuration Release
      -destination 'platform=macOS' -derivedDataPath "$DERIVED"
      CODE_SIGNING_ALLOWED=NO)

info "Building $APP_NAME $VERSION with widget"
DEVELOPER_DIR="$DEV_DIR" xcodebuild "${ARGS[@]}" build
[[ -d "$BUILT" ]] || die "build reported success but $BUILT is missing"

# Nested code must be sealed before the enclosing bundle: framework, then extension, then app.
# --deep is deprecated and would skip the per-target entitlements.
SIGN_ARGS=(--force)
if [[ -n "$IDENTITY" ]]; then
    info "Signing with Developer ID: $IDENTITY"
    # Hardened runtime and a secure timestamp are both required for notarization
    SIGN_ARGS+=(--sign "$IDENTITY" --options runtime --timestamp)
else
    info "No Developer ID; signing ad-hoc"
    SIGN_ARGS+=(--sign -)
    warn "Ad-hoc signed: runs here, but not distributable to other machines"
fi

codesign "${SIGN_ARGS[@]}" "$BUILT/Contents/Frameworks/RedlineCore.framework"
codesign "${SIGN_ARGS[@]}" \
    --entitlements Sources/RedlineWidget/RedlineWidget.entitlements \
    --identifier com.goriparthi.redline.widget \
    "$BUILT/Contents/PlugIns/RedlineWidget.appex"
codesign "${SIGN_ARGS[@]}" \
    --entitlements Resources/Redline.entitlements \
    --identifier "$BUNDLE_ID" "$BUILT"

# get-task-allow is a debug-only entitlement and notarization refuses it outright
if codesign -d --entitlements - "$BUILT" 2>/dev/null | grep -q "get-task-allow"; then
    die "get-task-allow present; this build would be rejected by notarization"
fi

codesign --verify --strict "$BUILT" || die "signature verification failed"

# macOS only registers a sandboxed widget extension
WIDGET_ENT="$(codesign -d --entitlements - --xml \
    "$BUILT/Contents/PlugIns/RedlineWidget.appex" 2>/dev/null || true)"
grep -q "app-sandbox" <<<"$WIDGET_ENT" \
    || die "widget must be sandboxed or macOS will not register it"

rm -rf "$APP_BUNDLE"
mkdir -p "$DIST_DIR"
cp -R "$BUILT" "$APP_BUNDLE"
# Remove the copy inside DerivedData: Launch Services will happily register and run a widget
# from the build directory, leaving a second extension alive alongside the installed one.
rm -rf "$BUILT"
info "Built $APP_BUNDLE"
[[ -d "$APP_BUNDLE/Contents/PlugIns/RedlineWidget.appex" ]] \
    || die "widget extension is missing from the bundle"
info "Widget embedded: Contents/PlugIns/RedlineWidget.appex"
