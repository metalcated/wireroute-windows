[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')]
    [string] $Version = '1.1.1.0',

    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platform = @('x64', 'ARM64'),

    [ValidateSet('development', 'store-upload')]
    [string] $ArtifactKind = 'development',

    [string] $PackageIdentityName = 'WireRoute.Development',

    [string] $PackagePublisher = 'CN=WireRoute Development'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repositoryRoot "installer\dist\store\$Version\$ArtifactKind"

function Get-PeMachine {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry] $Entry
    )

    $entryStream = $Entry.Open()
    $stream = [System.IO.MemoryStream]::new()
    try {
        $entryStream.CopyTo($stream)
    } finally {
        $entryStream.Dispose()
    }
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64) {
            throw "'$($Entry.FullName)' is too small to be a PE image."
        }

        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset + 6 -gt $stream.Length) {
            throw "'$($Entry.FullName)' has an invalid PE header offset."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$($Entry.FullName)' does not contain a PE signature."
        }

        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

foreach ($targetPlatform in $Platform) {
    $packageName = "WireRoute.Store_${Version}_${targetPlatform}.msix"
    $packages = @(
        Get-ChildItem -LiteralPath (Join-Path $outputRoot $targetPlatform) -Recurse -File -Filter $packageName
    )
    if ($packages.Count -ne 1) {
        throw "Expected exactly one '$packageName' below '$outputRoot', found $($packages.Count)."
    }

    $package = $packages[0]
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[$entry.FullName.Replace('\', '/')] = $entry
        }

        if (-not $entries.ContainsKey('AppxManifest.xml')) {
            throw "'$($package.Name)' does not contain AppxManifest.xml."
        }

        $manifestReader = [System.IO.StreamReader]::new($entries['AppxManifest.xml'].Open())
        try {
            [xml] $manifest = $manifestReader.ReadToEnd()
        } finally {
            $manifestReader.Dispose()
        }

        $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaceManager.AddNamespace('f', $manifest.DocumentElement.NamespaceURI)
        $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaceManager)
        $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespaceManager)
        if ($null -eq $identity -or $null -eq $application) {
            throw "'$($package.Name)' is missing its package identity or application declaration."
        }

        $expectedArchitecture = $targetPlatform.ToLowerInvariant()
        if ($identity.Name -ne $PackageIdentityName -or
            $identity.Publisher -ne $PackagePublisher -or
            $identity.Version -ne $Version -or
            $identity.ProcessorArchitecture -ne $expectedArchitecture) {
            throw "'$($package.Name)' has unexpected identity metadata."
        }

        if ($application.EntryPoint -ne 'Windows.FullTrustApplication') {
            throw "'$($package.Name)' is not declared as a full-trust desktop application."
        }

        $applicationPath = $application.Executable.Replace('\', '/')
        if (-not $entries.ContainsKey($applicationPath)) {
            throw "'$($package.Name)' does not contain its declared executable '$applicationPath'."
        }

        $applicationDirectory = $applicationPath.Substring(0, $applicationPath.LastIndexOf('/') + 1)
        $backendPath = $applicationDirectory + 'wireguard.exe'
        if (-not $entries.ContainsKey($backendPath)) {
            throw "'$($package.Name)' does not contain wireguard.exe beside the application executable."
        }
        if ($entries.ContainsKey('wireguard.exe')) {
            throw "'$($package.Name)' incorrectly contains an extra wireguard.exe at package root."
        }

        $expectedMachine = if ($targetPlatform -eq 'x64') { 0x8664 } else { 0xAA64 }
        if ((Get-PeMachine $entries[$applicationPath]) -ne $expectedMachine -or
            (Get-PeMachine $entries[$backendPath]) -ne $expectedMachine) {
            throw "'$($package.Name)' contains an executable for the wrong processor architecture."
        }

        $requiredEntries = @(
            'Assets/Square150x150Logo.scale-200.png',
            'Assets/Square44x44Logo.scale-200.png',
            'Assets/Square44x44Logo.targetsize-24_altform-unplated.png',
            'Assets/StoreLogo.png',
            'Assets/Wide310x150Logo.scale-200.png',
            'resources.pri'
        )
        foreach ($requiredEntry in $requiredEntries) {
            if (-not $entries.ContainsKey($requiredEntry)) {
                throw "'$($package.Name)' is missing required entry '$requiredEntry'."
            }
        }

        $dependencyNames = @(
            $manifest.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $namespaceManager)
            | ForEach-Object { $_.Name }
        )
        if ($dependencyNames -notcontains 'Microsoft.WindowsAppRuntime.2') {
            throw "'$($package.Name)' does not declare the Windows App Runtime framework dependency."
        }

        $capabilityNames = @(
            $manifest.SelectNodes('/f:Package/f:Capabilities/*', $namespaceManager)
            | ForEach-Object { $_.Name }
        )
        foreach ($requiredCapability in @('internetClient', 'privateNetworkClientServer', 'runFullTrust')) {
            if ($capabilityNames -notcontains $requiredCapability) {
                throw "'$($package.Name)' is missing required capability '$requiredCapability'."
            }
        }

        Write-Host (
            "Validated {0}: {1:N1} MiB, {2}, {3} files." -f
            $package.Name,
            ($package.Length / 1MB),
            $identity.ProcessorArchitecture,
            $archive.Entries.Count)
    } finally {
        $archive.Dispose()
    }

    if ($ArtifactKind -eq 'store-upload') {
        $uploadName = "WireRoute.Store_${Version}_${targetPlatform}.msixupload"
        $uploads = @(
            Get-ChildItem -LiteralPath (Join-Path $outputRoot $targetPlatform) -Recurse -File -Filter $uploadName
        )
        if ($uploads.Count -ne 1) {
            throw "Expected exactly one '$uploadName' below '$outputRoot', found $($uploads.Count)."
        }

        $uploadArchive = [System.IO.Compression.ZipFile]::OpenRead($uploads[0].FullName)
        try {
            $uploadEntries = @($uploadArchive.Entries.FullName)
            $symbolName = "WireRoute.Store_${Version}_${targetPlatform}.appxsym"
            if ($uploadEntries -notcontains $packageName -or $uploadEntries -notcontains $symbolName) {
                throw "'$uploadName' must contain '$packageName' and '$symbolName'."
            }
        } finally {
            $uploadArchive.Dispose()
        }

        Write-Host "Validated Store upload container $uploadName."
    }
}

Write-Host "WireRoute $Version Store package validation succeeded."
