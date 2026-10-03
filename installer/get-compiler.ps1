$ErrorActionPreference = 'Stop'
$found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($found) { return $found.Source }
foreach ($candidate in @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs/Antigravity IDE/resources/app/node_modules/innosetup/bin/ISCC.exe')
)) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isinfo.php or add ISCC.exe to PATH.'
