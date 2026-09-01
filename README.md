# WireRoute for Windows

WireRoute is a native WinUI 3 WireGuard and RouterOS client for Windows 11. Its Blue Nordic interface and workflows mirror the released WireRoute macOS app while tunnel execution remains on the proven WireGuard for Windows engine and WireGuardNT driver.

Native releases are produced independently for x64 and ARM64. x86 emulation is not a release target.

## Support, privacy, and project policies

- [Official downloads and verification](docs/DOWNLOADS.md)
- [Secure RouterOS WireGuard setup](docs/ROUTEROS_SETUP.md)
- [Support and contact](docs/SUPPORT.md)
- [Privacy policy](docs/PRIVACY.md)
- [Security reporting](docs/SECURITY.md)
- [Code signing policy](docs/CODE_SIGNING_POLICY.md)
- [Legal and open-source notices](docs/LEGAL.md)
- [MIT License](COPYING)

## Code signing

WireRoute is applying for sponsored open-source code signing through SignPath Foundation. Until the application is accepted and the verified release pipeline is active, published Windows artifacts remain unsigned and should be checked against the SHA-256 manifest included with each release.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by SignPath Foundation.

## Current capabilities

- Protected WireGuard profile import, ZIP import/export, creation, editing, QR display, and key copy.
- Split and Full routing, profile DNS, service-free encrypted DNS, private-IP exclusion, and Ethernet/Wi-Fi on-demand rules.
- Demand-start WireGuardNT tunnel activation with live transfer rates, handshakes, bounded native logs, and protected activity history.
- RouterOS HTTPS connection storage, certificate pin review, read-only discovery, managed-peer filtering, client-address suggestions, reviewed peer creation, and automatic local profile import.
- A persistent Windows notification icon, close-to-tray behavior, a macOS-matched context menu, Blue Nordic/System themes, and selectable icon styles.
- Self-contained portable ZIPs and per-machine MSI installers for x64 and ARM64.

## Service-free application model

WireRoute does not install or require the persistent `WireGuardManager` service. Starting or stopping a tunnel displays the normal Windows elevation prompt. A manual-start per-tunnel service exists only while that tunnel is connected and is deleted on disconnect.

## Build and test

Requirements are Visual Studio 2022 with Desktop development with .NET, the Windows 11 SDK, .NET 10, and Go 1.25 or newer. Build the managed app and run the test suites with:

```powershell
dotnet build src\WireRoute.App\WireRoute.App.csproj -c Release -p:Platform=x64
dotnet test tests\WireRoute.Core.Tests\WireRoute.Core.Tests.csproj -c Release
dotnet test tests\WireRoute.RouterOS.Tests\WireRoute.RouterOS.Tests.csproj -c Release
dotnet test tests\WireRoute.Storage.Tests\WireRoute.Storage.Tests.csproj -c Release -p:Platform=x64
go test ./tunnel ./manager ./driver
```

Build the native x64 and ARM64 backends, self-contained WinUI applications, MSI installers, portable ZIPs, and SHA-256 manifest with:

```powershell
.\scripts\Build-WireRouteRelease.ps1 -Version 1.0.0
```

Pass `-SigningCertificateThumbprint` to sign release binaries and installers with a certificate in the Windows certificate store. Output is written to `installer\dist`.

## Architecture documentation

WireRoute-specific decisions and the macOS parity baseline are documented in:

- [`wireroute-windows-architecture.md`](docs/wireroute-windows-architecture.md)
- [`macos-routeros-parity.md`](docs/macos-routeros-parity.md)

The inherited WireGuard for Windows security and enterprise references remain available in:

- [`adminregistry.md`](docs/adminregistry.md) &ndash; A list of registry keys settable by the system administrator for changing the behavior of the application.
- [`attacksurface.md`](docs/attacksurface.md) &ndash; A discussion of the various components from a security perspective, so that future auditors of this code have a head start in assessing its security design.
- [`enterprise.md`](docs/enterprise.md) &ndash; A summary of various features and tips for making the application usable in enterprise settings.
- [`netquirk.md`](docs/netquirk.md) &ndash; A description of various networking quirks and "kill-switch" semantics.

## License

This repository remains MIT-licensed. WireRoute-specific work is copyright its contributors; inherited WireGuard for Windows portions retain their original notices.

```text
Copyright (C) 2018-2026 WireGuard LLC. All Rights Reserved.

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```
