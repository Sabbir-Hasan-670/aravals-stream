$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version is missing from Directory.Build.props.' }
$compiler = & (Join-Path $PSScriptRoot 'get-compiler.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $root 'ffmpeg/bin/ffmpeg.exe'))) { throw 'Run scripts/get-windows-ffmpeg.ps1 before building the Agent installer.' }
$publish = Join-Path $root 'publish-agent'
& dotnet publish (Join-Path $root 'src\AravalsStream.RemoteAgent\AravalsStream.RemoteAgent.csproj') -c Release -warnaserror -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
& $compiler "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'AravalsRemoteCapture.iss')
if ($LASTEXITCODE -ne 0) { throw 'Agent installer build failed.' }
