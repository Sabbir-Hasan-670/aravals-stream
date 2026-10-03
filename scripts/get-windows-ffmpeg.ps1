param([string]$Destination = (Join-Path $PSScriptRoot '../ffmpeg'))
$ErrorActionPreference = 'Stop'
# Gyan Windows builds are linked by https://ffmpeg.org/download.html.
$url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$work = Join-Path ([IO.Path]::GetTempPath()) ('aravals-ffmpeg-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$archive = Join-Path $work 'ffmpeg.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
$checksum = (Invoke-WebRequest -Uri ($url + '.sha256')).Content
if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::UTF8.GetString($checksum) }
$expected = [regex]::Match([string]$checksum, '[A-Fa-f0-9]{64}').Value
if (-not $expected -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'FFmpeg archive checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'extracted')
$packages = @(Get-ChildItem -LiteralPath (Join-Path $work 'extracted') -Directory)
if ($packages.Count -ne 1) { throw 'Unexpected FFmpeg archive layout.' }
$package = $packages[0]
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Get-ChildItem -LiteralPath $package.FullName -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force }
Write-Output 'FFmpeg download and SHA256 verification: PASS'
