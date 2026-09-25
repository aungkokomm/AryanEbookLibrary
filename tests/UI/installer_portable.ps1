param([string]$Setup, [string]$Work)
# The installer's portable mode, silently: a new portable copy leaves no trace in Windows and starts with its own library
# beside it; updating a portable copy that has a library replaces the app and keeps the library byte for byte.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($Work -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch folder: $Work" }
New-Item -ItemType Directory -Force $Work | Out-Null
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }

# Everything an install would leave in Windows, as text to compare.
function Traces {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A411A052-9A90-4579-8B49-5DFFC1F4ED7B}_is1'
    $reg = if (Test-Path $key) { (Get-ItemProperty $key | Out-String) } else { 'no uninstall key' }
    $hklm = Test-Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A411A052-9A90-4579-8B49-5DFFC1F4ED7B}_is1'
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Aryan eBook Library'
    $menuItems = if (Test-Path $menu) { (Get-ChildItem $menu | ForEach-Object { "$($_.Name) $($_.LastWriteTimeUtc.Ticks)" }) -join ';' } else { 'no menu' }
    $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Aryan eBook Library.lnk'
    $deskItem = if (Test-Path $desk) { (Get-Item $desk).LastWriteTimeUtc.Ticks } else { 'no shortcut' }
    $programs = Join-Path $env:LOCALAPPDATA 'Programs\Aryan eBook Library'
    $progItems = if (Test-Path $programs) { @(Get-ChildItem $programs -Force).Count } else { 'no folder' }
    "$reg|$hklm|$menuItems|$deskItem|$progItems"
}
function Install([string]$dir, [string]$log) {
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/PORTABLE', "/DIR=`"$dir`"", "/LOG=`"$log`"" -PassThru
    if (-not $p.WaitForExit(300000)) { throw 'setup did not finish' }
    return $p.ExitCode
}

# 1. a new portable copy
$before = Traces
$dir = Join-Path $Work 'portable-new'
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
$code = Install $dir (Join-Path $Work 'portable-new.log')
Check ($code -eq 0) "setup finished (exit $code)"
Check (Test-Path (Join-Path $dir 'AryanEbookLibrary.exe')) "the app is in the chosen folder"
Check (Test-Path (Join-Path $dir 'THIRD-PARTY-NOTICES.txt')) "with its notices"
Check (-not (Get-ChildItem $dir -Filter 'unins*' -ErrorAction SilentlyContinue)) "no uninstaller"
Check ((Traces) -eq $before) "nothing changed in Windows (uninstall entry, Start menu, desktop, Programs folder)"
Check (-not (Test-Path (Join-Path $dir 'AryanLibrary-Data'))) "no library shipped"

$p = Start-Process (Join-Path $dir 'AryanEbookLibrary.exe') -WorkingDirectory $dir -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
Park $h 1400 900
Start-Sleep -Seconds 5
[void][W7]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
[void]$p.WaitForExit(15000)
Check (Test-Path (Join-Path $dir 'AryanLibrary-Data\library.db')) "it starts, and keeps its library beside itself"

# 2. updating a portable copy that has a library
$data = Join-Path $dir 'AryanLibrary-Data'
$hashes = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -ne 'aryan.log' } | ForEach-Object { "$($_.FullName.Substring($data.Length)) $((Get-FileHash $_.FullName).Hash)" }
(Get-Item (Join-Path $dir 'AryanEbookLibrary.dll')).LastWriteTimeUtc = [DateTime]::new(2000, 1, 1)   # an "old" app
$code = Install $dir (Join-Path $Work 'portable-update.log')
Check ($code -eq 0) "updating the copy finished (exit $code)"
Check ((Get-Item (Join-Path $dir 'AryanEbookLibrary.dll')).LastWriteTimeUtc.Year -gt 2000) "the app was replaced"
$after = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -ne 'aryan.log' } | ForEach-Object { "$($_.FullName.Substring($data.Length)) $((Get-FileHash $_.FullName).Hash)" }
Check ((Compare-Object $hashes $after) -eq $null -and $hashes.Count -gt 0) "the library is untouched ($($hashes.Count) files, same hashes)"
Check ((Traces) -eq $before) "and still nothing in Windows"
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
