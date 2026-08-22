# Assembles a Windows folder that runs on a machine with no Swift toolchain: the binary plus
# the Swift runtime DLLs it imports. Windows resolves DLLs from the exe's own directory first,
# so nothing has to be installed or put on PATH.
param(
    [string]$Bin = ".build\release\redline.exe",
    [string]$Out = "dist\windows"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Bin)) { throw "not found: $Bin" }

New-Item -ItemType Directory -Path $Out -Force | Out-Null
Copy-Item $Bin $Out -Force

# The runtime ships beside the toolchain rather than inside the SDK. Globbed rather than
# pinned to a version, so a toolchain bump here does not need an edit.
$runtimeRoots = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Swift\Runtimes\*\usr\bin"),
    (Join-Path ${env:ProgramFiles} "Swift\Runtimes\*\usr\bin")
)
$found = $runtimeRoots | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Select-Object -First 1
if (-not $found) { throw "no Swift runtime directory found in: $($runtimeRoots -join ', ')" }

Write-Host "runtime: $found"
Copy-Item (Join-Path $found "*.dll") $Out -Force

Write-Host "packaged into $Out :"
Get-ChildItem $Out | ForEach-Object { Write-Host ("  {0,-34} {1,10:N0}" -f $_.Name, $_.Length) }
