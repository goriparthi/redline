# Rewrites the package identity for a Store submission.
#
# The manifest in the repo carries the sideload identity, which is what CI installs and self
# tests. Partner Center assigns its own Name and Publisher when an app name is reserved, and a
# package whose identity does not match the reservation is rejected on upload. Rather than keep
# two manifests that drift, this patches the one.
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][string]$PublisherDisplayName,
    [string]$Manifest = "windows\RedLine.App\Package.appxmanifest"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Manifest)) { throw "not found: $Manifest" }

$xml = [xml](Get-Content $Manifest -Raw)
$identity = $xml.Package.Identity
if (-not $identity) { throw "no Identity element in $Manifest" }

# Version is deliberately left alone. It comes from Resources/Info.plist by way of the release
# procedure, and scripts/ci.sh fails when the two have drifted.
$identity.Name = $Name
$identity.Publisher = $Publisher
$xml.Package.Properties.PublisherDisplayName = $PublisherDisplayName

$xml.Save((Resolve-Path $Manifest))
Write-Host "identity: $Name"
Write-Host "publisher: $Publisher ($PublisherDisplayName)"
Write-Host "version:  $($identity.Version) (unchanged)"
