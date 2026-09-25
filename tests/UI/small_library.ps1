param([string]$Work)
# A fresh app folder and a one-book folder under %TEMP%, for probes that need the online lookups to finish quickly.
$ErrorActionPreference = 'Stop'
if ($Work -notlike "$([IO.Path]::GetTempPath())*") { throw "only a scratch folder: $Work" }
$app = Join-Path $Work 'app'; $books = Join-Path $Work 'books'
New-Item -ItemType Directory -Force $books | Out-Null
robocopy "E:\Aryan\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64" $app /E /NFL /NDL /NJH /NJS /NP /XD AryanLibrary-Data | Out-Null

# One page, no metadata: title and author come from the file name, so the lookups have something to do.
$objs = @(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    $null,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
$text = 'BT /F1 18 Tf 40 300 Td (Pride and Prejudice) Tj ET'
$objs[3] = "<< /Length $($text.Length) >>`nstream`n$text`nendstream"
$sb = [Text.StringBuilder]::new("%PDF-1.4`n")
$offsets = @()
for ($i = 0; $i -lt $objs.Count; $i++) { $offsets += $sb.Length; [void]$sb.Append("$($i + 1) 0 obj`n$($objs[$i])`nendobj`n") }
$xref = $sb.Length
[void]$sb.Append("xref`n0 $($objs.Count + 1)`n0000000000 65535 f `n")
foreach ($o in $offsets) { [void]$sb.Append(('{0:D10} 00000 n ' -f $o) + "`n") }
[void]$sb.Append("trailer`n<< /Size $($objs.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
[IO.File]::WriteAllText((Join-Path $books 'Jane Austen - Pride and Prejudice.pdf'), $sb.ToString(), [Text.Encoding]::ASCII)
"app $app"
"books $books"
"next: first_run.ps1 -App $app -Books $books -Out <folder>"
