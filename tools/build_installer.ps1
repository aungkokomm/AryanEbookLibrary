# Builds the Aryan eBook Library installer end to end:
#   1. Publishes an unpackaged, self-contained Release build into publish\installer-payload
#   2. Compiles installer\AryanEbookLibrary.iss with Inno Setup into dist\
#
# Usage:  pwsh -File tools\build_installer.ps1
# Requires: Visual Studio MSBuild and Inno Setup 6 (ISCC.exe).
#
# One installer per version: an existing dist\AryanEbookLibrary-Setup-<version>.exe is never
# overwritten. Bump <Version> in AryanEbookLibrary.csproj first.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio with the .NET desktop workload." }
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php" }

$csproj = Join-Path $root 'AryanEbookLibrary.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$setupPath = Join-Path $root "dist\AryanEbookLibrary-Setup-$version.exe"
if (Test-Path $setupPath) {
    throw "$setupPath already exists. Bump <Version> in AryanEbookLibrary.csproj for a new installer."
}

# A dedicated payload folder that only this script writes, cleaned so no stale file rides along.
# It is never run from, so it can never hold a library; refuse rather than delete one if it does.
$payload = Join-Path $root 'publish\installer-payload'
if (Test-Path (Join-Path $payload 'AryanLibrary-Data')) {
    throw "$payload\AryanLibrary-Data exists (the app was run from there). Move it out, then re-run."
}
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }

Write-Host "==> Publishing self-contained Release build $version..." -ForegroundColor Cyan
& $msbuild $csproj /t:Publish /restore `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true `
    "/p:PublishDir=$payload\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# Must match ExeName in the .iss, which reads the version off it.
$exe = Join-Path $payload 'AryanEbookLibrary.exe'
if (-not (Test-Path $exe)) { throw "AryanEbookLibrary.exe missing from publish output." }
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($exeVersion -ne $version) { throw "Published exe says '$exeVersion' but the csproj says '$version'." }
foreach ($asset in 'Assets\app.ico', 'Assets\AppIcon.png', 'AryanEbookLibrary.pri') {
    if (-not (Test-Path (Join-Path $payload $asset))) { throw "$asset missing from publish output." }
}

Write-Host "==> Compiling installer with Inno Setup..." -ForegroundColor Cyan
& $iscc (Join-Path $root 'installer\AryanEbookLibrary.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $setupPath)) { throw "Expected $setupPath after a successful compile." }
$setup = Get-Item $setupPath
Write-Host ("==> Done: {0} ({1} MB)" -f $setup.FullName, [math]::Round($setup.Length / 1MB, 1)) -ForegroundColor Green
exit 0
