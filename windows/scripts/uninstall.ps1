# Removes RedLine for the current user. -Purge also deletes config, logs and history.
# Claude, Codex and Ollama files are never touched.
param([switch]$Purge)
$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\RedLine'
Get-Process RedLine -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RedLine' -ErrorAction SilentlyContinue
cmdkey /delete:redline 2>$null | Out-Null
$shim = Join-Path $env:USERPROFILE '.local\bin\ollama.cmd'
if ((Test-Path $shim) -and (Select-String -Path $shim -Pattern 'RedLine ollama shim' -Quiet)) { Remove-Item $shim; Write-Host 'Removed the ollama shim' }
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest; Write-Host "Removed $dest" }
if ($Purge) {
    foreach ($p in '.config\redline', '.local\share\redline') {
        $full = Join-Path $env:USERPROFILE $p
        if (Test-Path $full) { Remove-Item -Recurse -Force $full; Write-Host "Removed $full" }
    }
}
Write-Host 'If Set Up Claude Tracking was used, remove the statusLine entry from ~/.claude/settings.json or use Uninstall RedLine... in the app, which restores it.'
