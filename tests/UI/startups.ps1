param([string]$App, [int]$Runs = 20, [int]$WaitSeconds = 8, [switch]$Rewrite)
# Starts a scratch copy of Aryan off-screen again and again and counts the starts where it dies by itself.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($App -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch copy: $App" }
$exe = Join-Path $App 'AryanEbookLibrary.exe'
$died = 0
for ($run = 1; $run -le $Runs; $run++) {
    # What a running scan does: covers rewritten in place while the cards open them.
    $writer = if ($Rewrite) { Start-Process python -ArgumentList @((Join-Path $PSScriptRoot 'rewrite_covers.py'), "`"$(Join-Path $App 'AryanLibrary-Data')`"", '12') -WindowStyle Hidden -PassThru }
    $p = Start-Process $exe -WorkingDirectory $App -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
    if ($h -ne [IntPtr]::Zero) { Park $h 1919 1018 }
    Start-Sleep -Seconds $WaitSeconds
    if ($p.HasExited) {
        $died++
        "run {0,2}: DIED by itself, exit 0x{1:X8}" -f $run, $p.ExitCode
        if ($writer) { [void]$writer.WaitForExit(20000) }
        continue
    }
    [void][W7]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $p.WaitForExit(15000)) { "run {0,2}: did not close, stopped" -f $run; $p.Kill(); $p.WaitForExit() }
    else { "run {0,2}: ok, closed 0x{1:X8}" -f $run, $p.ExitCode }
    if ($writer) { [void]$writer.WaitForExit(20000) }
}
"$died of $Runs starts died by themselves"
