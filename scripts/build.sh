#!/bin/bash
# Builds the release binary.
source "$(dirname "$0")/lib/common.sh"

cd "$REPO_ROOT"
info "Building $APP_NAME $VERSION (release)"
swift build -c release
info "Binary: $BUILD_DIR/release/$BIN_NAME"
