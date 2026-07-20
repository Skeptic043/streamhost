# Builds the shippable Spectari zip: self-contained exe (no .NET install
# needed) + bundled ffmpeg + setup/start scripts + README.
param([string]$Version)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Missing -Version. Usage: build-package.ps1 -Version X.Y   (e.g. -Version 0.14)"
}

$commit = (git rev-parse --short HEAD).Trim()

Write-Host "Cleaning previous output..."
if (Test-Path dist/Spectari) { Remove-Item dist/Spectari -Recurse -Force }

Write-Host "Publishing self-contained exe..."
dotnet publish src/Spectari -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:SourceRevisionId=$commit `
    -o dist/Spectari | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "Bundling ffmpeg..."
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
Copy-Item $ffmpeg dist/Spectari/ffmpeg.exe -Force

Write-Host "Recording build info..."
$ffmpegVersionLine = (& dist/Spectari/ffmpeg.exe -version | Select-Object -First 1)
$ffmpegBuildconf = ""
$ffmpegBuildconfLine = (& dist/Spectari/ffmpeg.exe -version | Select-String -SimpleMatch 'configuration:' | Select-Object -First 1)
if ($ffmpegBuildconfLine) { $ffmpegBuildconf = $ffmpegBuildconfLine.ToString().Trim() }
if ([string]::IsNullOrWhiteSpace($ffmpegBuildconf)) {
    throw "Could not read the ffmpeg build configuration; refusing to guess its license."
}
if ($ffmpegBuildconf -match '(?:^|\s)--enable-nonfree(?:\s|$)') {
    throw "The selected ffmpeg build uses --enable-nonfree and cannot be redistributed."
}

Write-Host "Checking ffmpeg capabilities..."
$requiredCapabilities = @{
    '-encoders' = @('libx264', 'h264_nvenc', 'h264_amf', 'h264_qsv', 'aac')
    '-decoders' = @('rawvideo', 'pcm_f32le')
    '-demuxers' = @('rawvideo', 'f32le')
    '-devices'  = @('lavfi')
    '-filters'  = @('scale', 'testsrc')
    '-muxers'   = @('mp4', 'null')
    '-pix_fmts' = @('bgra', 'yuv420p')
    '-protocols' = @('pipe')
}
foreach ($query in $requiredCapabilities.Keys) {
    $listing = (& dist/Spectari/ffmpeg.exe -hide_banner $query 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg $query failed; refusing to package an unverified build." }
    foreach ($capability in $requiredCapabilities[$query]) {
        $pattern = "(?m)^\s*(?:[A-Z.]{1,8}\s+)?$([regex]::Escape($capability))(?:\s|$)"
        if ($listing -notmatch $pattern) {
            throw "The selected ffmpeg build does not provide required capability '$capability' ($query)."
        }
    }
}

$ffmpegHash = (Get-FileHash dist/Spectari/ffmpeg.exe -Algorithm SHA256).Hash
$sourceManifestPath = Join-Path $PSScriptRoot "packaging/ffmpeg-sources.json"
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
$ffmpegSource = @($sourceManifest.builds) |
    Where-Object { $_.sha256 -eq $ffmpegHash } |
    Select-Object -First 1
if (-not $ffmpegSource) {
    throw "No corresponding-source mapping exists for ffmpeg SHA256 $ffmpegHash; refusing to package it."
}

$buildDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
$buildInfo = @(
    "Spectari version: $Version"
    "Git commit: $commit"
    "Build date: $buildDate"
    "ffmpeg version: $ffmpegVersionLine"
    "ffmpeg SHA256: $ffmpegHash"
    "ffmpeg corresponding source: $($ffmpegSource.sourceUrl)"
    "ffmpeg source SHA256: $($ffmpegSource.sourceSha256)"
    "ffmpeg source commit: $($ffmpegSource.sourceCommit)"
)
if (-not [string]::IsNullOrWhiteSpace($ffmpegBuildconf)) {
    $buildInfo += "ffmpeg buildconf: $ffmpegBuildconf"
}
Set-Content -Path dist/Spectari/build-info.txt -Value $buildInfo

Write-Host "Adding scripts + README..."
Copy-Item packaging/setup.bat, packaging/start-stream.bat, packaging/README.txt dist/Spectari/ -Force

Write-Host "Adding license + third-party notices..."
# Spectari's own license, .txt so double-click opens it in Notepad.
Copy-Item LICENSE dist/Spectari/LICENSE.txt -Force

# Generated at package time so it describes the actual bundled ffmpeg.exe.
# Derive ffmpeg's license from its build configuration (validated above).
if ($ffmpegBuildconf -match '(?:^|\s)--enable-gpl(?:\s|$)') {
    $ffmpegLicenseLine = if ($ffmpegBuildconf -match '(?:^|\s)--enable-version3(?:\s|$)') {
        "FFmpeg is used here under the GNU General Public License (GPL) version 3 or later."
    } else {
        "FFmpeg is used here under the GNU General Public License (GPL) version 2 or later."
    }
    $isGpl = $true
} elseif ($ffmpegBuildconf -match '(?:^|\s)--enable-version3(?:\s|$)') {
    $ffmpegLicenseLine = "FFmpeg is used here under the GNU Lesser General Public License (LGPL) version 3 or later."
    $isGpl = $false
} else {
    $ffmpegLicenseLine = "FFmpeg is used here under the GNU Lesser General Public License (LGPL) version 2.1 or later."
    $isGpl = $false
}

$expectedLicense = if ($isGpl -and $ffmpegBuildconf -match '(?:^|\s)--enable-version3(?:\s|$)') {
    "GPL-3.0-or-later"
} elseif ($isGpl) {
    "GPL-2.0-or-later"
} elseif ($ffmpegBuildconf -match '(?:^|\s)--enable-version3(?:\s|$)') {
    "LGPL-3.0-or-later"
} else {
    "LGPL-2.1-or-later"
}
if ($ffmpegSource.license -ne $expectedLicense) {
    throw "The ffmpeg source mapping says '$($ffmpegSource.license)' but the binary requires '$expectedLicense'."
}

$ffmpegLicensePath = Join-Path $PSScriptRoot ("packaging/" + $ffmpegSource.licenseFile)
if (-not (Test-Path -LiteralPath $ffmpegLicensePath)) {
    throw "The mapped ffmpeg license file is missing: $ffmpegLicensePath"
}
Copy-Item -LiteralPath $ffmpegLicensePath -Destination dist/Spectari/FFMPEG-LICENSE.txt -Force

$notice = @(
    "Spectari third-party notices"
    ""
    "Spectari bundles FFmpeg as ffmpeg.exe. FFmpeg is a separate project with"
    "its own authors and license, listed below."
    ""
    "ffmpeg version: $ffmpegVersionLine"
    "ffmpeg SHA256: $ffmpegHash"
    ""
    $ffmpegLicenseLine
)
if ($isGpl) {
    $notice += ""
    $notice += "The FFmpeg source corresponding to this build is available here:"
    $notice += $ffmpegSource.sourceUrl
    $notice += "Source archive SHA256: $($ffmpegSource.sourceSha256)"
    $notice += "FFmpeg source commit: $($ffmpegSource.sourceCommit)"
    $notice += "Build provider and configuration: $($ffmpegSource.providerUrl)"
    $notice += "The full FFmpeg license text is in FFMPEG-LICENSE.txt."
}
$notice += ""
$notice += "FFmpeg project: https://ffmpeg.org"
$notice += "FFmpeg source: https://ffmpeg.org/download.html"
$notice += "FFmpeg license details: https://ffmpeg.org/legal.html"
$notice += ""
$notice += "Spectari itself is licensed separately under the PolyForm Noncommercial License. See LICENSE.txt."
Set-Content -Path dist/Spectari/THIRD-PARTY-NOTICES.txt -Value $notice

# Publish drops debug symbols we don't need to ship
Remove-Item dist/Spectari/*.pdb -ErrorAction SilentlyContinue

$zip = "dist/Spectari-v$Version.zip"
Write-Host "Zipping -> $zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path dist/Spectari/* -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"
