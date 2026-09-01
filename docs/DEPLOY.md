# Deploying RedLine

A DMG that exists only in `dist/` is not a release. It is a build nobody can install, and it
goes stale the moment the next commit lands. **Cutting a DMG obliges you to finish the
deploy.** This file is the whole procedure, start to finish, so there is nothing left to
remember.

The rule, stated once:

> **Never stop at `make dmg`.** `make dmg` is a step inside `make release`, not a destination.
> If you built a DMG on purpose, publish it or delete it before you move on.

## What a deploy actually is

Five things have to end up in agreement. A deploy is done when all five say the same version,
and not before.

| # | Artifact | Where it lives | Set by |
|---|---|---|---|
| 1 | Version | `Resources/Info.plist`, `project.yml` | you, by hand |
| 2 | Release notes | `notes/releases/<version>.md` | you, by hand |
| 3 | Git tag and commit | `origin/main`, tag `v<version>` | `git push`, then `scripts/release.sh` |
| 4 | GitHub release plus DMG | the releases page | `scripts/release.sh` |
| 5 | Homebrew cask | `Casks/redline.rb` or `Casks/redline-beta.rb` | you, from the sha the release prints |

Miss 3 and the tag points at the wrong commit. Miss 4 and nobody gets it. Miss 5 and
`brew upgrade` keeps handing out the old build.

## The procedure

### 1. Pick the version

Semver, and the suffix decides the channel. `0.7.0-beta.2` is a prerelease and reaches only
the beta channel; `0.7.0` is stable and reaches everyone.

Bump both of these, and keep them in step:

- `Resources/Info.plist`: `CFBundleShortVersionString` is the full version including any
  suffix, `CFBundleVersion` is the bare core version.
- `project.yml`: `MARKETING_VERSION` matches `CFBundleShortVersionString`.

`Resources/Info.plist` is the single source of truth. Every script reads the version from it,
and `scripts/release.sh` refuses a version argument that disagrees with it.

### 2. Write the release notes

`notes/releases/<version>.md`, required, no exceptions. `scripts/check-notes.sh` enforces the
shape: a plain summary sentence first, then `### Added:` / `### Fixed:` / `### Changed:`
sections in prose, then `### Install`. It rejects a commit dump, anything under 40 words, and
any em dash.

Write it for someone deciding whether to spend their afternoon installing this.

### 3. Regenerate the Xcode project if you added a source file

`Redline.xcodeproj` holds an explicit file list, so a new file is invisible to `xcodebuild`
even though `swift build` globs the directory and succeeds. The widget only builds through
Xcode, so a forgotten regeneration ships an app without the widget.

    make xcodeproj

Commit the result.

### 4. Commit and push

`scripts/release.sh` refuses a dirty tree, and `gh release create` tags whatever the remote's
default branch points at, not your local `HEAD`. So the push has to happen first, or the tag
lands on the wrong commit.

    git add -A
    git commit
    git push origin main

Scan the staged diff for secrets before committing. Commit subjects here read like
`Release 0.7.0-beta.2: see which agents are running and which are stuck`. No ticket prefix;
this is a personal repo.

### 5. Release

    NOTARY_PROFILE=redline make release

That single command runs the whole gauntlet, in the order that fails cheapest first:

1. Refuses a dirty working tree.
2. Lints the release notes.
3. Applies the promotion gate (below).
4. Runs the unit tests.
5. Builds, signs, notarizes and staples the DMG, widget embedded.
6. Refuses to publish a DMG that is not notarized.
7. Creates the tag, uploads the DMG, publishes the release, marking it prerelease when the
   version carries a suffix.
8. Prints the version and sha256 for the cask.

Notarization needs a **Developer ID Application** certificate. `Apple Development` and
`Apple Distribution` certificates do not work outside the App Store. `NOTARY_PROFILE` is a
`notarytool store-credentials` profile name; on this machine it is `redline`. Passing
`NOTARY_APPLE_ID` / `NOTARY_TEAM_ID` / `NOTARY_PASSWORD` instead works but puts the password
in the process list, so prefer the profile.

A signed but unnotarized DMG is worse than none: Gatekeeper warns on it, and the in-place
updater refuses anything unnotarized, so it cannot even be replaced from inside the app.

### 6. Update the cask

`scripts/release.sh` prints the two lines. Paste them into the right file:

- Prerelease (`x.y.z-beta.n`): `Casks/redline-beta.rb`
- Stable: `Casks/redline.rb`

Then commit that on its own, subject `Cask <version>`. **Never point the stable cask at a
prerelease.** The two casks conflict with each other by design, so a user is on one channel
or the other, never both.

### 7. Confirm

    gh release list --limit 3

The new version should be there, marked `Pre-release` if it carries a suffix. If it is not
listed, the deploy did not happen, whatever your `dist/` directory says.

## The promotion gate

`scripts/release.sh` will not let a core version live on beta forever. Two limits, because
either alone is toothless: a count lets a build sit on `beta.2` indefinitely, an age lets a
whole run of betas ship in one afternoon.

- At most **3** betas per core version.
- At most **14 days** from the first beta of that core version.

It also refuses a prerelease of a core version whose stable is already out, because version
ordering puts `0.7.0-beta.3` below `0.7.0` and it could never reach anyone. And it refuses a
stable release for a core version that never shipped a beta at all.

`GATE_ANYWAY=1` skips the gate. It is there for a hotfix that cannot wait, not for avoiding
the decision.

## When a deploy goes wrong

- **Wrong commit tagged.** `gh release delete v<version> --cleanup-tag`, fix, release again.
  Do this quickly; a release anyone has downloaded should get a new version instead.
- **Notarization rejected.** `xcrun notarytool log <submission-id> --keychain-profile redline`
  says why. Usually a missing hardened runtime or an unsigned nested binary.
- **Published but the cask is stale.** Users are on the old build and nothing tells them.
  Fix the cask and commit it; this is the step most likely to be forgotten.
- **Beta needs pulling.** Delete the GitHub release. The stable channel never saw it, so the
  blast radius is beta cask users and anyone on the beta update channel.

## What is not part of a deploy

- **CI** (`.github/workflows/ci.yml`) runs `make ci` on every push to `main`. It builds and
  checks; it does not publish anything. A green CI run is not a release.
- **The site** (`.github/workflows/pages.yml`) publishes only when `site/**` changes, on its
  own schedule. It is documentation, not distribution.
- **`make install`** puts a build in `~/Applications` on this machine and loads the
  LaunchAgent. Useful for dogfooding, invisible to everyone else.
