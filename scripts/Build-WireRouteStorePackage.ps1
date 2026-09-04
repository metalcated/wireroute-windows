[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')]
    [string] $Version = '1.1.1.0',

    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platform = @('x64', 'ARM64'),

    [string] $PackageIdentityName = 'WireRoute.Development',

    [string] $PackagePublisher = 'CN=WireRoute Development',

    [string] $PublisherDisplayName = 'WireRoute contributors',

    [switch] $StoreUpload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$developmentIdentityName = 'WireRoute.Development'
$developmentPublisher = 'CN=WireRoute Development'
if ($StoreUpload -and (
    $PackageIdentityName -eq $developmentIdentityName -or
    $PackagePublisher -eq $developmentPublisher)) {
    throw 'Store uploads require the exact Package Identity Name and Publisher values from Partner Center.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageProject = Join-Path $repositoryRoot 'packaging\WireRoute.Store\WireRoute.Store.wapproj'
$manifestTemplate = Join-Path $repositoryRoot 'packaging\WireRoute.Store\Package.appxmanifest'
$workingRoot = Join-Path $repositoryRoot ".distfiles\WireRoute.Store\$Version"
$manifestPath = Join-Path $workingRoot 'Package.appxmanifest'
$artifactKind = if ($StoreUpload) { 'store-upload' } else { 'development' }
$outputRoot = Join-Path $repositoryRoot "installer\dist\store\$Version\$artifactKind"

& (Join-Path $PSScriptRoot 'Generate-WireRouteStoreAssets.ps1')

New-Item -ItemType Directory -Path $workingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

[xml] $manifest = Get-Content -LiteralPath $manifestTemplate -Raw
$manifest.Package.Identity.Name = $PackageIdentityName
$manifest.Package.Identity.Publisher = $PackagePublisher
$manifest.Package.Identity.Version = $Version
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Save($manifestPath)

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}

$visualStudioRoot = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath).Trim()
if ([string]::IsNullOrWhiteSpace($visualStudioRoot)) {
    throw 'Visual Studio with MSBuild was not found.'
}

$msbuild = Join-Path $visualStudioRoot 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "MSBuild was not found at '$msbuild'."
}

$buildMode = if ($StoreUpload) { 'StoreUpload' } else { 'SideloadOnly' }
foreach ($targetPlatform in $Platform) {
    $runtimeIdentifier = if ($targetPlatform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
    $platformOutput = Join-Path $outputRoot $targetPlatform
    New-Item -ItemType Directory -Path $platformOutput -Force | Out-Null

    & $msbuild $packageProject `
        /restore `
        /m `
        /nologo `
        /verbosity:minimal `
        /p:Configuration=Release `
        /p:Platform=$targetPlatform `
        /p:RuntimeIdentifier=$runtimeIdentifier `
        /p:AppxBundle=Never `
        /p:AppxPackageDir="$platformOutput\" `
        /p:AppxPackageSigningEnabled=false `
        /p:GenerateAppxPackageOnBuild=true `
        /p:UapAppxPackageBuildMode=$buildMode `
        /p:WireRoutePackageManifest="$manifestPath"

    if ($LASTEXITCODE -ne 0) {
        throw "The $targetPlatform Store package build failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot 'Test-WireRouteStorePackage.ps1') `
    -Version $Version `
    -Platform $Platform `
    -ArtifactKind $artifactKind `
    -PackageIdentityName $PackageIdentityName `
    -PackagePublisher $PackagePublisher

Write-Host "WireRoute $Version Store package output is available in $outputRoot"
