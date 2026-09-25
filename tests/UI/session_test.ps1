param([string]$App, [string]$Out)
# How a session ended is known at the next start: normal close, a kill (as a crash), Windows ending the session.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($App -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch copy: $App" }
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -Namespace T -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
$data = Join-Path $App 'AryanLibrary-Data'
$marker = Join-Path $data 'aryan.running'
$logFile = Join-Path $data 'aryan.log'
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }
function LogSince([int]$lines) { @(Get-Content $logFile)[$lines..100000] -join "`n" }
function StartApp {
    $p = Start-Process (Join-Path $App 'AryanEbookLibrary.exe') -WorkingDirectory $App -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
    Park $h 1600 1000
    Start-Sleep -Seconds 6
    return @{ P = $p; H = $h }
}
function CloseApp($a) { [void][W7]::PostMessage($a.H, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); [void]$a.P.WaitForExit(15000) }

# 1. a normal session
if (Test-Path $marker) { Remove-Item $marker }
$n = @(Get-Content $logFile).Count
$a = StartApp
Check (Test-Path $marker) "the marker is there while Aryan runs"
CloseApp $a
Check (-not (Test-Path $marker)) "a normal close removes it"
Check ((LogSince $n) -notmatch 'did not close normally') "a normal start does not complain"

# 2. killed, as a crash would end it
$a = StartApp
Stop-Process -Id $a.P.Id -Force; $a.P.WaitForExit()
Check (Test-Path $marker) "a killed session leaves the marker"
$n = @(Get-Content $logFile).Count
$a = StartApp
Capture $a.H (Join-Path $Out 'after-kill.png')
Check ((LogSince $n) -match 'did not close normally') "the next start logs that the last session ended badly"
$dialog = Find $a.H 'Aryan did not close normally last time'
Check ($null -ne $dialog) "and tells the user in a dialog"
[void](Act $a.H 'OK' 'Button')
Start-Sleep -Seconds 1
CloseApp $a
Check (-not (Test-Path $marker)) "closing after that removes the marker"

# 3. Windows ends the session, then the process is ended
$a = StartApp
$n = @(Get-Content $logFile).Count
[void][T.U]::SendMessage($a.H, 0x0016, [IntPtr]1, [IntPtr]::Zero)   # WM_ENDSESSION, TRUE
Start-Sleep -Seconds 2
Stop-Process -Id $a.P.Id -Force; $a.P.WaitForExit()
$said = LogSince $n
Check ($said -match 'Windows is ending the session' -and $said -match 'app: closed') "Windows ending the session saves and closes first"
Check (-not (Test-Path $marker)) "so no marker is left"
$n = @(Get-Content $logFile).Count
$a = StartApp
Check ((LogSince $n) -notmatch 'did not close normally') "and the next start does not complain"
CloseApp $a
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
