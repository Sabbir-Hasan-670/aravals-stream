$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version is missing from Directory.Build.props.' }
$compiler = & (Join-Path $PSScriptRoot 'get-compiler.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $root 'ffmpeg/bin/ffmpeg.exe'))) { throw 'Run scripts/get-windows-ffmpeg.ps1 before building the installer.' }
& dotnet publish (Join-Path $root 'src\AravalsStream.App\AravalsStream.App.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$mediaDestination = Join-Path $root 'publish/ffmpeg'
New-Item -ItemType Directory -Path $mediaDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'ffmpeg') -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $mediaDestination -Recurse -Force }
$obsoleteLauncher = Join-Path $root 'publish\AravalsStream.exe'
if (Test-Path -LiteralPath $obsoleteLauncher) { Remove-Item -LiteralPath $obsoleteLauncher }
& $compiler "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'AravalsStream.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
