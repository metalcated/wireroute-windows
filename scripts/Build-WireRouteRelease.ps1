[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.1.1',

    [string] $SigningCertificateThumbprint,

    [ValidatePattern('^https://')]
    [string] $TimestampServer = 'https://timestamp.digicert.com',

    [switch] $SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repositoryRoot (
    ".distfiles\WireRoute.Release\$Version\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$distRoot = Join-Path $repositoryRoot 'installer\dist'

if (-not $SkipNativeBuild) {
    foreach ($resourceFile in @('resources_amd64.syso', 'resources_arm64.syso')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $resourceFile) -PathType Leaf)) {
            throw "The native build requires $resourceFile. Run the repository resource preparation first."
        }
    }
    $previousGoOs = $env:GOOS
    $previousGoArch = $env:GOARCH
    Push-Location $repositoryRoot
    try {
        $env:GOOS = 'windows'
        foreach ($architecture in @('amd64', 'arm64')) {
            $env:GOARCH = $architecture
            & go build -tags load_wgnt_from_rsrc -ldflags '-H windowsgui -s -w' -trimpath -buildvcs=false -o "$architecture\wireguard.exe" .
            if ($LASTEXITCODE -ne 0) {
                throw "The native $architecture build failed with exit code $LASTEXITCODE."
            }
        }
    } finally {
        Pop-Location
        if ($null -eq $previousGoOs) { Remove-Item Env:GOOS -ErrorAction SilentlyContinue } else { $env:GOOS = $previousGoOs }
        if ($null -eq $previousGoArch) { Remove-Item Env:GOARCH -ErrorAction SilentlyContinue } else { $env:GOARCH = $previousGoArch }
    }
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
foreach ($platform in @('x64', 'ARM64')) {
    $architectureDirectory = if ($platform -eq 'x64') { 'amd64' } else { 'arm64' }
    $stagingDirectory = Join-Path $releaseRoot $platform
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    foreach ($nativeFile in @('wireguard.exe')) {
        $nativePath = Join-Path (Join-Path $repositoryRoot $architectureDirectory) $nativeFile
        if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
            throw "The $platform native build is missing $nativeFile."
        }
        Copy-Item -LiteralPath $nativePath -Destination (Join-Path $stagingDirectory $nativeFile) -Force
    }

    & (Join-Path $PSScriptRoot 'Publish-WireRouteApp.ps1') -Platform $platform -Configuration Release -Version $Version -Destination $stagingDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "The $platform WireRoute app publish failed with exit code $LASTEXITCODE."
    }

    $developerDirectory = Join-Path $repositoryRoot $architectureDirectory
    Get-ChildItem -LiteralPath $stagingDirectory -Force |
        Copy-Item -Destination $developerDirectory -Recurse -Force

    & (Join-Path $PSScriptRoot 'Build-WireRouteInstaller.ps1') `
        -Platform $platform `
        -SourceDirectory $stagingDirectory `
        -Version $Version `
        -SigningCertificateThumbprint $SigningCertificateThumbprint `
        -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) {
        throw "The $platform WireRoute installer failed with exit code $LASTEXITCODE."
    }

    $zipPath = Join-Path $distRoot "WireRoute-$platform-$Version.zip"
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
}

$releaseFiles = Get-ChildItem -LiteralPath $distRoot -File |
    Where-Object { $_.Name -match "^WireRoute-(x64|ARM64)-$([regex]::Escape($Version))\.(msi|zip)$" } |
    Sort-Object Name
$manifestPath = Join-Path $distRoot "WireRoute-$Version-SHA256SUMS.txt"
$manifestLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllLines($manifestPath, $manifestLines, [System.Text.UTF8Encoding]::new($false))

Write-Host "WireRoute $Version release artifacts are available in $distRoot"
