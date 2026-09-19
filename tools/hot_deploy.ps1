# Fast inner-loop deploy: publish a self-contained Release build and copy it into
# E:\Aryan\testrun, a throwaway test copy inside the project (never a real install).
#
# The app is portable: its library (SQLite index, covers, settings, log) lives in
# AryanLibrary-Data next to the exe. That folder is excluded by bare name so a deploy
# can never overwrite the test library, even if a stray one exists in the publish
# output. No purge: stale files are harmless and never worth risking data for.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'testrun'
$publishDir = Join-Path $root 'publish\AryanEbookLibrary'

# Refuse to clobber a running app: a locked exe gives a confusing partial copy.
$proc = Get-Process -Name 'AryanEbookLibrary' -ErrorAction SilentlyContinue
if ($proc) {
    throw "Aryan eBook Library is running (pid $($proc.Id)). Close it, then re-run."
}

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found." }

Write-Host "==> Publishing self-contained Release build..." -ForegroundColor Cyan
& $msbuild (Join-Path $root 'AryanEbookLibrary.csproj') /t:Publish /restore `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true `
    "/p:PublishDir=$publishDir\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

Write-Host "==> Copying into $target ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $target | Out-Null
# /E recurse, no purge; exclude the portable library folder by bare name.
robocopy $publishDir $target /E /XD AryanLibrary-Data /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)." }

$dll = Get-Item (Join-Path $target 'AryanEbookLibrary.dll')
Write-Host "==> Deployed $($dll.VersionInfo.FileVersion) ($($dll.LastWriteTime)) to $target" -ForegroundColor Green
exit 0   # robocopy's "files copied" code (1) would otherwise leak out as a failure
