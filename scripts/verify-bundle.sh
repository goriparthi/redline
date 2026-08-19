#!/bin/bash
# Checks that dist/Redline.app is a bundle macOS will actually run: valid Info.plist, intact
# signature, an executable that answers, and the widget where the app expects it.
#
# Lives here rather than inline in the CI workflow so a laptop and a runner check the same
# things, and so a failure can be reproduced without pushing a commit.
source "$(dirname "$0")/lib/common.sh"

APP="${1:-$APP_BUNDLE}"
[[ -d "$APP" ]] || die "no bundle at $APP; run make bundle first"

info "Verifying $APP"

plutil -lint "$APP/Contents/Info.plist" >/dev/null || die "Info.plist is not valid"

# --strict catches a resource added after signing, which is how a bundle passes a plain
# verify locally and is refused on someone else's machine.
codesign --verify --strict "$APP" || die "signature does not verify"

BIN_PATH="$APP/Contents/MacOS/$BIN_NAME"
[[ -x "$BIN_PATH" ]] || die "no executable at $BIN_PATH"

REPORTED="$("$BIN_PATH" --version 2>/dev/null | awk '{print $2}')"
[[ -n "$REPORTED" ]] || die "the binary does not answer --version"
if [[ "$REPORTED" != "$VERSION" ]]; then
    die "bundle reports $REPORTED but Resources/Info.plist says $VERSION"
fi

# Every command the CLI advertises has to exist, or a script written against the help text
# fails on a machine that has the release
for cmd in status findings history cadence ingest help; do
    "$BIN_PATH" help | grep -qE "^ +$cmd +[a-z]" \
        || die "help does not document the $cmd command"
done

if [[ -d "$APP/Contents/PlugIns/RedlineWidget.appex" ]]; then
    info "Widget embedded"
else
    warn "no widget in this bundle (expected for a SwiftPM-only build)"
fi

info "Bundle verified: $APP_NAME $REPORTED"
