# Building, running, and developing

## Supported development targets

WireRoute release validation targets Windows 11 on native x64 and ARM64. The WinUI project declares `10.0.19041.0` as its minimum supported platform version, but Windows 10 is not a release-validation target.

Install:

- Git for Windows;
- Visual Studio 2022 with **Desktop development with .NET**;
- the Windows 11 SDK;
- .NET 10 SDK;
- Go 1.25 or newer; and
- PowerShell 7 or Windows PowerShell 5.1.

The installer script downloads WiX Toolset 3.14.1 on first use and verifies the archive against a pinned SHA-256 hash.

Clone the repository and use `main`:

```powershell
git clone https://github.com/metalcated/wireroute-windows.git
Set-Location wireroute-windows
git switch main
```

## Managed build and tests

Build the x64 WinUI application:

```powershell
dotnet build src\WireRoute.App\WireRoute.App.csproj -c Release -p:Platform=x64
```

Use `-p:Platform=ARM64` for the ARM64 build.

Run the managed and focused native test suites:

```powershell
dotnet test tests\WireRoute.Core.Tests\WireRoute.Core.Tests.csproj -c Release
dotnet test tests\WireRoute.RouterOS.Tests\WireRoute.RouterOS.Tests.csproj -c Release
dotnet test tests\WireRoute.Storage.Tests\WireRoute.Storage.Tests.csproj -c Release -p:Platform=x64
go test ./tunnel ./manager ./driver
```

The Storage tests exercise Windows DPAPI and should run on Windows. Re-run them with the matching native platform when validating ARM64.

## Preparing the native backend

The repository root `build.bat` downloads and verifies the inherited Windows toolchain and WireGuardNT package, renders resources, and builds the native backend:

```powershell
.\build.bat
```

This creates the architecture-specific resource objects and native outputs used by the WireRoute release script. The x86 output is inherited from upstream WireGuard for Windows; WireRoute releases only x64 and ARM64.

On WSL or another Linux environment, `make amd64/wireguard.exe arm64/wireguard.exe` can prepare the native backends when the required MinGW and ImageMagick tools are available. A complete WinUI/MSI release still requires Windows.

## Publishing and running the WinUI app

Publish a complete unpackaged application into the matching architecture directory:

```powershell
.\scripts\Publish-WireRouteApp.ps1 -Platform x64
.\scripts\Publish-WireRouteApp.ps1 -Platform ARM64
```

The default destinations are `amd64\` and `arm64\`. The native `wireguard.exe` must already be present beside the published application.

Run the desired architecture:

```powershell
.\amd64\WireRoute.exe
```

Do not copy only `WireRoute.exe` and the managed assemblies. The unpackaged application also requires generated `.xbf` and `.pri` resources, Windows App SDK files, native dependencies, assets, and the matching backend. An incomplete directory can compile successfully and then fail before creating a window.

Closing the main window leaves WireRoute in the notification area. In default mode, connect and disconnect request elevation only for the tunnel operation. Persistent VPN is opt-in and creates an automatic per-tunnel service; it does not install the inherited `WireGuardManager` service.

## Building release artifacts

After native resources have been prepared, create both architectures, MSI installers, portable ZIPs, and the checksum manifest:

```powershell
.\scripts\Build-WireRouteRelease.ps1 -Version 1.1.1
```

Outputs are written to `installer\dist`:

```text
WireRoute-x64-1.1.1.msi
WireRoute-x64-1.1.1.zip
WireRoute-ARM64-1.1.1.msi
WireRoute-ARM64-1.1.1.zip
WireRoute-1.1.1-SHA256SUMS.txt
```

`Build-WireRouteInstaller.ps1` can build one MSI from an already staged application directory. `Publish-WireRouteApp.ps1` and `Build-WireRouteInstaller.ps1` are lower-level helpers; the release script is the canonical full build.

Validate that all expected files exist, the MSI and ZIP containers are readable, and every payload matches the checksum manifest:

```powershell
.\scripts\Test-WireRouteRelease.ps1 -Version 1.1.1
```

## GitHub Actions release workflow

`.github/workflows/release.yml` runs the managed tests, focused native tests, and an x64 WinUI compile on pushes and pull requests targeting `main`. A manual workflow run additionally prepares the native toolchain, packages x64 and ARM64, validates the release set, and retains the unsigned files as a GitHub Actions artifact for 14 days.

The manual run accepts a numeric `x.y.z` version and a separate **Publish the unsigned artifacts as a GitHub pre-release** choice. Leave publishing disabled for a build-only validation run. Enabling it creates an immutable version tag and an explicitly labeled unsigned pre-release from the same validated files. It cannot replace an existing version.

Until the SignPath Foundation application is accepted and the signing workflow is active, this pipeline intentionally cannot publish a stable or signed release. The production signing stage will be added between validation and release publication and will retain the existing manual-approval boundary.

## Signing

For a certificate available to SignTool through the Windows certificate store, pass its SHA-1 thumbprint:

```powershell
.\scripts\Build-WireRouteRelease.ps1 `
    -Version 1.1.1 `
    -SigningCertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

The default RFC 3161 timestamp service is `https://timestamp.digicert.com` and can be replaced with `-TimestampServer` using another HTTPS URL.

The current local signing path signs `WireRoute.exe`, `WireRoute.dll`, the native `wireguard.exe`, and each MSI. Production signing may instead be performed by the approved SignPath pipeline described in [Code-signing policy](CODE_SIGNING_POLICY.md). In either path, verify signatures on the final staged files and MSI rather than assuming that a successful build produced signed artifacts.

## Installer behavior

The WireRoute MSI is per-machine, installs under Program Files, creates a Start menu shortcut, and does not automatically launch the application. It supports standard quiet MSI deployment; see [Enterprise deployment](enterprise.md).

## Localization

The inherited Go backend contains upstream WireGuard localization resources, but the WireRoute WinUI interface does not currently have a supported localization contribution workflow. Do not use the inherited CrowdIn instructions as a WireRoute UI process. When WinUI localization is introduced, document its resource format and validation commands here.

## Command-line tooling

The upstream `wg.exe` utility can inspect WireGuardNT adapters when run with sufficient permissions, but it is not part of the supported WireRoute UI workflow or release artifact contract. Use it as an advanced WireGuard diagnostic tool, not as a replacement for WireRoute profile storage or service ownership.
