[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $SourceDirectory,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.1.1',

    [string] $SigningCertificateThumbprint,

    [ValidatePattern('^https://')]
    [string] $TimestampServer = 'https://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $repositoryRoot 'installer'
$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$wixRoot = Join-Path $installerRoot '.deps\wix\bin'
$wixArchive = Join-Path $repositoryRoot '.distfiles\wix314-binaries.zip'
$wixArchiveHash = '6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31'
$wixDownload = 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip'
$wireRoutePlatform = if ($Platform -eq 'x64') { 'amd64' } else { 'arm64' }
$wixArchitecture = if ($Platform -eq 'x64') { 'x64' } else { 'arm64' }
$outputRoot = Join-Path $repositoryRoot ".distfiles\WireRoute.Installer\$wireRoutePlatform\$Version"
$distRoot = Join-Path $installerRoot 'dist'
$msiPath = Join-Path $distRoot "WireRoute-$Platform-$Version.msi"

foreach ($requiredFile in @('WireRoute.exe', 'WireRoute.dll', 'wireguard.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $requiredFile) -PathType Leaf)) {
        throw "WireRoute installer source is missing $requiredFile."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $wixRoot 'heat.exe') -PathType Leaf)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $wixArchive) -Force | Out-Null
    if (-not (Test-Path -LiteralPath $wixArchive -PathType Leaf)) {
        Invoke-WebRequest -Uri $wixDownload -OutFile $wixArchive
    }
    $actualHash = (Get-FileHash -LiteralPath $wixArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $wixArchiveHash) {
        throw 'The downloaded WiX archive did not match its pinned SHA-256 hash.'
    }
    New-Item -ItemType Directory -Path $wixRoot -Force | Out-Null
    Expand-Archive -LiteralPath $wixArchive -DestinationPath $wixRoot -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    $signToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
    $signToolPath = if ($null -ne $signToolCommand) { $signToolCommand.Source } else { $null }
    if ($null -eq $signToolPath) {
        $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signToolPath = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ($null -eq $signToolPath) {
        throw 'signtool.exe is required when a signing certificate thumbprint is supplied.'
    }
    foreach ($binaryName in @('WireRoute.exe', 'WireRoute.dll', 'wireguard.exe')) {
        & $signToolPath sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServer /td SHA256 /d 'WireRoute' (Join-Path $sourceRoot $binaryName)
        if ($LASTEXITCODE -ne 0) {
            throw "Signing $binaryName failed with exit code $LASTEXITCODE."
        }
    }
}

$heat = Join-Path $wixRoot 'heat.exe'
$candle = Join-Path $wixRoot 'candle.exe'
$light = Join-Path $wixRoot 'light.exe'
$harvestedSource = Join-Path $outputRoot 'WireRouteFiles.wxs'
$harvestedObject = Join-Path $outputRoot 'WireRouteFiles.wixobj'
$productObject = Join-Path $outputRoot 'WireRouteProduct.wixobj'

& $heat dir $sourceRoot -nologo -cg WireRouteFiles -dr WireRouteFolder -scom -sreg -sfrag -srd -ag -var var.WireRouteSourceDir -out $harvestedSource
if ($LASTEXITCODE -ne 0) {
    throw "WiX harvesting failed with exit code $LASTEXITCODE."
}

$iconPath = Join-Path $repositoryRoot 'ui\icon\wireroute.ico'
& $candle -nologo -arch $wixArchitecture "-dWireRouteSourceDir=$sourceRoot" "-dWireRouteIconPath=$iconPath" "-dWireRouteVersion=$Version" "-dWireRoutePlatform=$wireRoutePlatform" -out $productObject (Join-Path $installerRoot 'wireroute.wxs')
if ($LASTEXITCODE -ne 0) {
    throw "WireRoute WiX compilation failed with exit code $LASTEXITCODE."
}
& $candle -nologo -arch $wixArchitecture "-dWireRouteSourceDir=$sourceRoot" -out $harvestedObject $harvestedSource
if ($LASTEXITCODE -ne 0) {
    throw "WireRoute file-list compilation failed with exit code $LASTEXITCODE."
}
& $light -nologo -spdb -sice:ICE03 -sice:ICE39 -sice:ICE61 -out $msiPath $productObject $harvestedObject
if ($LASTEXITCODE -ne 0) {
    throw "WireRoute MSI linking failed with exit code $LASTEXITCODE."
}

if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    & $signToolPath sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServer /td SHA256 /d 'WireRoute Setup' $msiPath
    if ($LASTEXITCODE -ne 0) {
        throw "Signing the WireRoute MSI failed with exit code $LASTEXITCODE."
    }
}

Write-Host "WireRoute $Platform installer created at $msiPath"
