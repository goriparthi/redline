# RedLine

Personal project. Read `docs/ARCHITECTURE.md` before changing anything and
`docs/EXTENDING.md` before adding anything.

## Deploying is not optional

**A DMG is not a release.** If you run `make dmg` on purpose, you have started a deploy and
you finish it in the same session: push, `make release`, update the cask. A DMG left sitting
in `dist/` is a build nobody can install, and it is stale by the next commit.

`docs/DEPLOY.md` is the full procedure. The short version:

1. Bump `Resources/Info.plist` and `project.yml` to the same version.
2. Write `notes/releases/<version>.md`.
3. `make xcodeproj` if you added a source file, and commit it.
4. Commit, then **push**, because `gh release create` tags the remote's HEAD and not yours.
5. `NOTARY_PROFILE=redline make release`.
6. Paste the printed version and sha256 into `Casks/redline-beta.rb` for a prerelease or
   `Casks/redline.rb` for a stable, and commit that as `Cask <version>`.
7. `gh release list --limit 3` to confirm it is actually there.

Never point the stable cask at a prerelease. Never promote a beta to stable without being
asked; ask first.

Publishing a release is outward facing, so confirm before step 5 unless the ask was
explicitly to publish.

## Conventions

- **No Jira ticket and no ticket-prefixed branch.** This is a personal repo. Commit subjects
  read like `Release 0.6.0: promote the beta line to stable` and `Cask 0.6.0`.
- No em dashes anywhere, including comments and commit messages.
- Code comments and file headers: two lines maximum.
- Testable logic goes in `RedlineCore`, which has no AppKit, no network and no Keychain.
  Anything touching those lives in `Sources/redline`.
- Every scanner takes an injectable `root:`/`log:` and a `now:` on `scan`, so tests run
  against fixtures in a temp directory rather than the real home.
- Never guess a number the user reads as fact. Unpriced models are excluded from cost and
  flagged, not priced at a nearby tier.
- Adding a source file means `make xcodeproj`, because the committed project holds an
  explicit file list and `swift build` globbing the directory will hide the omission.
- `swift build` and `make test` never compile the widget. Run `make widget` before assuming
  widget changes compile.

## Checks

`make test` for the unit suite, `make ci` for everything CI runs. Both are green before a
release, and `make release` runs the tests again anyway.
