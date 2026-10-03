param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Commit,
    [Parameter(Mandatory=$true)][string]$Repository,
    [Parameter(Mandatory=$true)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid release version.' }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'Use the exact source commit SHA.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
if (-not $env:GH_TOKEN) { throw 'GitHub release authentication is missing.' }
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$names = @(
    "AravalsStream-Setup-$Version.exe",
    "AravalsRemoteCapture-Setup-$Version.exe",
    "AravalsStream-Development-$Version-linux-x64.tar.gz",
    "AravalsStream-Development-$Version-osx-x64.tar.gz",
    "AravalsStream-Development-$Version-osx-arm64.tar.gz"
)
$checksums = @()
foreach ($name in $names) {
    $path = Join-Path $directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -lt 1024) { throw "Required package missing or empty: $name" }
    $checksums += (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-Path $directory 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n")
$runUrl = "https://github.com/$Repository/actions/runs/$env:GITHUB_RUN_ID"
$manifest = @{version=$Version; sourceCommit=$Commit; workflowRun=$runUrl; windows='Windows workstation and Remote Capture Agent'; linux='Desktop development port'; macOS='Intel and Apple Silicon desktop development ports'}
[IO.File]::WriteAllText((Join-Path $directory 'BUILD-INFO.json'), ($manifest | ConvertTo-Json))
$names += @('SHA256SUMS.txt','BUILD-INFO.json')
$headers = @{Authorization=('Bearer ' + $env:GH_TOKEN); Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'}
$api = "https://api.github.com/repos/$Repository"
$tag = 'v' + $Version
$reference = $null
try { $reference = Invoke-RestMethod -Headers $headers -Uri "$api/git/ref/tags/$tag" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if ($reference) {
    $object = $reference.object
    for ($depth = 0; $object.type -eq 'tag' -and $depth -lt 5; $depth++) {
        $annotated = Invoke-RestMethod -Headers $headers -Uri "$api/git/tags/$($object.sha)"
        $object = $annotated.object
    }
    if ($object.type -ne 'commit' -or $object.sha -ne $Commit) { throw 'That tag points to a different source commit. Choose a new release version.' }
}
$notesPayload = @{tag_name=$tag; target_commitish=$Commit} | ConvertTo-Json
$generated = Invoke-RestMethod -Headers $headers -Method Post -Uri "$api/releases/generate-notes" -ContentType 'application/json' -Body $notesPayload
$body = @"
## Downloads from one build

| Platform | Download |
| --- | --- |
| Windows x64 | AravalsStream-Setup-$Version.exe |
| Windows Remote Capture Agent | AravalsRemoteCapture-Setup-$Version.exe |
| Linux x64 | AravalsStream-Development-$Version-linux-x64.tar.gz |
| Mac Intel | AravalsStream-Development-$Version-osx-x64.tar.gz |
| Mac Apple Silicon (M-series) | AravalsStream-Development-$Version-osx-arm64.tar.gz |

All packages use version **$Version** and source commit **$Commit**. SHA256SUMS.txt provides download checksums; BUILD-INFO.json records the source and build. [Build and test results]($runUrl).

### Platform status

Windows contains the existing workstation and a separate Remote Capture Agent installer. Linux/macOS contain the Avalonia desktop development port with actual preview, RTMP/RTMPS encoding, MKV recording, saved scenes/transforms, device discovery and protected destination credentials. Full workstation feature parity is still pending, so this combined release is a **prerelease**.

Linux/macOS require separately installed FFmpeg. Linux currently requires an X11 desktop for screen capture; PulseAudio utilities (pactl) provide audio discovery and Secret Service (secret-tool plus an unlocked keyring) protects saved keys. Extract the Linux archive and run ./AravalsStream.Desktop from its directory.

Mac archives contain Aravals Stream.app. Install FFmpeg, open the app matching your CPU and grant native camera/microphone/screen-recording permissions when needed. The app is unsigned and not notarized. macOS system audio and Linux Wayland capture are not implemented yet.

Live scene switching, full multistream/provider controls and remaining workstation features are still being ported. [Detailed port status](https://github.com/$Repository/blob/$Commit/docs/cross-platform-port.md).

## Automatically generated changes

$($generated.body)
"@
$release = $null
try { $release = Invoke-RestMethod -Headers $headers -Uri "$api/releases/tags/$tag" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if (-not $release) {
    $recent = Invoke-RestMethod -Headers $headers -Uri "$api/releases?per_page=100"
    $release = $recent | Where-Object {$_.tag_name -eq $tag} | Select-Object -First 1
}
if ($release) {
    if (-not $release.draft) { throw 'That version is already published. Choose a new version; published downloads will not be overwritten.' }
    if ($release.target_commitish -ne $Commit) { throw 'Existing draft belongs to a different source commit. Choose a new version.' }
} else {
    $payload = @{tag_name=$tag; target_commitish=$Commit; name="Aravals Stream $Version — Windows, Linux, macOS"; body=$body; draft=$true; prerelease=$true; make_latest='false'} | ConvertTo-Json
    $release = Invoke-RestMethod -Headers $headers -Method Post -Uri "$api/releases" -ContentType 'application/json' -Body $payload
}
# Keep the release private as a draft until every required download has uploaded and passed verification.
foreach ($name in $names) {
    $path = Join-Path $directory $name
    $size = (Get-Item -LiteralPath $path).Length
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = $release.assets | Where-Object {$_.name -eq $name} | Select-Object -First 1
    if ($asset -and $asset.size -eq $size -and $asset.digest -eq ('sha256:' + $hash)) { continue }
    if ($asset) { Invoke-RestMethod -Headers $headers -Method Delete -Uri "$api/releases/assets/$($asset.id)" | Out-Null }
    $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
    $asset = Invoke-RestMethod -Headers $headers -Method Post -Uri $upload -ContentType 'application/octet-stream' -InFile $path
    if ($asset.state -ne 'uploaded' -or $asset.size -ne $size -or ($asset.digest -and $asset.digest -ne ('sha256:' + $hash))) { throw "Upload verification failed: $name" }
    Write-Output "Verified release asset: $name"
}
$assets = Invoke-RestMethod -Headers $headers -Uri "$api/releases/$($release.id)/assets?per_page=100"
foreach ($name in $names) { if (-not ($assets | Where-Object {$_.name -eq $name -and $_.state -eq 'uploaded'})) { throw "Release asset missing: $name" } }
$publish = @{draft=$false; prerelease=$true; body=$body; make_latest='false'} | ConvertTo-Json
$published = Invoke-RestMethod -Headers $headers -Method Patch -Uri "$api/releases/$($release.id)" -ContentType 'application/json' -Body $publish
Write-Output "Published all-platform prerelease: $($published.html_url)"
