[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0',

    [string] $Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\WireRoute.App\WireRoute.App.csproj'

$runtimeIdentifier = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$architectureDirectory = if ($Platform -eq 'x64') { 'amd64' } else { 'arm64' }
$publishDirectory = Join-Path $repositoryRoot (
    ".distfiles\WireRoute.App\$runtimeIdentifier\" + [Guid]::NewGuid().ToString('N'))

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot $architectureDirectory
} elseif (-not [System.IO.Path]::IsPathRooted($Destination)) {
    $Destination = Join-Path $repositoryRoot $Destination
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    --self-contained true `
    --property:Platform=$Platform `
    --property:Version=$Version `
    --property:WindowsAppSDKSelfContained=true `
    --property:DebugSymbols=false `
    --property:DebugType=None `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "WireRoute.App publish failed with exit code $LASTEXITCODE."
}

$targetDirectoryOutput = dotnet msbuild $projectPath `
    --nologo `
    --property:Configuration=$Configuration `
    --property:Platform=$Platform `
    --property:RuntimeIdentifier=$runtimeIdentifier `
    --property:Version=$Version `
    --property:WindowsAppSDKSelfContained=true `
    -getProperty:TargetDir

if ($LASTEXITCODE -ne 0) {
    throw "Unable to locate the WireRoute.App architecture build output."
}

$targetDirectory = ([string]::Join([Environment]::NewLine, $targetDirectoryOutput)).Trim()
foreach ($resourceFile in @('WireRoute.pri', 'App.xbf', 'MainWindow.xbf')) {
    $resourcePath = Join-Path $targetDirectory $resourceFile
    if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
        throw "WireRoute.App architecture build output is missing $resourceFile."
    }

    Copy-Item -LiteralPath $resourcePath -Destination (Join-Path $publishDirectory $resourceFile) -Force
}

$requiredFiles = @(
    'WireRoute.exe',
    'WireRoute.dll',
    'WireRoute.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Assets\wireroute.ico',
    'Assets\wireroute.png'
)

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "Published WireRoute.App output is missing $requiredFile."
    }
}

Get-ChildItem -LiteralPath $publishDirectory -Force | Copy-Item -Destination $Destination -Recurse -Force

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $Destination $requiredFile) -PathType Leaf)) {
        throw "Staged WireRoute.App output is missing $requiredFile."
    }
}

Write-Host "WireRoute.App $Platform output staged at $Destination"
