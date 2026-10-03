$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version is missing from Directory.Build.props.' }
$compiler = 'C:\Users\sabbi\AppData\Local\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe'
if (-not (Test-Path $compiler)) { $compiler = (Get-Command ISCC.exe -ErrorAction Stop).Source }
& dotnet publish (Join-Path $root 'src\AravalsStream.App\AravalsStream.App.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$obsoleteLauncher = Join-Path $root 'publish\AravalsStream.exe'
if (Test-Path -LiteralPath $obsoleteLauncher) { Remove-Item -LiteralPath $obsoleteLauncher }
& $compiler "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'AravalsStream.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
