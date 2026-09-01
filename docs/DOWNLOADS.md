# WireRoute Downloads

Official Windows releases are published on the [WireRoute GitHub Releases page](https://github.com/metalcated/wireroute-windows/releases).

Native builds are produced separately for x64 and ARM64:

- `WireRoute-x64-<version>.msi`
- `WireRoute-ARM64-<version>.msi`
- `WireRoute-x64-<version>.zip`
- `WireRoute-ARM64-<version>.zip`
- `WireRoute-<version>-SHA256SUMS.txt`

The MSI is the recommended installation format. Portable ZIPs are provided for development and testing workflows that do not require installer registration.

## Code-signing status

WireRoute is applying for the SignPath Foundation open-source code-signing program. Until the application is accepted and the verified release pipeline is active, release artifacts remain unsigned. A release is represented as signed only when Windows reports a valid Authenticode signature issued in the name of SignPath Foundation.

During this interim period, the reviewed GitHub Actions workflow can publish only an explicitly labeled **unsigned pre-release**. Stable signed releases remain disabled until the SignPath signing and approval stage is active.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by SignPath Foundation.

See the complete [WireRoute code-signing policy](CODE_SIGNING_POLICY.md).

## Verify a download

Download the release's `WireRoute-<version>-SHA256SUMS.txt` file and calculate the artifact's SHA-256 hash in PowerShell:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\WireRoute-x64-1.0.0.msi
```

Compare the complete hexadecimal value with the matching line in the manifest. A checksum detects a damaged or substituted download but does not establish publisher identity; Authenticode provides publisher and signing-certificate verification after the SignPath release process is active.

Download WireRoute only from this repository's release page or a future distribution location linked from this repository.
