[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LogoPath,

    [string]$SourceIcon = (Join-Path $PSScriptRoot '..\Thunderstore\icon.png'),

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Thunderstore\icon.png'),

    [ValidateRange(32, 96)]
    [int]$LogoSize = 58,

    [ValidateRange(0, 24)]
    [int]$Padding = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$logoPathResolved = (Resolve-Path -LiteralPath $LogoPath).Path
$sourceFullPath = [System.IO.Path]::GetFullPath($SourceIcon)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
$tempPath = [System.String]::Concat($outputFullPath, '.tmp.png')
$backupPath = [System.String]::Concat($outputFullPath, '.bak')
$sameSourceAndOutput = [string]::Equals(
    $sourceFullPath,
    $outputFullPath,
    [System.StringComparison]::OrdinalIgnoreCase
)

if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    if ($sameSourceAndOutput -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        Copy-Item -LiteralPath $backupPath -Destination $sourceFullPath -Force -Confirm:$false
        Write-Host "Restored missing source icon from: $backupPath"
    }
    else {
        throw "Source icon does not exist: $sourceFullPath"
    }
}

$sourceImage = $null
$logoImage = $null
$canvas = $null
$graphics = $null
$renderWidth = 0
$renderHeight = 0
$x = 0
$y = 0

try {
    $sourceImage = [System.Drawing.Image]::FromFile($sourceFullPath)
    $logoImage = [System.Drawing.Image]::FromFile($logoPathResolved)

    if ($sourceImage.Width -ne 256 -or $sourceImage.Height -ne 256) {
        throw "Thunderstore icon must be exactly 256x256 pixels. Current size: $($sourceImage.Width)x$($sourceImage.Height)."
    }

    if ($logoImage.Width -le 0 -or $logoImage.Height -le 0) {
        throw 'The AWL logo has invalid dimensions.'
    }

  $sourceBitmap = [System.Drawing.Bitmap]$sourceImage
  $sourceRectangle = New-Object System.Drawing.Rectangle 0, 0, 256, 256
  $canvas = $sourceBitmap.Clone($sourceRectangle, $sourceBitmap.PixelFormat)

  $graphics = [System.Drawing.Graphics]::FromImage($canvas)
  $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
  $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    $scale = [Math]::Min(
        [double]$LogoSize / [double]$logoImage.Width,
        [double]$LogoSize / [double]$logoImage.Height
    )
    $renderWidth = [Math]::Max(1, [int][Math]::Round($logoImage.Width * $scale))
    $renderHeight = [Math]::Max(1, [int][Math]::Round($logoImage.Height * $scale))
    $x = 256 - $Padding - $renderWidth
    $y = 256 - $Padding - $renderHeight

    if ($x -lt 0 -or $y -lt 0) {
        throw 'Logo size and padding exceed the 256x256 icon bounds.'
    }

    $graphics.DrawImage($logoImage, $x, $y, $renderWidth, $renderHeight)

    if (-not $PSCmdlet.ShouldProcess($outputFullPath, 'Write branded Thunderstore icon')) {
        return
    }

    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -Confirm:$false
    }

    $canvas.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    if ($graphics) { $graphics.Dispose() }
    if ($canvas) { $canvas.Dispose() }
    if ($logoImage) { $logoImage.Dispose() }
    if ($sourceImage) { $sourceImage.Dispose() }
}

try {
    if ($sameSourceAndOutput -and (Test-Path -LiteralPath $outputFullPath -PathType Leaf)) {
        Copy-Item -LiteralPath $outputFullPath -Destination $backupPath -Force -Confirm:$false
        Write-Host "Backup: $backupPath"
    }

    [System.IO.File]::Copy($tempPath, $outputFullPath, $true)
    Write-Host "Created: $outputFullPath"
    Write-Host "Logo placement: ${renderWidth}x${renderHeight} at ($x,$y)"
}
finally {
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
}
