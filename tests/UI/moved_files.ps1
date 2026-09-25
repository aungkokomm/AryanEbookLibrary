param([string]$Work)
# Files sorted by hand in Explorer: a one-book library whose book is a favorite with a note; the file is moved into a
# subfolder, then renamed there. After each, a start (its scan) must keep the book, favorite and note on the new path,
# with nothing left missing and the folder's sidecar naming the new path. Temp folders only, off-screen.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia.ps1')
if ($Work -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch folder: $Work" }
$fails = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "PASS $what" } else { "FAIL $what"; $script:fails++ } }

& (Join-Path $PSScriptRoot 'small_library.ps1') -Work $Work | Out-Null
$app = Join-Path $Work 'app'; $books = Join-Path $Work 'books'
& (Join-Path $PSScriptRoot 'first_run.ps1') -App $app -Books $books -Out (Join-Path $Work 'shots') | Select-Object -Last 1
$db = Join-Path $app 'AryanLibrary-Data\library.db'
$log = Join-Path $app 'AryanLibrary-Data\aryan.log'

function Sql([string]$sql) {
    python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); r=c.execute(sys.argv[2]).fetchall(); c.commit(); print('|'.join(str(x) for row in r for x in row))" $db $sql
}
Sql "INSERT INTO book_state (key, drive_id, rel_path, is_favorite, notes, updated_utc) SELECT state_key, drive_id, rel_path, 1, 'my note', '$((Get-Date).ToUniversalTime().ToString('o'))' FROM books" | Out-Null

function Start-And-Scan([string]$what) {
    $before = @(Select-String -Path $log -Pattern 'Scan complete|Moved:').Count
    $proc = Start-Process (Join-Path $app 'AryanEbookLibrary.exe') -WorkingDirectory $app -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 100 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $proc.Refresh(); $h = $proc.MainWindowHandle }
    Park $h 1600 1000
    $status = $null
    for ($i = 0; $i -lt 60 -and -not $status; $i++) {
        Start-Sleep -Milliseconds 500
        $status = (Find $h 'Scan complete' 'Text')
        if (-not $status) {
            $root = [Windows.Automation.AutomationElement]::FromHandle($h)
            foreach ($t in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) {
                if ($t.Current.Name.StartsWith('Scan complete')) { $status = $t; break }
            }
        }
    }
    $text = if ($status) { $status.Current.Name } else { '' }
    "  status: $text"
    Check ($text -match '^Scan complete: 0 new, .*1 moved') "$what`: the scan says 1 moved, not new"
    Start-Sleep -Seconds 4   # the sidecar is written 2 s after the move
    Capture $h (Join-Path $Work "moved-$($what -replace '\W', '').png")
    [void][W7]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    [void]$proc.WaitForExit(20000)
}

function Expect([string]$sub, [string]$what) {
    $f = @((Sql "SELECT b.rel_path, b.is_missing, s.is_favorite, s.notes FROM books b LEFT JOIN book_state s ON s.key = b.state_key") -split '\|')
    "  book: $($f -join ' | ')"
    Check ($f.Count -eq 4 -and $f[0].EndsWith("\books\$sub") -and $f[1] -eq '0' -and $f[2] -eq '1' -and $f[3] -eq 'my note') "$what`: one book, at the new path, still a favorite with its note"
    $sidecar = Get-Content (Join-Path $books '.aryan-library.json') -Raw | ConvertFrom-Json
    $names = @($sidecar.Items.PSObject.Properties.Name)
    Check ($names.Count -eq 1 -and $names[0] -eq $sub) "$what`: the folder's sidecar names the new path ($($names -join ', '))"
}

$pdf = Get-ChildItem $books -Filter *.pdf | Select-Object -First 1
New-Item -ItemType Directory -Force (Join-Path $books 'Classics') | Out-Null
Move-Item $pdf.FullName (Join-Path $books "Classics\$($pdf.Name)")
Start-And-Scan 'moved into a subfolder'
Expect "Classics\$($pdf.Name)" 'moved into a subfolder'

Rename-Item (Join-Path $books "Classics\$($pdf.Name)") 'Pride and Prejudice (Austen).pdf'
Start-And-Scan 'renamed'
Expect 'Classics\Pride and Prejudice (Austen).pdf' 'renamed'

Select-String -Path $log -Pattern 'Moved:' | ForEach-Object { "  log: $($_.Line)" }
if ($fails -eq 0) { "ALL PASS" } else { "$fails FAILED" }
