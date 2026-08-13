# Signing and notarizing

Redline ships ad-hoc signed by default, which works locally but makes macOS block every
download. With a paid Apple Developer membership you can sign and notarize properly, and the
build picks it up automatically.

## What you need, once

### 1. A Developer ID Application certificate

`Apple Development` and `Apple Distribution` certificates are **not** enough. Distribution
outside the App Store needs a third kind:

- Xcode → Settings → Accounts → select your Apple ID → select the team →
  **Manage Certificates…** → **+** → **Developer ID Application**

Only the Account Holder for a team can create one. Confirm it landed:

```sh
security find-identity -v -p codesigning | grep "Developer ID Application"
```

### 2. A notary credential

Two ways. Either stores the secret in the Keychain under a profile name, which keeps it out of
your shell history and out of the process list; that is why `scripts/package-dmg.sh` warns when
you pass `NOTARY_PASSWORD` on the command line instead.

#### Option A: app-specific password

`notarytool` will **not** accept your normal Apple ID password. Using it returns
`HTTP status code: 401. Invalid credentials`, which is the usual reason this step fails.

1. Sign in at <https://account.apple.com> with the Apple ID on the developer team.
2. **Sign-In and Security** → **App-Specific Passwords**.
3. Press **+**, label it something like `notarytool`, and confirm with your Apple ID password.
4. Copy the value. It looks like `abcd-efgh-ijkl-mnop`, and it is shown **once**.
5. Run the command below and **paste** it, hyphens included. The prompt is hidden, so a typo
   is invisible.

```sh
xcrun notarytool store-credentials redline \
  --apple-id "you@example.com" \
  --team-id "YOURTEAMID"
```

Requires two-factor authentication on the Apple ID; app-specific passwords do not exist
without it. If it still fails, check that the Apple ID is a member of that exact team.

#### Option B: App Store Connect API key (better for automation)

No password, no expiry surprises, and revocable on its own.

1. <https://appstoreconnect.apple.com> → **Users and Access** → **Integrations** →
   **App Store Connect API** → generate a key with the **Developer** role.
2. Download the `.p8` once, and note the **Key ID** and the **Issuer ID**.

```sh
xcrun notarytool store-credentials redline \
  --key ~/private_keys/AuthKey_XXXXXXXXXX.p8 \
  --key-id XXXXXXXXXX \
  --issuer xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

Keep the `.p8` out of the repo. It is a credential, and it cannot be downloaded twice.

## The trap that costs you twenty minutes

Do **not** let Xcode sign the build. Automatic signing picks a *Development* identity for a
plain `build` action, and notarization then rejects the result three ways at once: wrong
certificate, no secure timestamp, and a `get-task-allow` entitlement that only belongs in debug
builds. You find out after the upload, not during the build.

`scripts/build-widget.sh` therefore compiles with `CODE_SIGNING_ALLOWED=NO` and signs
inside-out afterwards, framework then extension then app, with `--options runtime --timestamp`.
It also fails the build if `get-task-allow` reappears, so the mistake surfaces in seconds
rather than after a notary round trip.

Related: the app carries **no capability entitlements at all**, and the widget carries only
`app-sandbox`. An App Group was declared here once, but a capability entitlement demands a
provisioning profile, and that is what forced Xcode into development signing. The widget reads
the snapshot from its own container instead, which needs no entitlement.

## Releasing a signed build

```sh
NOTARY_PROFILE=redline make dmg
```

`scripts/bundle.sh` finds the Developer ID certificate on its own, signs with the hardened
runtime, and `package-dmg.sh` submits to the notary service and staples the ticket. Then:

```sh
make release        # tags, uploads the DMG, prints the cask sha256
```

Verify what you produced before publishing it:

```sh
spctl -a -vvv -t install /Applications/Redline.app   # expect: accepted, source=Notarized
xcrun stapler validate dist/Redline-*.dmg            # expect: validate action worked
```

## What signing changes beyond the download warning

- **The Keychain stops re-prompting.** Ad-hoc signing changes the code identity on every
  rebuild, which invalidates the Keychain ACL and makes macOS ask again. A stable Developer ID
  identity ends that.
- **The widget can use the real App Group.** App Group containers resolve only for code signed
  with a Team ID. Set `REDLINE_TEAM_ID` before `make xcodeproj`, and the snapshot no longer
  needs to be copied into the widget's own container.
- **Gatekeeper stops blocking**, so the DMG becomes a reasonable way to give the app to
  someone rather than a support conversation.

## What it does not change

Notarization is an automated malware scan, not a review. Apple does not inspect which APIs an
app calls or adjudicate another company's terms of service, so the undocumented Claude endpoint
described in the README is unaffected either way. App Store review is a different process with
different rules, which is why Redline does not target it.
