param([string]$Setup, [string]$Work, [string]$Out)
# Updating a portable copy through the wizard, as a user does it: Browse to the folder that holds Aryan, which makes
# Setup add "Aryan eBook Library" to it. Setup must ask, Yes must update the copy there, and its library must be kept
# byte for byte. Portable into a temp folder only, off-screen: nothing reaches Windows.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($Work -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch folder: $Work" }
New-Item -ItemType Directory -Force $Out | Out-Null
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }
$root = [Windows.Automation.AutomationElement]::RootElement
$button = [Windows.Automation.ControlType]::Button

# A portable copy with a library, made silently first.
$copy = Join-Path $Work 'My Ebooks Data'
if (-not (Test-Path (Join-Path $copy 'AryanEbookLibrary.exe'))) {
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/PORTABLE', "/DIR=`"$copy`"" -PassThru
    [void]$p.WaitForExit(300000)
}
$data = Join-Path $copy 'AryanLibrary-Data'
New-Item -ItemType Directory -Force (Join-Path $data 'Covers') | Out-Null
Set-Content (Join-Path $data 'library.db') 'stands in for the real library' -NoNewline
Set-Content (Join-Path $data 'Covers\cover.png') 'a cover' -NoNewline
$hashes = Get-ChildItem $data -Recurse -File | ForEach-Object { "$($_.FullName.Substring($data.Length)) $((Get-FileHash $_.FullName).Hash)" }
(Get-Item (Join-Path $copy 'AryanEbookLibrary.dll')).LastWriteTimeUtc = [DateTime]::new(2000, 1, 1)

function Find([string]$name, [Windows.Automation.ControlType]$type) {
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($setupIds -notcontains $w.Current.ProcessId) { continue }
        foreach ($e in $w.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))) {
            if ($e.Current.Name.StartsWith($name)) { return $e }
        }
    }
}
function Wait([string]$name, [Windows.Automation.ControlType]$type, [int]$seconds = 20) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { $e = Find $name $type; if ($e) { return $e }; Start-Sleep -Milliseconds 500 }
}
function Press([string]$name) { (Wait $name $button).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
function Tick($item) {
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $lh = [IntPtr][Windows.Automation.TreeWalker]::RawViewWalker.GetParent($item).Current.NativeWindowHandle
    [void][W7]::PostMessage($lh, 0x0100, [IntPtr]0x20, [IntPtr]0x00390001)
    [void][W7]::PostMessage($lh, 0x0101, [IntPtr]0x20, [IntPtr]0xC0390001)
    Start-Sleep -Milliseconds 800
}

$p = Start-Process $Setup -PassThru
$setupIds = @()
for ($i = 0; $i -lt 60 -and -not $wizard; $i++) {
    Start-Sleep -Milliseconds 500
    $setupIds = @(Get-Process | Where-Object { $_.ProcessName -like "$([IO.Path]::GetFileNameWithoutExtension($Setup))*" }).Id
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.Name.StartsWith('Setup - Aryan eBook Library')) { $wizard = $w }
    }
}
$h = [IntPtr]$wizard.Current.NativeWindowHandle
[void][W7]::SetWindowPos($h, [IntPtr]::Zero, $script:VS.Left - 2600, 0, 0, 0, 0x0015)
try {
    Tick (Wait 'Portable' ([Windows.Automation.ControlType]::ListItem))
    Press 'Next'
    # What Browse gives when the user picks the folder that holds Aryan.
    $edit = Wait '' ([Windows.Automation.ControlType]::Edit)
    $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $copy 'Aryan eBook Library'))
    Press 'Next'
    $yes = Wait 'Yes' $button 10
    Check ($null -ne $yes) "Setup asks when the folder above holds Aryan"
    Capture $h (Join-Path $Out 'update-1-question.png')
    $yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 1
    Check ($null -ne (Wait 'Install' $button 10)) "Yes goes on to the Ready page"
    Capture $h (Join-Path $Out 'update-2-ready.png')
    Press 'Install'
    $finish = Wait 'Finish' $button 300
    Check ($null -ne $finish) "the update finishes"
    $launch = Find 'Launch' ([Windows.Automation.ControlType]::ListItem)
    if ($launch) { Tick $launch }   # do not start the app from the finish page
    Capture $h (Join-Path $Out 'update-3-finish.png')
    $finish.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    [void]$p.WaitForExit(20000)
}
finally {
    if (-not $p.HasExited) {
        Press 'Cancel'
        $y = Wait 'Yes' $button 10
        if ($y) { $y.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
        [void]$p.WaitForExit(20000)
    }
}
Start-Sleep -Seconds 2
$started = Get-Process AryanEbookLibrary -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$copy\*" }
if ($started) { $started | ForEach-Object { [void]$_.CloseMainWindow(); [void]$_.WaitForExit(15000) }; "(closed the copy the finish page started)" }
Check ((Get-Item (Join-Path $copy 'AryanEbookLibrary.dll')).LastWriteTimeUtc.Year -gt 2000) "the app in the folder was updated"
Check (-not (Test-Path (Join-Path $copy 'Aryan eBook Library'))) "no second copy inside it"
$after = Get-ChildItem $data -Recurse -File | ForEach-Object { "$($_.FullName.Substring($data.Length)) $((Get-FileHash $_.FullName).Hash)" }
Check ((Compare-Object $hashes $after) -eq $null) "the library is untouched ($($hashes.Count) files, same hashes)"
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
