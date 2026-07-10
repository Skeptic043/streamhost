# Builds the shippable StreamHost zip: self-contained exe (no .NET install
# needed) + bundled ffmpeg + setup/start scripts + README.
param([string]$Version)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Missing -Version. Usage: build-package.ps1 -Version X.Y   (e.g. -Version 0.14)"
}

$commit = (git rev-parse --short HEAD).Trim()

Write-Host "Cleaning previous output..."
if (Test-Path dist/StreamHost) { Remove-Item dist/StreamHost -Recurse -Force }

Write-Host "Publishing self-contained exe..."
dotnet publish src/StreamHost -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:SourceRevisionId=$commit `
    -o dist/StreamHost | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "Bundling ffmpeg..."
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
Copy-Item $ffmpeg dist/StreamHost/ffmpeg.exe -Force

Write-Host "Recording build info..."
$ffmpegVersionLine = (& dist/StreamHost/ffmpeg.exe -version | Select-Object -First 1)
$ffmpegHash = (Get-FileHash dist/StreamHost/ffmpeg.exe -Algorithm SHA256).Hash
$buildDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
$buildInfo = @(
    "StreamHost version: $Version"
    "Git commit: $commit"
    "Build date: $buildDate"
    "ffmpeg version: $ffmpegVersionLine"
    "ffmpeg SHA256: $ffmpegHash"
)
Set-Content -Path dist/StreamHost/build-info.txt -Value $buildInfo

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
