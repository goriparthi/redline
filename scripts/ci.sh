#!/bin/bash
# The whole check suite, in the order that fails cheapest first. This is what CI runs, and
# running `make ci` on a laptop runs exactly the same thing: the workflow file only decides
# when to call this, never what it does.
#
#   scripts/ci.sh            everything
#   scripts/ci.sh --quick    skip the Xcode project and widget build
source "$(dirname "$0")/lib/common.sh"

QUICK=0
for arg in "$@"; do
    case "$arg" in
        --quick) QUICK=1 ;;
        *) die "unknown option: $arg" ;;
    esac
done

step() { printf '\n\033[1;35m===\033[0m %s\n' "$*"; }

# One toolchain for the whole run. test.sh and build-widget.sh each resolve this for
# themselves, so exporting it here only makes them agree; what it really fixes is the report
# below, which used to run xcodebuild directly and abort the suite on its first step on any
# machine whose active developer directory is the Command Line Tools.
DEV_DIR="$(resolve_developer_dir)"
if [[ -n "$DEV_DIR" ]]; then export DEVELOPER_DIR="$DEV_DIR"; fi

step "Toolchain"
swift --version
info "DEVELOPER_DIR=${DEVELOPER_DIR:-unset}"
# Reporting only. A machine without Xcode still runs everything up to the widget steps, which
# say so themselves, so failing to print a version must not end the suite here.
if command -v xcodebuild >/dev/null; then
    xcodebuild -version || warn "xcodebuild is present but no Xcode is selected"
fi

# Written notes are part of the change, not a release day scramble. Missing is a warning
# here and fatal in release.sh; one that reads like a commit dump fails either way.
step "Release notes"
"$REPO_ROOT/scripts/check-notes.sh" --allow-missing

step "Build"
"$REPO_ROOT/scripts/build.sh"

step "Unit tests"
"$REPO_ROOT/scripts/test.sh"

step "End to end"
"$REPO_ROOT/scripts/e2e.sh"

step "Bundle"
"$REPO_ROOT/scripts/bundle.sh"

step "Verify bundle"
"$REPO_ROOT/scripts/verify-bundle.sh"

if (( QUICK )); then
    info "Quick run: skipping the Xcode project and widget build"
    info "All checks passed"
    exit 0
fi

# The committed project is written by whatever Xcode generated it, and a runner with an
# older Xcode cannot open a newer project format. Regenerating makes this independent of that.
step "Xcode project"
if command -v xcodegen >/dev/null; then
    (cd "$REPO_ROOT" && xcodegen generate)
else
    warn "xcodegen not installed; skipping regeneration"
fi

step "App with widget (unsigned)"
SIGN=no "$REPO_ROOT/scripts/build-widget.sh"
[[ -d "$DIST_DIR/$APP_NAME.app/Contents/PlugIns/RedlineWidget.appex" ]] \
    || die "widget is missing from the built app"

step "Verify bundle with widget"
"$REPO_ROOT/scripts/verify-bundle.sh"

info "All checks passed"
