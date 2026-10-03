param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Use a semantic version such as 1.0.0.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
foreach ($relative in @('Directory.Build.props')) {
    $path = Join-Path $root $relative
    [xml]$document = Get-Content -LiteralPath $path -Raw
    $node = $document.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node) { throw "Version element missing in $relative." }
    $node.InnerText = $Version
    $document.Save($path)
}
Write-Output "Build version set to $Version (temporary runner checkout only)."
