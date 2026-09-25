param([string]$App, [string]$Out, [int]$Seconds = 60)
# The status bar after the online lookups end (1.0.3: Google's "daily allowance used up" kept it up all day). A made-up
# Google key makes Google refuse at once, the same path as a used-up day. Watches the bar through UIA (a collapsed bar is
# not in the tree) and prints what it shows over time. Use a small library (small_library.ps1 + first_run.ps1), or the
# lookups before Google's take hours. Sends one book's title to Open Library, Wikidata and Google. Settings restored after.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($App -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch copy: $App" }
New-Item -ItemType Directory -Force $Out | Out-Null
$sf = Join-Path $App 'AryanLibrary-Data\settings.json'
$Backup = Join-Path $Out 'settings.json.bak'
Copy-Item $sf $Backup -Force
try {
    $j = Get-Content $Backup -Raw | ConvertFrom-Json
    $j.LookupOnline = $true; $j.GoogleBooksKey = 'not-a-real-key'; $j.AutoScanOnStart = $true
    $j | ConvertTo-Json -Depth 10 | Set-Content $sf -Encoding utf8
    $proc = Start-Process (Join-Path $App 'AryanEbookLibrary.exe') -WorkingDirectory $App -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $proc.Refresh(); $h = $proc.MainWindowHandle }
    Park $h 1600 1000
    $start = Get-Date; $last = $null; $googleSeen = $null
    while (((Get-Date) - $start).TotalSeconds -lt $Seconds) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($h)
        $texts = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))
        $status = @($texts | Where-Object { $_.Current.Name -match '^(Scan |Scanning|Open Library|Google Books|Wikidata|Wikipedia)' } | ForEach-Object { $_.Current.Name }) -join ' | '
        $t = [int]((Get-Date) - $start).TotalSeconds
        if ($status -ne $last) { "{0,4}s  {1}" -f $t, ($(if ($status) { $status } else { '(status bar hidden)' })); $last = $status }
        if ($status -match 'Google Books (did not|.*daily)' -and -not $googleSeen) { $googleSeen = $t }
        Start-Sleep -Seconds 1
    }
    $t = [int]((Get-Date) - $start).TotalSeconds
    Capture $h (Join-Path $Out 'statusbar-end.png')
    if (-not $googleSeen) { "FAIL Google's refusal never showed in $t s" }
    elseif ($last) { "FAIL the bar is stuck: still showing at $t s, Google's refusal first shown at $googleSeen s" }
    else { "PASS the bar hid after Google's refusal (first shown at $googleSeen s)" }
}
finally {
    if ($h -ne [IntPtr]::Zero) { [void][W7]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
    if ($proc) { [void]$proc.WaitForExit(15000) }
    Copy-Item $Backup $sf -Force
}
