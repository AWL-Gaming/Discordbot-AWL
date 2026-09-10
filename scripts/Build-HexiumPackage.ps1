[CmdletBinding()]
param(
    [string]$SourcePackage = '',
    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'Thunderstore\manifest.json') -Raw | ConvertFrom-Json
$version = $manifest.version_number

if ([string]::IsNullOrWhiteSpace($SourcePackage)) {
    $SourcePackage = Join-Path $repoRoot "Thunderstore\DiscordBot_v$version.zip"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "build\hexium\DiscordBot_v$version.zip"
}

$faqRoot = Join-Path $repoRoot 'Hexium\faq'
if (-not (Test-Path -LiteralPath $SourcePackage)) { throw "Source package not found: $SourcePackage" }
if (-not (Test-Path -LiteralPath $faqRoot)) { throw "Hexium FAQ source not found: $faqRoot" }

$faqFiles = @(Get-ChildItem -LiteralPath $faqRoot -Recurse -File -Filter '*.md')
if ($faqFiles.Count -eq 0) { throw 'Hexium FAQ is empty.' }
if ($faqFiles.Count -gt 100) { throw 'Hexium supports at most 100 FAQ entries.' }

foreach ($file in $faqFiles) {
    $relative = $file.FullName.Substring($faqRoot.Length).TrimStart('\')
    if (($relative -split '\\').Count -gt 2) { throw "FAQ nesting is deeper than one section: $relative" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path ([IO.Path]::GetTempPath()) ('DiscordBotHexium-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($SourcePackage, $temp)
    $targetFaq = Join-Path $temp 'faq'
    New-Item -ItemType Directory -Path $targetFaq -Force | Out-Null
    Get-ChildItem -LiteralPath $faqRoot | Copy-Item -Destination $targetFaq -Recurse -Force

    $outputDir = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    Remove-Item -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue

    $archive = [IO.Compression.ZipFile]::Open($OutputPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $temp -Recurse -File) {
            $entryName = $file.FullName.Substring($temp.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

$archive = [IO.Compression.ZipFile]::OpenRead($OutputPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $faqEntries = @($entryNames | Where-Object { $_ -like 'faq/*' -and $_ -like '*.md' })
    if ($faqEntries.Count -ne $faqFiles.Count) {
        throw "Hexium package contains $($faqEntries.Count) FAQ entries; expected $($faqFiles.Count)."
    }
    if ($entryNames | Where-Object { $_ -match '\\' }) {
        throw 'Hexium package contains a ZIP entry with Windows path separators.'
    }
    foreach ($name in @('manifest.json', 'README.md', 'icon.png', 'CHANGELOG.md', 'NOTICE.md', 'DiscordBot.dll')) {
        if ($entryNames -notcontains $name) { throw "Hexium package is missing required root file: $name" }
    }
}
finally {
    $archive.Dispose()
}

Write-Output "Built Hexium package with $($faqFiles.Count) FAQ entries: $OutputPath"