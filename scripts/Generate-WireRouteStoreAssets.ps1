[CmdletBinding()]
param(
    [string] $Source,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $repositoryRoot 'ui\icon\wireroute.png'
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot 'packaging\WireRoute.Store\Assets'
}

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "The source icon '$Source' does not exist."
}

Add-Type -AssemblyName System.Drawing.Common
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$sourceImage = [System.Drawing.Image]::FromFile($Source)
try {
    $background = ([System.Drawing.Bitmap] $sourceImage).GetPixel(0, 0)

    function Write-StoreAsset {
        param(
            [Parameter(Mandatory = $true)]
            [string] $Name,
            [Parameter(Mandatory = $true)]
            [int] $Width,
            [Parameter(Mandatory = $true)]
            [int] $Height,
            [Parameter(Mandatory = $true)]
            [int] $LogoSize
        )

        $bitmap = [System.Drawing.Bitmap]::new(
            $Width,
            $Height,
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $bitmap.SetResolution(96, 96)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear($background)
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $x = [int] (($Width - $LogoSize) / 2)
                $y = [int] (($Height - $LogoSize) / 2)
                $graphics.DrawImage($sourceImage, $x, $y, $LogoSize, $LogoSize)
            } finally {
                $graphics.Dispose()
            }

            $path = Join-Path $Destination $Name
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $bitmap.Dispose()
        }
    }

    Write-StoreAsset -Name 'StoreLogo.png' -Width 50 -Height 50 -LogoSize 50
    Write-StoreAsset -Name 'Square44x44Logo.scale-200.png' -Width 88 -Height 88 -LogoSize 88
    Write-StoreAsset -Name 'Square44x44Logo.targetsize-24_altform-unplated.png' -Width 24 -Height 24 -LogoSize 24
    Write-StoreAsset -Name 'Square150x150Logo.scale-200.png' -Width 300 -Height 300 -LogoSize 300
    Write-StoreAsset -Name 'Wide310x150Logo.scale-200.png' -Width 620 -Height 300 -LogoSize 300
} finally {
    $sourceImage.Dispose()
}

Write-Host "WireRoute Store assets generated in $Destination"
