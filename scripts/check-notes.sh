#!/bin/bash
# Lints notes/releases/<version>.md for the shape a person can actually read: a summary
# line, titled sections, real prose, no commit dump. Usage: check-notes.sh [version]
source "$(dirname "$0")/lib/common.sh"

ALLOW_MISSING=0
WANT=""
for arg in "$@"; do
    case "$arg" in
        --allow-missing) ALLOW_MISSING=1 ;;
        -*) die "unknown option: $arg" ;;
        *) WANT="$arg" ;;
    esac
done
[[ -n "$WANT" ]] || WANT="$VERSION"

NOTES="$REPO_ROOT/notes/releases/$WANT.md"
SHAPE="Shape that works:
  one plain summary sentence, no heading
  ### Fixed: / ### Added: / ### Changed: sections written as prose
  ### Install"

if [[ ! -f "$NOTES" ]]; then
    if (( ALLOW_MISSING )); then
        warn "no notes yet at notes/releases/$WANT.md; they are required to release"
        exit 0
    fi
    die "no release notes at notes/releases/$WANT.md
Write what changed for someone deciding whether to update.
$SHAPE"
fi

FAIL=0
bad() { printf '\033[1;31mnotes:\033[0m %s\n' "$*" >&2; FAIL=1; }

# The first non-blank line is what GitHub shows first and what a person reads to decide
# whether to keep reading, so it has to be a sentence rather than a heading or a bullet.
SUMMARY="$(grep -m1 -v '^[[:space:]]*$' "$NOTES" || true)"
if [[ -z "$SUMMARY" ]]; then
    bad "file is empty"
elif [[ "$SUMMARY" == \#* || "$SUMMARY" == [-*]\ * ]]; then
    bad "starts with a heading or a bullet; open with a plain summary sentence"
elif (( ${#SUMMARY} < 30 )); then
    bad "opening summary is too short to say anything: \"$SUMMARY\""
fi

if ! grep -qE '^### .+' "$NOTES"; then
    bad "no '### ' sections; group the changes under titled sections"
fi

# The generated-notes shapes, which is exactly what these notes exist instead of
if grep -qiE '^\*{0,2}Full Changelog|by @[^ ]+ in https://github|^[*-] [0-9a-f]{7,40} ' "$NOTES"; then
    bad "reads like a generated commit dump; describe the changes instead"
fi

# Prose, excluding headings, blank lines and indented code blocks. A compare link and two
# section titles clears every check above and still tells a reader nothing.
WORDS="$(awk '!/^[[:space:]]*$/ && !/^#/ && !/^    /' "$NOTES" | wc -w | tr -d ' ')"
if (( WORDS < 40 )); then
    bad "only $WORDS words of prose; say what changed and why it matters (40 word minimum)"
fi

# Release notes are user-facing copy, and an em dash there is a tell nowhere else in this
# repo has. Reword with a semicolon, a comma or a new sentence.
if grep -qn '—' "$NOTES"; then
    bad "contains an em dash on line(s): $(grep -n '—' "$NOTES" | cut -d: -f1 | paste -sd, -)"
fi

if (( FAIL )); then
    printf '%s\n' "$SHAPE" >&2
    die "notes/releases/$WANT.md is not ready to ship"
fi

info "notes/releases/$WANT.md looks readable ($WORDS words)"
