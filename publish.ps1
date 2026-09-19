# Builds the portable, self-contained release (no installer, no runtime needed on the target PC).
# Run from this folder in PowerShell:  .\publish.ps1
$ErrorActionPreference = "Stop"
dotnet publish .\AryanEbookLibrary.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o .\publish\AryanEbookLibrary
Compress-Archive -Path .\publish\AryanEbookLibrary\* -DestinationPath .\publish\AryanEbookLibrary-portable.zip -Force
Write-Host "Done: publish\AryanEbookLibrary-portable.zip"
