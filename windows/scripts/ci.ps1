# Everything Windows CI runs, in the same order, so a red build reproduces locally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build Redline.Windows.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    dotnet test Redline.Windows.sln -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
    & (Join-Path $PSScriptRoot 'build.ps1') -NoZip
    # No em dashes anywhere, a house rule
    $dash = Get-ChildItem -Recurse -Include *.cs,*.xaml,*.md,*.ps1 -Path src,tests,scripts,*.md | Select-String -Pattern ([char]0x2014) -List
    if ($dash) { $dash | ForEach-Object { Write-Host $_.Path }; throw 'em dash found' }
} finally { Pop-Location }
