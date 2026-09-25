param([string]$App, [string]$Books, [string]$Out)
# A brand-new library: the empty page's button, the folder picker, the scan, and the books shown after it.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
foreach ($p in $App, $Books) { if ($p -notlike "$([IO.Path]::GetTempPath())*") { throw "only scratch folders: $p" } }
if (Test-Path (Join-Path $App 'AryanLibrary-Data')) { throw "not a new library: $App\AryanLibrary-Data exists" }
New-Item -ItemType Directory -Force $Out | Out-Null
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }
$root = [Windows.Automation.AutomationElement]::RootElement
function FindAnywhere([string]$name, [Windows.Automation.ControlType]$type) {
    $cond = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        $hit = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if ($hit) { return $hit }
    }
    return $null
}

$proc = Start-Process (Join-Path $App 'AryanEbookLibrary.exe') -WorkingDirectory $App -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $proc.Refresh(); $h = $proc.MainWindowHandle }
try {
    Park $h 1600 1000
    Start-Sleep -Seconds 5
    Capture $h (Join-Path $Out 'fr1-empty.png')
    Check ($null -ne (Find $h 'Add a folder with books' 'Button')) "the empty library offers a button to add a folder"
    [void](Act $h 'Add a folder with books' 'Button')

    $select = $null
    for ($i = 0; $i -lt 20 -and -not $select; $i++) { Start-Sleep -Milliseconds 500; $select = FindAnywhere 'Select Folder' ([Windows.Automation.ControlType]::Button) }
    Check ($null -ne $select) "the button opens the folder picker"
    if ($select) {
        $dialog = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($select)
        while ($dialog -and $dialog.Current.ControlType -ne [Windows.Automation.ControlType]::Window) { $dialog = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($dialog) }
        $box = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Folder:'),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
        $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Books)
        Start-Sleep -Milliseconds 500
        $select.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        "picked $Books"
    }

    Start-Sleep -Seconds 20
    Capture $h (Join-Path $Out 'fr2-after.png')
    $log = Get-Content (Join-Path $App 'AryanLibrary-Data\aryan.log') -Raw
    Check ($null -ne (Find $h 'All Books' 'ListItem')) "All Books is there"
    Check ($null -eq (Find $h 'Add a folder with books' 'Button')) "and the button is gone once books are in"
}
finally {
    [void][W7]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Seconds 5
    if (-not $proc.HasExited) { "still running after close request" } else { "closed cleanly" }
}
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
