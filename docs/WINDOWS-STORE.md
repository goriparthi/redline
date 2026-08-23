# Shipping RedLine for Windows through the Microsoft Store

RedLine will not buy an Authenticode certificate. That decision closes off every route that
hands someone an installer directly, because Windows refuses to install an unsigned MSIX and
SmartScreen warns on every unsigned executable forever, reputation being attached to a signing
key nobody has.

The Store is the way round it, and the reason is narrow and worth stating plainly:

> **Microsoft signs the package for you.** You upload the MSIX, certification re-signs it with
> a Store-trusted certificate, and it installs with no prompt, updates itself and can carry a
> Windows 11 widget. Individual developer registration has been free since late 2025.

The cost is a public listing, a review per release, and one written justification, which is
already drafted below.

## What has to happen once, by hand

Nothing here can be scripted, and the first two are permanent.

1. **Register as an individual developer** at
   [partner.microsoft.com](https://partner.microsoft.com/dashboard). Free; identity
   verification is part of it.
2. **Reserve the name.** Apps and games → New product → MSIX or PWA app → check `RedLine` is
   available → Reserve product name. If `RedLine` is taken, whatever is reserved instead
   becomes the display name in the Store and the app is still RedLine everywhere else.
3. **Copy three values** off the product identity page:

   | Partner Center calls it | Goes into `Package.appxmanifest` |
   |---|---|
   | Package/Identity/Name | `<Identity Name="...">` |
   | Package/Identity/Publisher | `<Identity Publisher="CN=...">` |
   | Package/Properties/PublisherDisplayName | `<PublisherDisplayName>` |

   The publisher is a GUID Microsoft assigns, not a person's name. **Package identity cannot
   be changed after the product is created**, so read it twice.

## Producing the package

The manifest in the repo carries the sideload identity, which is what CI installs and self
tests on every run. Do not edit it by hand for a submission; patch it, so the two cannot drift:

```powershell
scripts\set-store-identity.ps1 -Name "<Identity Name>" `
                               -Publisher "CN=<guid>" `
                               -PublisherDisplayName "<publisher display name>"
```

Then build the package the way CI does, and upload the `.msix` to the submission. It goes up
unsigned: certification signs it.

The version comes from `Resources/Info.plist` by way of the normal release procedure, and
`scripts/ci.sh` fails when the manifest has drifted from it. The Store reserves the fourth part
of the version, so it stays `0`.

## The restricted capability, and what to write in the box

RedLine declares `unvirtualizedResources`, which is a restricted capability. Sideloading one
needs no approval; a Store submission does. The Submission options page has a field for it.
This is the justification, and it is the truth rather than a form of words:

> RedLine reads usage records that Claude Code, Codex and Ollama already write into the user's
> own home directory, and records a daily history of its own alongside them. The application
> ships two executables: a tray application and a command line tool that the user also runs
> directly from a terminal, outside the package.
>
> With write virtualization on, the packaged tray application's writes are redirected into a
> per-package location while the same user's command line invocations write to the real path.
> The two would then keep separate histories of the same activity, and each would appear to
> the user to have lost data the other had recorded. The capability is used only to keep one
> history in one place; the application writes nothing outside the user's own profile and
> requires no elevation.

If it is refused, the fallback is to drop the capability and have the packaged application set
`REDLINE_HOME` explicitly so both halves agree on a path inside the container. That costs the
command line tool its view of the same history, which is a real loss but not a broken app.

## What else review is likely to ask

- **Why it reads other applications' files.** It is the product: the transcripts are the
  source of every number RedLine shows, they are the user's own files, and nothing leaves the
  machine. `docs/ARCHITECTURE.md` says this at more length and the README says it first.
- **The child process.** The tray application runs the engine as a separate process from
  inside its own package. That is ordinary for a full trust packaged application.
- **No network for the numbers.** The only outbound requests are provider status pages and the
  update check. Worth saying, because a monitoring tool that phoned home would be judged
  differently.

## Before you submit

Dispatch CI with `wack=true`. It runs the Windows App Certification Kit against the package,
which is what Store certification runs, so anything it rejects costs a CI run rather than a
trip through review.

It passed on 2026-08-22, which settles the one real doubt: the Swift binaries are not built
with Control Flow Guard, and the binary analyzer accepts them regardless. Run it again anyway
before each submission, because the Swift toolchain and its runtime DLLs both move.

## What this does not change

`core-windows` keeps building, signing, installing and self testing the sideload package on
every run, with a certificate generated and thrown away inside the job. That is the test that
the packaging works. The Store submission is a separate, manual act with the identity patched
in, and it is the only build anybody installs.
