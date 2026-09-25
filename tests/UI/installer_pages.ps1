param([string]$Setup, [string]$Out, [ValidateSet('portable', 'install', 'switch')][string]$Mode)
# Walks the installer's pages off-screen and CANCELS at the Ready page: nothing is installed.
#   portable: choose Portable;  install: keep "Install for me";  switch: Portable, Back, then Install again.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
New-Item -ItemType Directory -Force $Out | Out-Null
$root = [Windows.Automation.AutomationElement]::RootElement
$button = [Windows.Automation.ControlType]::Button
$listItem = [Windows.Automation.ControlType]::ListItem
function Wizard {
    for ($i = 0; $i -lt 60; $i++) {
        foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.Name.StartsWith('Setup - Aryan eBook Library')) { return $w }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'no Setup window'
}
function Named($within, [string]$prefix, [Windows.Automation.ControlType]$type) {
    foreach ($e in $within.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))) {
        if ($e.Current.Name.StartsWith($prefix)) { return $e }
    }
    return $null
}
function Press([string]$name) {
    $e = Named $w $name $button
    if (-not $e) { throw "no button $name" }
    $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
}
# Inno's option list only lets UIA move the highlight; Space, as a keyboard user presses it, ticks the option.
function Choose([string]$prefix) {
    $item = Named $w $prefix $listItem
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $lh = [IntPtr][Windows.Automation.TreeWalker]::RawViewWalker.GetParent($item).Current.NativeWindowHandle
    [void][W7]::PostMessage($lh, 0x0100, [IntPtr]0x20, [IntPtr]0x00390001)
    [void][W7]::PostMessage($lh, 0x0101, [IntPtr]0x20, [IntPtr]0xC0390001)
    Start-Sleep -Milliseconds 800
    "chose: $prefix"
}
function Folder { (Named $w '' ([Windows.Automation.ControlType]::Edit)).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }

$p = Start-Process $Setup -PassThru
$w = Wizard
$h = [IntPtr]$w.Current.NativeWindowHandle
[void][W7]::SetWindowPos($h, [IntPtr]::Zero, $script:VS.Left - 2600, 0, 0, 0, 0x0015)   # off-screen, same size
Start-Sleep -Seconds 1
try {
    Capture $h (Join-Path $Out "$Mode-1-choice.png")
    if ($Mode -ne 'install') { Choose 'Portable' }
    Press 'Next'
    "folder offered: $(Folder)"
    if ($Mode -eq 'switch') {
        Press 'Back'
        Choose 'Install for me'
        Press 'Next'
        "folder offered after switching back: $(Folder)"
    }
    Capture $h (Join-Path $Out "$Mode-2-folder.png")
    $pages = 0
    while (-not (Named $w 'Install' $button) -and $pages -lt 3) {
        Press 'Next'
        $pages++
        if (-not (Named $w 'Install' $button)) { "a page between the folder and Ready (the shortcut page)"; Capture $h (Join-Path $Out "$Mode-3-between.png") }
    }
    if (-not (Named $w 'Install' $button)) { throw 'never reached the Ready page' }
    "reached the Ready page after $pages click(s) from the folder page"
    Capture $h (Join-Path $Out "$Mode-4-ready.png")
}
finally {
    Press 'Cancel'
    $yes = $null
    for ($i = 0; $i -lt 20 -and -not $yes; $i++) { Start-Sleep -Milliseconds 500; $yes = Named $w 'Yes' $button }
    if ($yes) { $yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    [void]$p.WaitForExit(20000)
    "cancelled, setup exited: $($p.HasExited)"
}
