param([Parameter(Mandatory=$true)][string]$ZipPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('Dockerfile','Dockerfile.published','README.md','.env.example','nginx-relay.conf.example','.dockerignore','validate-package.ps1',
        'linux-x64/AravalsStream.Relay','linux-x64/AravalsStream.Relay.dll','linux-x64/AravalsStream.Relay.runtimeconfig.json','linux-x64/AravalsStream.Relay.deps.json')) {
        if ($names -notcontains $required) { throw "Required deployment file is missing: $required" }
    }
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains('../') -or $name.Contains(':') -or
            $name -match '(?i)(^|/)(secrets|settings\.json|relay-state[^/]*\.json|session\.active)(/|$)|\.(pfx|p12|key|pem)$' -or
            ($name -match '(?i)\.env$' -and $name -ne '.env.example')) { throw "Forbidden deployment entry: $name" }
        if ($name -notlike 'linux-x64/*' -and $entry.Length -gt 0) {
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $value = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($value -match '(?i)[A-Z]:[\\/]Users[\\/]|/home/[A-Za-z0-9_.-]+/|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY') { throw "Private or user-specific content in: $name" }
        }
    }
    Write-Output "Deployment ZIP validation: PASS ($($archive.Entries.Count) entries)"
} finally { $archive.Dispose() }
