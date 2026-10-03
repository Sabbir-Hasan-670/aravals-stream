$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version is missing from Directory.Build.props.' }
$compiler = 'C:\Users\sabbi\AppData\Local\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe'
if (-not (Test-Path $compiler)) { $compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source }
$publish = Join-Path $root 'publish-agent'
& dotnet publish (Join-Path $root 'src\AravalsStream.RemoteAgent\AravalsStream.RemoteAgent.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
& $compiler "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'AravalsRemoteCapture.iss')
if ($LASTEXITCODE -ne 0) { throw 'Agent installer build failed.' }
