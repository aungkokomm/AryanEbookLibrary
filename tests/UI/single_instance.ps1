param([string]$App, [string]$OtherApp)
# One copy per library: a second start of the same copy hands over and exits; another copy with its own data runs.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
foreach ($a in $App, $OtherApp) { if ($a -notlike "$([IO.Path]::GetTempPath())*") { throw "only scratch copies: $a" } }
function Running([string]$dir) { @(Get-Process AryanEbookLibrary -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir\*" }) }
function StartOne([string]$dir) {
    $p = Start-Process (Join-Path $dir 'AryanEbookLibrary.exe') -WorkingDirectory $dir -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
    if ($h -ne [IntPtr]::Zero) { Park $h 1600 1000 }
    return $p
}
function CloseAll([string]$dir) {
    foreach ($p in Running $dir) { [void][W7]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); [void]$p.WaitForExit(15000) }
}
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }

$first = StartOne $App
Start-Sleep -Seconds 3
$second = Start-Process (Join-Path $App 'AryanEbookLibrary.exe') -WorkingDirectory $App -PassThru
$gone = $second.WaitForExit(20000)
Check ($gone -and $second.ExitCode -eq 0) "a second start of the same copy exits by itself (exit $(if ($gone) { $second.ExitCode } else { 'still running' }))"
Check (-not $first.HasExited -and (Running $App).Count -eq 1) "the first window is still open, and only one copy runs ($((Running $App).Count))"
$log = Get-Content (Join-Path $App 'AryanLibrary-Data\aryan.log') -Tail 5
Check (($log -join "`n") -match 'already open') "the log says why the second start closed"

$other = StartOne $OtherApp
Start-Sleep -Seconds 3
Check (-not $other.HasExited -and (Running $OtherApp).Count -eq 1) "a copy with its own data folder runs alongside"
CloseAll $OtherApp

CloseAll $App
Check ((Running $App).Count -eq 0) "the first copy closed"
$again = StartOne $App
Start-Sleep -Seconds 3
Check (-not $again.HasExited) "after closing, the same copy starts again"
CloseAll $App
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
