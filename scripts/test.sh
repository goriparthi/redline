#!/bin/bash
# Runs the test suite, selecting a toolchain that actually has XCTest.
source "$(dirname "$0")/lib/common.sh"

cd "$REPO_ROOT"
DEV_DIR="$(resolve_developer_dir)"
[[ -n "$DEV_DIR" ]] || die "no developer dir found; install Xcode or the Command Line Tools"
info "Testing with DEVELOPER_DIR=$DEV_DIR"
DEVELOPER_DIR="$DEV_DIR" swift test "$@"
