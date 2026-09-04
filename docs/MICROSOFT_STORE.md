# Microsoft Store packaging

WireRoute's Microsoft Store target is a full-trust MSIX package in this repository. It is an additional distribution format, not a fork of the application and not part of the `wireroute-nt` driver repository.

## Repository boundaries

- `src/WireRoute.App` remains the single application implementation.
- `packaging/WireRoute.Store` owns the MSIX manifest and package assets.
- `installer` continues to own the existing MSI and portable release artifacts.
- `wireroute-nt` remains an independently versioned WireGuardNT source fork. Store packaging must not replace the official signed WireGuardNT package with a locally built driver.

## Development package

The checked-in manifest uses the clearly non-production identity `WireRoute.Development`. It exists only so packaging can be compiled and inspected before Partner Center registration is complete.

Prepare the native backend, then build unsigned development packages:

```powershell
.\build.bat
.\scripts\Build-WireRouteStorePackage.ps1
```

The output is written below `installer\dist\store\1.1.1.0\development`. An unsigned package cannot be installed directly. Local installation testing requires a development certificate whose subject matches the manifest publisher and whose public certificate is trusted on the test machine.

Every build runs `scripts\Test-WireRouteStorePackage.ps1`. The validator opens each package without installing it and verifies the manifest identity, full-trust entry point, framework dependency, capabilities, visual assets, application/backend placement, and x64 or ARM64 PE architecture.

Pull requests to `main` also run `.github\workflows\store-msix.yml`. That workflow produces validated, unsigned development packages as short-lived GitHub Actions artifacts. They are structural test artifacts, not Store submissions.

## Partner Center identity

Do not guess or normalize Store identity values. Copy these exact, case-sensitive values from **Partner Center > Product management > Product identity**:

- Package/Identity/Name
- Package/Identity/Publisher
- Package/Properties/PublisherDisplayName

Create Store-upload artifacts only after those values are available:

```powershell
.\scripts\Build-WireRouteStorePackage.ps1 `
  -Version 1.1.1.0 `
  -StoreUpload `
  -PackageIdentityName '<Partner Center identity name>' `
  -PackagePublisher '<Partner Center publisher>' `
  -PublisherDisplayName '<Partner Center display name>'
```

The script rejects `-StoreUpload` when either development identity value is still present.
Store-upload output is isolated below `installer\dist\store\1.1.1.0\store-upload`, so it cannot be confused with development-identity packages.

## Runtime model

The MSIX package is architecture-specific and includes:

- The self-contained .NET application output for x64 or ARM64.
- A framework-dependent Windows App SDK declaration serviced by the Store.
- The matching inherited `wireguard.exe` tunnel backend built with the official checksum-pinned WireGuardNT package.

The package currently declares `runFullTrust`, Internet client access, and private-network client/server access. It intentionally does not declare packaged or LocalSystem service capabilities until Microsoft confirms the acceptable service model.

## Eligibility gate

Before public submission, validate on a clean Windows 11 machine or VM:

1. Install the development-signed MSIX.
2. Launch from Start and from the notification area.
3. Import, edit, export, activate, and deactivate a profile.
4. Exercise RouterOS discovery and provisioning.
5. Verify WireGuardNT driver loading and per-tunnel service creation.
6. Enable Persistent VPN, reboot, verify reconnection, then disable it.
7. Upgrade to a higher package version without losing protected profiles or settings.
8. Uninstall and confirm that WireRoute-owned services, adapters, and persistent service configurations are removed.
9. Run the Windows App Certification Kit.

Certification notes must disclose WireGuardNT, UAC elevation, demand-start and persistent LocalSystem tunnel services, cleanup behavior, RouterOS network access, and the exact steps Microsoft can use to create or import a test profile.

Microsoft may reject the package because MSIX does not ordinarily deploy kernel drivers and packaged LocalSystem services require restricted capabilities that are rarely approved. A Store upload is therefore an eligibility probe until certification succeeds. Rejection must not be worked around by silently reducing security or bypassing the supported WireGuard tunnel engine.
