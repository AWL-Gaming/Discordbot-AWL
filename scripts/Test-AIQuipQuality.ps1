[CmdletBinding()]
param(
    [string]$AssemblyPath = '',
    [string]$BepInExCorePath = '',
    [string]$ValheimManagedPath = '',
    [string[]]$ReferencePath = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $repoRoot 'bin\Release\DiscordBot.dll'
}

if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    throw "Assembly not found: $AssemblyPath"
}

if ([string]::IsNullOrWhiteSpace($BepInExCorePath)) {
    $BepInExCorePath = [Environment]::GetEnvironmentVariable('BEPINEX_CORE_PATH')
}
if ([string]::IsNullOrWhiteSpace($ValheimManagedPath)) {
    $ValheimManagedPath = [Environment]::GetEnvironmentVariable('VALHEIM_MANAGED_PATH')
}
if ([string]::IsNullOrWhiteSpace($BepInExCorePath) -or -not (Test-Path -LiteralPath $BepInExCorePath -PathType Container)) {
    throw 'BepInEx core path is required. Pass -BepInExCorePath or set BEPINEX_CORE_PATH.'
}
if ([string]::IsNullOrWhiteSpace($ValheimManagedPath) -or -not (Test-Path -LiteralPath $ValheimManagedPath -PathType Container)) {
    throw 'Valheim managed path is required. Pass -ValheimManagedPath or set VALHEIM_MANAGED_PATH.'
}

$resolveDirs = @(
    (Split-Path -Parent (Resolve-Path -LiteralPath $AssemblyPath).Path),
    $BepInExCorePath,
    $ValheimManagedPath
) + $ReferencePath

$resolveHandler = [ResolveEventHandler]{
    param($_sender, $resolveArgs)

    $fileName = ([Reflection.AssemblyName]$resolveArgs.Name).Name + '.dll'
    foreach ($directory in $resolveDirs) {
        if ([string]::IsNullOrWhiteSpace($directory)) { continue }
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    return $null
}

function Invoke-Validation {
    param(
        [Parameter(Mandatory)]$QualityType,
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$Candidate
    )

    $method = $QualityType.GetMethod(
        'TryValidateAndFinalize',
        [Reflection.BindingFlags]'Public,Static')
    if ($null -eq $method) { throw 'TryValidateAndFinalize was not found.' }

    [object[]]$arguments = @($Candidate, $Context, $null, 0, $null)
    $accepted = [bool]$method.Invoke($null, $arguments)

    [pscustomobject]@{
        Accepted = $accepted
        Final = [string]$arguments[2]
        Score = [int]$arguments[3]
        Error = [string]$arguments[4]
    }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Name)
    if ($Actual -ne $Expected) {
        throw "$Name failed. Expected '$Expected', got '$Actual'."
    }
}

function Assert-True {
    param([bool]$Value, [string]$Name)
    if (-not $Value) { throw "$Name failed." }
}

[AppDomain]::CurrentDomain.add_AssemblyResolve($resolveHandler)
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
    $contextType = $assembly.GetType('DiscordBot.AIRequestContext', $true)
    $qualityType = $assembly.GetType('DiscordBot.AIQuipQuality', $true)

    $deathFactory = $contextType.GetMethod('Death', [Reflection.BindingFlags]'Public,Static')
    $dayFactory = $contextType.GetMethod('Day', [Reflection.BindingFlags]'Public,Static')
    if ($null -eq $deathFactory -or $null -eq $dayFactory) {
        throw 'AI request context factories were not found.'
    }

    $deathContext = $deathFactory.Invoke($null, @('Med', 'Med challenged gravity and lost.'))
    $dayContext = $dayFactory.Invoke($null, @(42, 'Day 42 begins beneath Odin''s watch.'))

    $tests = @(
        @{ Name = 'Valid death quip'; Context = $deathContext; Candidate = '{PLAYER} tripped over fate; Odin stamped the paperwork for Valhalla.'; Expected = $true },
        @{ Name = 'Lowercase player token'; Context = $deathContext; Candidate = '{player} tripped over fate; Odin stamped the paperwork for Valhalla.'; Expected = $true },
        @{ Name = 'Truncated Valk fragment'; Context = $deathContext; Candidate = ', Odin watching, Valk'; Expected = $false },
        @{ Name = 'Missing player token'; Context = $deathContext; Candidate = 'Odin watched another warrior stumble into Valhalla without reading the warning signs.'; Expected = $false },
        @{ Name = 'Repeated player token'; Context = $deathContext; Candidate = '{PLAYER} met Odin, and {PLAYER} immediately requested a less embarrassing saga.'; Expected = $false },
        @{ Name = 'Markdown wrapper'; Context = $deathContext; Candidate = '**{PLAYER} reached Valhalla while Odin pretended not to notice the landing.**'; Expected = $false },
        @{ Name = 'Model planning preamble'; Context = $deathContext; Candidate = 'Selecting the Best Option: {PLAYER} reached Valhalla while Odin reviewed the alternatives.'; Expected = $false },
        @{ Name = 'Missing Norse flavor'; Context = $deathContext; Candidate = '{PLAYER} fell down and learned a very ordinary lesson about gravity today.'; Expected = $false },
        @{ Name = 'Valid day quip'; Context = $dayContext; Candidate = 'Day {DAY} dawns; Odin demands fresh axes and fewer excuses from every warrior.'; Expected = $true },
        @{ Name = 'Bare day token rejected'; Context = $dayContext; Candidate = '{DAY} rises beneath Odin''s watch; every Viking prepares for another brutal morning.'; Expected = $false }
    )

    foreach ($test in $tests) {
        $result = Invoke-Validation -QualityType $qualityType -Context $test.Context -Candidate $test.Candidate
        Assert-Equal -Actual $result.Accepted -Expected $test.Expected -Name $test.Name

        if ($result.Accepted) {
            Assert-True -Value ($result.Score -ge 70) -Name ($test.Name + ' score')
            if ($test.Name -in @('Valid death quip', 'Lowercase player token')) {
                Assert-True -Value ($result.Final -match '\bMed\b') -Name ($test.Name + ' exact player replacement')
                Assert-True -Value ($result.Final -notmatch '\{PLAYER\}') -Name ($test.Name + ' player token removal')
            }
            if ($test.Name -eq 'Valid day quip') {
                Assert-True -Value ($result.Final -match '\bDay 42\b') -Name 'Exact day replacement'
                Assert-True -Value ($result.Final -notmatch '\{DAY\}') -Name 'Day token removal'
            }
        }

        Write-Output ("PASS: {0} (accepted={1}, score={2}, error={3})" -f $test.Name, $result.Accepted, $result.Score, $result.Error)
    }

    Write-Output "Validated AI quip quality gate: $AssemblyPath"
}
finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolveHandler)
}
