[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LogoPath,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
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

$sourcePath = (Resolve-Path -LiteralPath $SourceIcon).Path
$logoPathResolved = (Resolve-Path -LiteralPath $LogoPath).Path
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath

if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$sourceImage = $null
$logoImage = $null
$canvas = $null
$graphics = $null
$tempPath = "$outputFullPath.tmp.png"

try {
    $sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
    $logoImage = [System.Drawing.Image]::FromFile($logoPathResolved)

    if ($sourceImage.Width -ne 256 -or $sourceImage.Height -ne 256) {
        throw "Thunderstore icon must be exactly 256x256 pixels. Current size: $($sourceImage.Width)x$($sourceImage.Height)."
    }

    $canvas = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $canvas.SetResolution(96, 96)

    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage($sourceImage, 0, 0, 256, 256)

    $scale = [Math]::Min($LogoSize / $logoImage.Width, $LogoSize / $logoImage.Height)
    $renderWidth = [Math]::Max(1, [int][Math]::Round($logoImage.Width * $scale))
    $renderHeight = [Math]::Max(1, [int][Math]::Round($logoImage.Height * $scale))
    $x = 256 - $Padding - $renderWidth
    $y = 256 - $Padding - $renderHeight

    if ($x -lt 0 -or $y -lt 0) {
        throw 'Logo size and padding exceed the 256x256 icon bounds.'
    }

    $graphics.DrawImage($logoImage, $x, $y, $renderWidth, $renderHeight)

    if ($PSCmdlet.ShouldProcess($outputFullPath, 'Write branded Thunderstore icon')) {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }

        $canvas.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Png)

        if ($sourcePath -eq $outputFullPath -and (Test-Path -LiteralPath $outputFullPath)) {
            $backupPath = "$outputFullPath.bak"
            Copy-Item -LiteralPath $outputFullPath -Destination $backupPath -Force
            Write-Host "Backup: $backupPath"
        }

        Move-Item -LiteralPath $tempPath -Destination $outputFullPath -Force
        Write-Host "Created: $outputFullPath"
        Write-Host "Logo placement: ${renderWidth}x${renderHeight} at ($x,$y)"
    }
}
finally {
    if ($graphics) { $graphics.Dispose() }
    if ($canvas) { $canvas.Dispose() }
    if ($logoImage) { $logoImage.Dispose() }
    if ($sourceImage) { $sourceImage.Dispose() }
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}
