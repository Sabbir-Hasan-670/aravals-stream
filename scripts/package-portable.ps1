param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$name = 'AravalsStream-Development-' + $Runtime
$stage = Join-Path $output $name
if (Test-Path -LiteralPath $stage) { throw 'Package staging directory already exists; choose a fresh output directory.' }
New-Item -ItemType Directory -Path $stage | Out-Null
if ($Runtime.StartsWith('osx-')) {
    $contents = Join-Path $stage 'Aravals Stream.app/Contents'
    $binary = Join-Path $contents 'MacOS'
    New-Item -ItemType Directory -Path $binary -Force | Out-Null
    Get-ChildItem -LiteralPath $publish -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $binary -Recurse }
    @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>AravalsStream.Desktop</string>
<key>CFBundleIdentifier</key><string>com.aravals.stream.development</string>
<key>CFBundleName</key><string>Aravals Stream Development</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.20.1</string>
<key>CFBundleVersion</key><string>0.20.1.1</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSCameraUsageDescription</key><string>Capture the camera you choose for your stream.</string>
<key>NSMicrophoneUsageDescription</key><string>Capture the microphone you choose for your stream.</string>
</dict></plist>
'@ | Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -Encoding utf8
    if (-not $IsMacOS) { throw 'macOS development bundles must be packaged and validated on a macOS runner.' }
    & chmod +x (Join-Path $binary 'AravalsStream.Desktop')
    & plutil -lint (Join-Path $contents 'Info.plist')
    if ($LASTEXITCODE -ne 0) { throw 'Invalid macOS bundle metadata.' }
} else {
    Get-ChildItem -LiteralPath $publish -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse }
    if ($Runtime.StartsWith('linux-')) { & chmod +x (Join-Path $stage 'AravalsStream.Desktop') }
    if ($Runtime -eq 'win-x64') {
        $media = Join-Path $PSScriptRoot '../ffmpeg'
        if (Test-Path -LiteralPath (Join-Path $media 'bin/ffmpeg.exe')) { Copy-Item -LiteralPath $media -Destination (Join-Path $stage 'ffmpeg') -Recurse }
    }
}
@'
ARAVALS STREAM — DEVELOPMENT PORT

This package is not yet the completed Linux/macOS streaming workstation.
Full feature parity and native screen/audio acceptance are pending.
See https://github.com/Sabbir-Hasan-670/aravals-stream/blob/codex/portable-desktop/docs/cross-platform-port.md

Windows: run AravalsStream.Desktop.exe. Bundled FFmpeg is included when prepared by CI.
Linux: install FFmpeg using your distribution package manager, then run ./AravalsStream.Desktop in an X11 desktop session. Wayland portal capture is still pending.
macOS: install FFmpeg (for example through Homebrew), open Aravals Stream.app, and grant the requested native capture permissions. This development bundle is unsigned and not notarized. Native device indices must be enumerated; camera 0 must not be guessed as a display.

Do not publish this package on the download website as a completed full-platform release.
'@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding utf8
if ($Runtime -eq 'win-x64') { Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $output ($name + '.zip')) }
else {
    & tar -czf (Join-Path $output ($name + '.tar.gz')) -C $output $name
    if ($LASTEXITCODE -ne 0) { throw 'Portable archive creation failed.' }
}
Write-Output "Development package created: $name"
