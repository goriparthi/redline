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

## The account and the name, both done

Registration is at [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) → Get
started for free → **Individual developer (free)**, with a **personal** Microsoft account.

Not `partner.microsoft.com/dashboard`, which lands on the Microsoft Cloud Partner Program: a
different thing entirely, for resellers and partner organizations. If a page asks you to log in
with a **work** account, you are in the wrong one.

`RedLine` was taken, so the reserved name is **RedLineMonitor**. That is the name in the Store
and in the Start menu; the app calls itself RedLine everywhere it speaks for itself, which
nothing validates. If `RedLine` ever frees up, Manage app names can add it.

## The identity, which is now fixed forever

Partner Center assigned these when the product was created, and **none of them can be
changed**. They are in `Package.appxmanifest` already:

| Partner Center | Value |
|---|---|
| Package/Identity/Name | `PrashanthGoriparthi.RedLineMonitor` |
| Package/Identity/Publisher | `CN=FAC0DA4F-3C48-41BA-A60C-9C96E94CBB9F` |
| Package/Properties/PublisherDisplayName | `Prashanth Goriparthi` |
| Package Family Name | `PrashanthGoriparthi.RedLineMonitor_5q7p1twdvrvx0` |
| Store ID | `9P33V6M8FMHK` |

The Publisher is a GUID Microsoft issued, not a person. Any certificate that signs this
package must carry that exact subject, including the throwaway one CI generates, or Windows
reads the package as tampered with.

## Producing the package

There is nothing to patch. The manifest carries the real Store identity, so the package CI
builds, installs and self tests on every run is the same one that gets uploaded. Build it the
way the `Build the MSIX` step does and upload the `.msix`, unsigned: certification signs it.

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

## Getting it onto a machine you own

CI publishes three artifacts per run, and picking the wrong one wastes an afternoon.

| Artifact | What it is | Use it for |
|---|---|---|
| `redline-windows-app-x64` | the self-contained folder | looking at the app, screenshots |
| `redline-windows-msix-x64-unsigned` | the package, untouched | the Store submission |
| `redline-windows-msix-x64-testsigned` | the same package signed by the run, plus its public certificate | installing by hand |

**To just look at it, take the folder.** Unzip and run `RedLine.App.exe`. No install, nothing
to trust, and the engine is already beside it. This is the one to use for screenshots.

**To install the package for real**, take the test signed artifact and, in an elevated
PowerShell:

```powershell
Import-Certificate -FilePath .\redline-ci-public.cer `
                   -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\RedLine.App_0.8.3.0_x64.msix
```

The certificate step is not optional and not a formality: that key was generated inside the CI
run and exists nowhere else, so no machine trusts it until told to. Only do this on a machine
you are about to throw away. Uninstall with
`Get-AppxPackage PrashanthGoriparthi.RedLineMonitor | Remove-AppxPackage`.

The unsigned artifact cannot be installed this way at all. That is not a defect: Windows
refuses unsigned packages, which is the whole reason RedLine goes through the Store.

Downloading an artifact needs authentication even on a public repository, so on the VM either
use `gh run download <run-id> -n <artifact-name>` or the S3 route already set up for it.

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
