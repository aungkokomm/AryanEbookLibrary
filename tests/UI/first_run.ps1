param([string]$App, [string]$Books, [string]$Out)
# A brand-new library: the empty page's button, the folder picker, the scan, and the books shown after it.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
foreach ($p in $App, $Books) { if ($p -notlike "$([IO.Path]::GetTempPath())*") { throw "only scratch folders: $p" } }
if (Test-Path (Join-Path $App 'AryanLibrary-Data')) { throw "not a new library: $App\AryanLibrary-Data exists" }
New-Item -ItemType Directory -Force $Out | Out-Null
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }
$proc = Start-Process (Join-Path $App 'AryanEbookLibrary.exe') -WorkingDirectory $App -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $proc.Refresh(); $h = $proc.MainWindowHandle }
try {
    Park $h 1600 1000
    Start-Sleep -Seconds 5
    Capture $h (Join-Path $Out 'fr1-empty.png')
    Check ($null -ne (Find $h 'Add a folder with books' 'Button')) "the empty library offers a button to add a folder"
    [void](Act $h 'Add a folder with books' 'Button')

    # The Windows App SDK's folder picker is an ordinary dialog inside the app (issue #1). It opens at the top left of
    # the screen whatever the owner's position, so it is parked the moment it appears; off screen, UI Automation does
    # not see its lower half, so the folder goes into its box and Select Folder is pressed by window messages.
    $dialog = [IntPtr]::Zero
    for ($i = 0; $i -lt 500 -and $dialog -eq [IntPtr]::Zero; $i++) {
        foreach ($w in [W7]::Windows([uint32]$proc.Id)) { if ([W7]::Class($w) -eq '#32770') { $dialog = $w } }
        if ($dialog -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 20 }
    }
    Check ($dialog -ne [IntPtr]::Zero) "the button opens the folder picker"
    if ($dialog -ne [IntPtr]::Zero) {
        [void][W7]::SetWindowPos($dialog, [IntPtr]::Zero, $script:VS.Left - 2600, 1100, 0, 0, 0x0015)
        Start-Sleep -Seconds 2
        $box = [W7]::FindWindowEx($dialog, [IntPtr]::Zero, 'Edit', $null)
        $select = [W7]::FindWindowEx($dialog, [IntPtr]::Zero, 'Button', 'Select Folder')
        Check ($box -ne [IntPtr]::Zero -and $select -ne [IntPtr]::Zero) "the picker has its folder box and Select Folder"
        [void][W7]::SendMessage($box, 0x000C, [IntPtr]::Zero, $Books)       # WM_SETTEXT
        Start-Sleep -Milliseconds 500
        [void][W7]::SendMessage($select, 0x00F5, [IntPtr]::Zero, $null)     # BM_CLICK
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
