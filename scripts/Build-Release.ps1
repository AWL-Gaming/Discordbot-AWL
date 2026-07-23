[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$GamePath = '',

    [string]$BepInExPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'DiscordBot.csproj'
$nugetConfig = Join-Path $repoRoot 'NuGet.Config'
$publicizedPath = Join-Path $repoRoot 'build\publicized_assemblies'
$copyOutputPath = Join-Path $repoRoot 'build\plugin'

function Resolve-ExistingPath {
    param([string[]]$Candidates, [string]$RequiredChild)

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path -LiteralPath (Join-Path $expanded $RequiredChild)) {
            return (Resolve-Path -LiteralPath $expanded).Path
        }
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = Resolve-ExistingPath -RequiredChild 'valheim_Data\Managed\assembly_valheim.dll' -Candidates @(
        'D:\SteamLibrary\steamapps\common\Valheim',
        'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
        'C:\Program Files\Steam\steamapps\common\Valheim'
    )
}

if ([string]::IsNullOrWhiteSpace($GamePath) -or -not (Test-Path -LiteralPath (Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll'))) {
    throw 'Valheim was not found. Pass -GamePath with the Valheim installation directory.'
}

if ([string]::IsNullOrWhiteSpace($BepInExPath)) {
    $bepCandidates = [System.Collections.Generic.List[string]]::new()
    $bepCandidates.Add((Join-Path $GamePath 'BepInEx'))

    foreach ($profileRoot in @(
        (Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles'),
        (Join-Path $env:APPDATA 'com.kesomannen.gale\valheim\profiles')
    )) {
        if (-not (Test-Path -LiteralPath $profileRoot)) { continue }
        Get-ChildItem -LiteralPath $profileRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { $bepCandidates.Add((Join-Path $_.FullName 'BepInEx')) }
    }

    $BepInExPath = Resolve-ExistingPath -Candidates $bepCandidates.ToArray() -RequiredChild 'core\BepInEx.dll'
}

if ([string]::IsNullOrWhiteSpace($BepInExPath) -or -not (Test-Path -LiteralPath (Join-Path $BepInExPath 'core\BepInEx.dll'))) {
    throw 'BepInEx was not found. Pass -BepInExPath with the BepInEx directory.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'vswhere.exe was not found. Install Visual Studio Build Tools with MSBuild.'
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or -not (Test-Path -LiteralPath $msbuild)) {
    throw 'MSBuild was not found. Install the Visual Studio Build Tools MSBuild component.'
}

Push-Location $repoRoot
try {
    & dotnet tool restore --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    New-Item -ItemType Directory -Path $publicizedPath, $copyOutputPath -Force | Out-Null

    $managedPath = Join-Path $GamePath 'valheim_Data\Managed'
    $assemblies = @(
        @{ Source = 'assembly_valheim.dll'; Output = 'assembly_valheim_publicized.dll' },
        @{ Source = 'assembly_guiutils.dll'; Output = 'assembly_guiutils_publicized.dll' },
        @{ Source = 'assembly_utils.dll'; Output = 'assembly_utils_publicized.dll' },
        @{ Source = 'Splatform.dll'; Output = 'Splatform_publicized.dll' }
    )

    foreach ($assembly in $assemblies) {
        $source = Join-Path $managedPath $assembly.Source
        $temporaryOutput = Join-Path $publicizedPath $assembly.Source
        $finalOutput = Join-Path $publicizedPath $assembly.Output

        if (-not (Test-Path -LiteralPath $source)) {
            throw "Required Valheim assembly is missing: $source"
        }

        $mustGenerate = -not (Test-Path -LiteralPath $finalOutput) -or
            (Get-Item -LiteralPath $source).LastWriteTimeUtc -gt (Get-Item -LiteralPath $finalOutput).LastWriteTimeUtc

        if ($mustGenerate) {
            & dotnet tool run assembly-publicizer -- $source --output $publicizedPath --target All --strip --overwrite
            if ($LASTEXITCODE -ne 0) { throw "Failed to publicize $source" }
            Copy-Item -LiteralPath $temporaryOutput -Destination $finalOutput -Force
        }
    }

    $commonProperties = @(
        "/p:Configuration=$Configuration",
        "/p:GamePath=$GamePath",
        "/p:ValheimGamePath=$GamePath",
        "/p:BepInExPath=$BepInExPath",
        "/p:CorlibPath=$managedPath",
        "/p:PublicizedAssembliesPath=$publicizedPath",
        "/p:CopyOutputDLLPath=$copyOutputPath",
        "/p:CopyOutputDLLPath2=$copyOutputPath",
        "/p:CopyOutputDLLPath3=$copyOutputPath"
    )

    & $msbuild $projectPath /t:Restore "/p:RestoreConfigFile=$nugetConfig" @commonProperties /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    & $msbuild $projectPath /t:Build @commonProperties /p:AfterTargets=ILRepacker /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    & (Join-Path $PSScriptRoot 'Test-ThunderstorePackage.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Package validation failed.' }
}
finally {
    Pop-Location
}
