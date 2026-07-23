[CmdletBinding()]
param([string]$PackagePath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'Thunderstore\manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $repoRoot "Thunderstore\DiscordBot_v$($manifest.version_number).zip"
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package not found: $PackagePath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing

$required = @('manifest.json', 'README.md', 'icon.png', 'CHANGELOG.md', 'NOTICE.md', 'DiscordBot.dll')
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
$temp = Join-Path ([IO.Path]::GetTempPath()) ('DiscordBotPackage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($name in $required) {
        if ($names -notcontains $name) { throw "Required root file is missing: $name" }
    }

    if ($names | Where-Object { $_ -match '^[^/]+/' }) {
        throw 'The package contains a top-level folder. Thunderstore files must be at the zip root.'
    }

    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $temp)

    $packageManifest = Get-Content -LiteralPath (Join-Path $temp 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($field in @('name', 'version_number', 'website_url', 'description', 'dependencies')) {
        if ($null -eq $packageManifest.$field) { throw "Manifest field is missing: $field" }
    }

    if ($packageManifest.version_number -ne $manifest.version_number) {
        throw 'Packaged manifest version does not match Thunderstore/manifest.json.'
    }

    $icon = [Drawing.Image]::FromFile((Join-Path $temp 'icon.png'))
    try {
        if ($icon.Width -ne 256 -or $icon.Height -ne 256) {
            throw "icon.png must be 256x256, found $($icon.Width)x$($icon.Height)."
        }
    }
    finally {
        $icon.Dispose()
    }

    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $temp 'DiscordBot.dll')).Version
    $expectedVersion = [Version]$manifest.version_number
    if ($assemblyVersion.Major -ne $expectedVersion.Major -or
        $assemblyVersion.Minor -ne $expectedVersion.Minor -or
        $assemblyVersion.Build -ne $expectedVersion.Build) {
        throw "DLL version $assemblyVersion does not match manifest version $expectedVersion."
    }

    Write-Output "Validated package: $PackagePath"
}
finally {
    $archive.Dispose()
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
