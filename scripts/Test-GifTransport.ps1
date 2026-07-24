[CmdletBinding()]
param(
    [string]$AssemblyPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$discordPath = Join-Path $repoRoot 'src\Behaviors\Discord.cs'
$recorderPath = Join-Path $repoRoot 'src\Behaviors\Recorder.cs'
$manifestPath = Join-Path $repoRoot 'Thunderstore\manifest.json'

if (-not (Test-Path -LiteralPath $discordPath)) {
    throw "Source not found: $discordPath"
}
if (-not (Test-Path -LiteralPath $recorderPath)) {
    throw "Source not found: $recorderPath"
}

$discord = Get-Content -LiteralPath $discordPath -Raw
$recorder = Get-Content -LiteralPath $recorderPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not $Content.Contains($Needle)) {
        throw "$Name failed. Missing marker: $Needle"
    }

    Write-Output "PASS: $Name"
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Content.Contains($Needle)) {
        throw "$Name failed. Forbidden marker remains: $Needle"
    }

    Write-Output "PASS: $Name"
}

$requiredDiscord = [ordered]@{
    'Protocol v3' = 'private const int WebhookBrokerProtocolVersion = 3;'
    '16 KiB chunks' = 'private const int RemoteAttachmentChunkBytes = 16 * 1024;'
    'Attachment queue' = 'Queue<PendingRemoteAttachment>'
    'Start acknowledgement' = 'AttachmentAckStage.Start'
    'Chunk acknowledgement' = 'AttachmentAckStage.Chunk'
    'Completion acknowledgement' = 'AttachmentAckStage.Complete'
    'Abort acknowledgement' = 'AttachmentAckStage.Abort'
    'ACK receiver' = 'RPC_WebhookAttachmentAck'
    'Abort receiver' = 'RPC_WebhookAttachmentAbort'
    'Bounded retries' = 'private const int RemoteAttachmentMaxRetries = 3;'
    'Two-chunk ACK window' = 'private const int RemoteAttachmentChunkWindowSize = 2;'
    'Keyed ACK table' = 'Dictionary<string, AttachmentAckWaitState>'
    'Stable ACK codes' = 'AttachmentAckCode'
    'Aggregate transfer memory cap' = 'private const int MaxConcurrentRemoteTransferBytes = 32 * 1024 * 1024;'
    'Fresh server RPC lookup' = 'GetConnectedServerRpc'
    'Duplicate-safe completion cache' = 'RemoteWebhookCompletedTransfers'
    'PNG transport fallback' = 'fallbackMimeType: "image/png"'
    'Text-only last resort logging' = 'Sending text-only webhook fallback because'
}

foreach ($entry in $requiredDiscord.GetEnumerator()) {
    Assert-Contains -Content $discord -Needle $entry.Value -Name $entry.Key
}

$requiredRecorder = [ordered]@{
    'Encoded size logging' = 'Encoded death GIF using'
    'Adaptive profile level 1' = 'adaptive GIF level 1'
    'Adaptive profile level 2' = 'adaptive GIF level 2'
    'Adaptive profile level 3' = 'adaptive GIF level 3'
    'Representative PNG fallback' = 'CreateFallbackPng'
    'GIF carries PNG fallback' = 'fallbackPng: fallbackPng'
    'PNG fallback reason logging' = 'Sending death PNG fallback because'
    'Text-only final fallback' = 'Sending text-only death notice because'
}

foreach ($entry in $requiredRecorder.GetEnumerator()) {
    Assert-Contains -Content $recorder -Needle $entry.Value -Name $entry.Key
}

Assert-NotContains -Content $discord -Needle 'SendWebhookAttachmentToServer' -Name 'Legacy fire-and-forget sender removed'
Assert-NotContains -Content $discord -Needle 'WebhookBrokerProtocolVersion = 2' -Name 'Legacy protocol removed'
Assert-NotContains -Content $discord -Needle 'ChunksPerFrame' -Name 'Frame-burst transport removed'
Assert-NotContains -Content $discord -Needle 'package.Write(attachment)' -Name 'Full attachment is never written into one RPC package'

$chunkWriteCount = ([regex]::Matches($discord, 'package\.Write\(entry\.Data\);')).Count
if ($chunkWriteCount -ne 1) {
    throw "Chunk writer count failed. Expected 1, got $chunkWriteCount."
}
Write-Output 'PASS: One bounded chunk writer'

if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $repoRoot 'bin\Release\DiscordBot.dll'
}

if (Test-Path -LiteralPath $AssemblyPath) {
    $version = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $AssemblyPath).Path).Version
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Manifest not found: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $expectedVersion = [Version]("$($manifest.version_number).0")
    if ($version -ne $expectedVersion) {
        throw "Assembly version failed. Expected $expectedVersion, got $version."
    }
    Write-Output "PASS: Assembly version $version"
}
else {
    Write-Warning "Assembly not found; source invariants passed without binary version validation: $AssemblyPath"
}

Write-Output 'Validated adaptive GIF capture and acknowledged attachment transport.'
