# Builds the shippable StreamHost zip: self-contained exe (no .NET install
# needed) + bundled ffmpeg + setup/start scripts + README.
param([string]$Version = "0.2")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Publishing self-contained exe..."
dotnet publish src/StreamHost -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o dist/StreamHost | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "Bundling ffmpeg..."
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
Copy-Item $ffmpeg dist/StreamHost/ffmpeg.exe -Force

Write-Host "Adding scripts + README..."
Copy-Item packaging/setup.bat, packaging/start-stream.bat, packaging/README.txt dist/StreamHost/ -Force

# Publish drops debug symbols we don't need to ship
Remove-Item dist/StreamHost/*.pdb -ErrorAction SilentlyContinue

$zip = "dist/StreamHost-v$Version.zip"
Write-Host "Zipping -> $zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path dist/StreamHost/* -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"
