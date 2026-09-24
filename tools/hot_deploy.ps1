# Fast inner-loop deploy (same loop as Ayaan PDF): publish a self-contained Release build and copy it
# straight over the INSTALLED app in %LOCALAPPDATA%\Programs\Aryan eBook Library, skipping the installer.
#
# Unlike Ayaan PDF, this app is portable: the user's library (AryanLibrary-Data: index, covers,
# settings, log) lives INSIDE the install folder, next to the exe. So, every time:
#   1. refuse while the app runs (a locked exe gives a confusing partial copy);
#   2. back up the library database and settings to a timestamped folder OUTSIDE the install folder
#      (covers are left out: they can be hundreds of MB and a rescan rebuilds them);
#   3. copy additively, excluding AryanLibrary-Data by bare name (never a mirror, never a purge);
#   4. prove the library database is byte-identical afterwards.
# Ship real updates to other machines with build_installer.ps1; this is for testing on this machine.
#
# -Target: where the app lives when it was moved from the install folder (it is portable), for example
#   pwsh -File tools\hot_deploy.ps1 -Target 'D:\My Ebooks Data'
param([string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Aryan eBook Library'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = $Target
$data = Join-Path $target 'AryanLibrary-Data'

if (-not (Test-Path (Join-Path $target 'AryanEbookLibrary.exe'))) {
    throw "Installed app not found at $target. Install once with the setup from dist\ first."
}

$proc = Get-Process -Name 'AryanEbookLibrary' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) }
if ($proc) { throw "Aryan eBook Library is running (pid $($proc.Id)). Close it, then re-run." }

# --- 2. back up the library ---
$db = Join-Path $data 'library.db'
$dbHashBefore = $null
if (Test-Path $db) {
    $backup = Join-Path $env:LOCALAPPDATA ("Aryan eBook Library Backups\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($f in 'library.db', 'library.db-wal', 'library.db-shm', 'settings.json') {
        $src = Join-Path $data $f
        if (Test-Path $src) { Copy-Item $src $backup }
    }
    $dbHashBefore = (Get-FileHash $db).Hash
    Write-Host "==> Library backed up to $backup" -ForegroundColor Cyan
}

# --- publish into a dedicated folder that only this script writes ---
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found." }

$publishDir = Join-Path $root 'publish\hot-deploy'
if (Test-Path (Join-Path $publishDir 'AryanLibrary-Data')) {
    throw "$publishDir\AryanLibrary-Data exists (the app was run from there). Move it out, then re-run."
}
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "==> Publishing self-contained Release build..." -ForegroundColor Cyan
& $msbuild (Join-Path $root 'AryanEbookLibrary.csproj') /t:Publish /restore `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true `
    "/p:PublishDir=$publishDir\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# --- 3. additive copy, library excluded by bare name ---
Write-Host "==> Copying over $target ..." -ForegroundColor Cyan
robocopy $publishDir $target /E /XD AryanLibrary-Data /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)." }

# --- 4. the library must be untouched ---
if ($dbHashBefore -and (Get-FileHash $db).Hash -ne $dbHashBefore) {
    throw "library.db changed during the deploy! Restore it from $backup"
}

$dll = Get-Item (Join-Path $target 'AryanEbookLibrary.dll')
Write-Host "==> Deployed $($dll.VersionInfo.ProductVersion) ($($dll.LastWriteTime)) to $target" -ForegroundColor Green
exit 0   # robocopy's "files copied" code (1) would otherwise leak out as a failure
