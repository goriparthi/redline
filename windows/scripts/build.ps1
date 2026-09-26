# Publishes RedLine.exe and redlinectl.exe into one self-contained folder, then builds the MSI
# and a zip, each with a .sha256 beside it, the pairs the in-place updater verifies.
param([string]$Configuration = 'Release', [string]$Runtime = 'win-x64', [switch]$NoZip, [switch]$NoMsi)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$out = Join-Path $root "dist\RedLine-$version-$Runtime"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

foreach ($proj in 'src\Redline.App\Redline.App.csproj', 'src\Redline.Cli\Redline.Cli.csproj') {
    dotnet publish (Join-Path $root $proj) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
foreach ($exe in 'RedLine.exe', 'redlinectl.exe') {
    if (-not (Test-Path (Join-Path $out $exe))) { throw "missing $exe in $out" }
}
Copy-Item (Join-Path $root '..\LICENSE') $out
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $out
Write-Host "Built $out"

function Write-Sha256([string]$file) {
    $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    Set-Content -NoNewline -Path "$file.sha256" -Value "$hash  $(Split-Path -Leaf $file)"
    Write-Host "sha256 $hash  $(Split-Path -Leaf $file)"
}

if (-not $NoMsi) {
    # MSI versions are numeric only, so 0.9.0-beta.1 installs as 0.9.0 and the stable replaces it
    $msiVersion = $version.Split('-')[0]
    $msiOut = Join-Path $root 'dist'
    dotnet build (Join-Path $root 'installer\Redline.Installer.wixproj') -c $Configuration --nologo -v quiet `
        -p:PublishDir=$out -p:MsiVersion=$msiVersion -p:OutputPath=$msiOut/
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
    $msi = Join-Path $msiOut "RedLine-$version-x64.msi"
    if (-not (Test-Path $msi)) { throw "missing $msi" }
    Remove-Item (Join-Path $msiOut '*.wixpdb') -ErrorAction SilentlyContinue
    Write-Host "Packaged $msi"
    Write-Sha256 $msi
}

if (-not $NoZip) {
    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Zipped $zip"
    Write-Sha256 $zip
}
