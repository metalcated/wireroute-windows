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

WireRoute does not currently have an active code-signing sponsorship or publicly trusted signing identity. Release artifacts remain unsigned. A future release will be represented as signed only after its final artifacts pass Authenticode verification against the approved publisher identity.

During this interim period, the reviewed GitHub Actions workflow can publish only an explicitly labeled **unsigned pre-release**. Microsoft Store registration is separate and does not prevent publishing GitHub previews. There is no claim that these downloads are Microsoft Store-signed.

See the complete [WireRoute code-signing policy](CODE_SIGNING_POLICY.md).

## Verify a download

Download the release's `WireRoute-<version>-SHA256SUMS.txt` file and calculate the artifact's SHA-256 hash in PowerShell:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\WireRoute-x64-1.1.3.msi
```

Compare the complete hexadecimal value with the matching line in the manifest. A checksum detects a damaged or substituted download but does not establish publisher identity; publisher and signing-certificate verification will require a future publicly trusted Authenticode signing process.

Download WireRoute only from this repository's release page or a future distribution location linked from this repository.
