#!/bin/bash
# Cuts a GitHub release: gates the version and its notes, tests, builds a notarized DMG,
# uploads it, and prints the cask sha256.
# Usage: scripts/release.sh [version]   (defaults to Resources/Info.plist version)
source "$(dirname "$0")/lib/common.sh"

# How long a core version may live in beta before it has to be promoted. Two limits because
# either alone is toothless: a count lets a build sit on beta.2 forever, an age lets a whole
# run of betas ship in one afternoon. Together they force the decision.
MAX_BETAS=3
MAX_BETA_DAYS=14

command -v gh >/dev/null || die "gh CLI not found; install with: brew install gh"

# The plist is the single source of truth for what actually gets built, so an explicit
# version argument only ever confirms it; a mismatch means the bump was forgotten.
if [[ -n "${1:-}" && "$1" != "$VERSION" ]]; then
    die "asked for $1 but Resources/Info.plist says $VERSION; bump the plist first"
fi

TAG="v$VERSION"
CORE="${VERSION%%-*}"
IS_BETA=0
if [[ "$VERSION" == *-* ]]; then IS_BETA=1; fi

# Everything cheap runs before the build. A gate that only fires after a full test run and a
# notarization round trip is a gate nobody wants to reach.
git -C "$REPO_ROOT" diff --quiet || die "working tree is dirty; commit before releasing"

# Written notes, every release, no exception: the body of a release is the only thing a
# person has to decide whether installing it is worth their afternoon.
"$REPO_ROOT/scripts/check-notes.sh" "$VERSION"

if [[ "${GATE_ANYWAY:-0}" == "1" ]]; then
    warn "GATE_ANYWAY=1: skipping the promotion gate"
else
    # Published releases rather than tags: this gate is about what reached users, and a
    # release can be deleted while its tag lingers. gh's --jq needs no local jq.
    SHIPPED="$(gh release list --limit 100 --json tagName,createdAt \
        --jq '.[] | [.tagName, .createdAt] | @tsv')" \
        || die "could not list published releases"

    # Dots are literal here: an unescaped 0.4.0 also matches a tag like v0x4y0-beta.1
    CORE_RE="${CORE//./\\.}"
    BETAS="$(printf '%s\n' "$SHIPPED" | grep -E "^v$CORE_RE-" || true)"
    BETA_COUNT=0
    if [[ -n "$BETAS" ]]; then
        BETA_COUNT="$(printf '%s\n' "$BETAS" | grep -c .)"
    fi
    STABLE_SHIPPED=0
    if printf '%s\n' "$SHIPPED" | grep -qE "^v$CORE_RE"$'\t'; then STABLE_SHIPPED=1; fi

    if (( IS_BETA )); then
        if (( STABLE_SHIPPED )); then
            die "v$CORE is already out, so a prerelease of it can never reach anyone:
version ordering puts $VERSION below $CORE. Bump the core version instead."
        fi
        if (( BETA_COUNT >= MAX_BETAS )); then
            die "$BETA_COUNT betas already shipped on $CORE with no stable release.
Promote it: set Resources/Info.plist to $CORE, write notes/releases/$CORE.md, release.
Set GATE_ANYWAY=1 to cut another beta anyway."
        fi
        # Only judge age against a beta that exists and whose date parsed; a silent 0 would
        # read as 1970 and trip the gate on every first beta.
        OLDEST=""
        if (( BETA_COUNT )); then
            OLDEST="$(printf '%s\n' "$BETAS" | cut -f2 | sort | head -1)"
        fi
        OLDEST_EPOCH=0
        if [[ -n "$OLDEST" ]]; then
            OLDEST_EPOCH="$(date -j -f "%Y-%m-%dT%H:%M:%SZ" "$OLDEST" +%s 2>/dev/null || echo 0)"
        fi
        if (( OLDEST_EPOCH > 0 )); then
            AGE_DAYS=$(( ( $(date +%s) - OLDEST_EPOCH ) / 86400 ))
            if (( AGE_DAYS > MAX_BETA_DAYS )); then
                die "$CORE has been in beta for $AGE_DAYS days (limit $MAX_BETA_DAYS).
Promote it: set Resources/Info.plist to $CORE, write notes/releases/$CORE.md, release.
Set GATE_ANYWAY=1 to cut another beta anyway."
            fi
        elif [[ -n "$OLDEST" ]]; then
            warn "could not read the age of the first $CORE beta; skipping the age check"
        fi
    elif (( BETA_COUNT == 0 )); then
        # The mirror of the gate above, or the answer to it is simply to skip beta entirely
        die "no beta ever shipped for $CORE, so this would be its first public build.
Cut $CORE-beta.1 first, or set GATE_ANYWAY=1 for a hotfix that cannot wait."
    fi
fi

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

# A version with a prerelease suffix ships to the beta channel: GitHub marks the release
# prerelease, so releases/latest, stable-channel updaters and the stable cask never see it.
PRERELEASE_FLAG=""
CASK_FILE="Casks/redline.rb"
if (( IS_BETA )); then
    PRERELEASE_FLAG="--prerelease"
    CASK_FILE="Casks/redline-beta.rb"
    info "Prerelease version; publishing $TAG to the beta channel"
fi

info "Creating release $TAG"
gh release create "$TAG" "$DMG" --title "$APP_NAME $TAG" \
    --notes-file "$REPO_ROOT/notes/releases/$VERSION.md" $PRERELEASE_FLAG

SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
info "Release published. Update $CASK_FILE:"
echo "  version \"$VERSION\""
echo "  sha256 \"$SHA\""
