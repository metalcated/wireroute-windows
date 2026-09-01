[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $DistributionRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DistributionRoot)) {
    $DistributionRoot = Join-Path $repositoryRoot 'installer\dist'
}

$payloadNames = @(
    "WireRoute-ARM64-$Version.msi",
    "WireRoute-ARM64-$Version.zip",
    "WireRoute-x64-$Version.msi",
    "WireRoute-x64-$Version.zip"
)
$manifestName = "WireRoute-$Version-SHA256SUMS.txt"
$expectedNames = @($payloadNames + $manifestName)

foreach ($name in $expectedNames) {
    $path = Join-Path $DistributionRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The release is missing $name."
    }
    if ((Get-Item -LiteralPath $path).Length -eq 0) {
        throw "The release artifact $name is empty."
    }
}

$manifestPath = Join-Path $DistributionRoot $manifestName
$manifestLines = @(Get-Content -LiteralPath $manifestPath)
if ($manifestLines.Count -ne $payloadNames.Count) {
    throw "The checksum manifest contains $($manifestLines.Count) entries; expected $($payloadNames.Count)."
}

$manifestHashes = @{}
foreach ($line in $manifestLines) {
    if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/]+)$') {
        throw "The checksum manifest contains an invalid line: $line"
    }
    if ($manifestHashes.ContainsKey($Matches.name)) {
        throw "The checksum manifest contains duplicate entries for $($Matches.name)."
    }
    $manifestHashes[$Matches.name] = $Matches.hash
}

foreach ($name in $payloadNames) {
    if (-not $manifestHashes.ContainsKey($name)) {
        throw "The checksum manifest is missing $name."
    }
    $path = Join-Path $DistributionRoot $name
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $manifestHashes[$name]) {
        throw "The SHA-256 checksum for $name does not match the manifest."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($name in ($payloadNames | Where-Object { $_.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) })) {
    $path = Join-Path $DistributionRoot $name
    $archive = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($requiredEntry in @('WireRoute.exe', 'wireguard.exe')) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "$name does not contain $requiredEntry."
            }
        }
    } finally {
        $archive.Dispose()
    }
}

$compoundFileHeader = [byte[]](0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)
foreach ($name in ($payloadNames | Where-Object { $_.EndsWith('.msi', [StringComparison]::OrdinalIgnoreCase) })) {
    $path = Join-Path $DistributionRoot $name
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $header = [byte[]]::new($compoundFileHeader.Length)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length) {
            throw "$name is too short to be a valid MSI."
        }
        $headerHex = [BitConverter]::ToString($header)
        $expectedHeaderHex = [BitConverter]::ToString($compoundFileHeader)
        if ($headerHex -cne $expectedHeaderHex) {
            throw "$name does not have a valid MSI compound-file header."
        }
    } finally {
        $stream.Dispose()
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    Write-Host "$name Authenticode status: $($signature.Status)"
}

Write-Host "Validated $($expectedNames.Count) WireRoute $Version release files in $DistributionRoot."
