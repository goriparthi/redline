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

ARGS=(-project Redline.xcodeproj -scheme "$APP_NAME" -configuration Release
      -destination 'platform=macOS' -derivedDataPath "$DERIVED")

if [[ -n "$IDENTITY" ]]; then
    info "Building with Developer ID: $IDENTITY"
    ARGS+=(-allowProvisioningUpdates)
else
    info "No Developer ID; building unsigned then ad-hoc signing with entitlements"
    ARGS+=(CODE_SIGNING_ALLOWED=NO)
fi

info "Building $APP_NAME $VERSION with widget"
DEVELOPER_DIR="$DEV_DIR" xcodebuild "${ARGS[@]}" build
[[ -d "$BUILT" ]] || die "build reported success but $BUILT is missing"

if [[ -z "$IDENTITY" ]]; then
    # Nested code must be sealed before the enclosing bundle, so sign framework, then
    # extension, then app. --deep is deprecated and skips per-target entitlements.
    info "Ad-hoc signing framework, widget, then app"
    codesign --force --sign - "$BUILT/Contents/Frameworks/RedlineCore.framework"
    codesign --force --sign - \
        --entitlements Sources/RedlineWidget/RedlineWidget.entitlements \
        --identifier com.goriparthi.redline.widget \
        "$BUILT/Contents/PlugIns/RedlineWidget.appex"
    codesign --force --sign - \
        --entitlements Resources/Redline.entitlements \
        --identifier "$BUNDLE_ID" "$BUILT"
    warn "Ad-hoc signed: runs here, but not distributable to other machines"
fi

codesign --verify --strict "$BUILT" || die "signature verification failed"

# Fail loudly rather than shipping a bundle whose widget cannot reach the shared container
APP_GROUP="group.com.goriparthi.redline"
if ! codesign -d --entitlements - --xml "$BUILT" 2>/dev/null | grep -q "$APP_GROUP"; then
    die "missing App Group entitlement ($APP_GROUP) on the app"
fi
# macOS only registers a sandboxed widget. On an ad-hoc build the App Group cannot resolve,
# so the app writes the snapshot into the widget's own container instead; nothing further is
# needed in the entitlements for that to work.
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
