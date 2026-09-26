# Installs RedLine for the current user: copies the build to %LOCALAPPDATA%\Programs\RedLine,
# registers it to start at sign-in, and launches it. No administrator rights needed.
param([string]$Source = $PSScriptRoot, [switch]$NoLaunch, [switch]$NoStartup)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\RedLine'
if (-not (Test-Path (Join-Path $Source 'RedLine.exe'))) {
    $built = Get-ChildItem (Join-Path $PSScriptRoot '..\dist') -Directory -Filter 'RedLine-*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $built) { throw 'No build found. Run scripts\build.ps1 first.' }
    $Source = $built.FullName
}

# A running copy holds its files open; retire it so the copy below cannot half-fail
Get-Process RedLine -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Recurse (Join-Path $Source '*') $dest
Write-Host "Installed $dest"

if (-not $NoStartup) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RedLine' `
        -Value ('"' + (Join-Path $dest 'RedLine.exe') + '"')
    Write-Host 'Registered to start at sign-in'
}
Write-Host "CLI:     $(Join-Path $dest 'redlinectl.exe')"
Write-Host "Config:  $(Join-Path $env:USERPROFILE '.config\redline\config.json')"
if (-not $NoLaunch) { Start-Process (Join-Path $dest 'RedLine.exe'); Write-Host 'Started. Look for the RedLine icon in the tray.' }
