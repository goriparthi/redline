#!/bin/bash
# Renders the MSIX tile and list icons from the same source the app icon comes from.
# Committed output, regenerable input: run this after changing brand/AppIcon.appiconset.
source "$(dirname "$0")/lib/common.sh"

cd "$REPO_ROOT"
SRC="brand/AppIcon.appiconset/icon_512x512.png"
OUT="windows/RedLine.App/Images"
[[ -f "$SRC" ]] || die "no source icon at $SRC"
command -v sips >/dev/null || die "sips is needed; this runs on macOS"

mkdir -p "$OUT"

# Scale 100 only. A packaged app can carry a .scale-200 beside each of these later; shipping
# one honest size beats shipping a set this machine cannot check.
render() {
    sips -z "$2" "$2" "$SRC" --out "$OUT/$1" >/dev/null
    info "$1 (${2}x${2})"
}

render "Square44x44Logo.png" 44
render "Square44x44Logo.targetsize-24_altform-unplated.png" 24
render "Square150x150Logo.png" 150
render "StoreLogo.png" 50

info "Wrote $(ls "$OUT" | wc -l | tr -d ' ') images to $OUT"
