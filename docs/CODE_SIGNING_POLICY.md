# WireRoute Code Signing Policy

## Status

WireRoute does not currently have an active code-signing sponsorship or publicly trusted signing identity. The SignPath Foundation application was not accepted, and its proposed signing integration is not active. GitHub releases remain explicitly labeled unsigned previews. Microsoft Store registration is a separate process.

The requirements below describe the boundary for a future approved signing integration, not an existing signing service. No artifact may be represented as signed unless its final Authenticode signature is valid and matches the approved publisher identity.

## Source and release boundary

- Source repository: [metalcated/wireroute-windows](https://github.com/metalcated/wireroute-windows)
- Release branch: `main`
- Official downloads: [GitHub Releases](https://github.com/metalcated/wireroute-windows/releases)
- Supported Windows architectures: x64 and ARM64

Release artifacts submitted for signing must be produced by a reviewed workflow stored in this repository and executed on GitHub-hosted runners. Provenance verification must bind the artifact to this repository, branch or release tag, commit, and workflow run.

Every release signing request requires manual approval. Local builds, pull-request builds, and artifacts produced outside the verified release workflow must not use a production release signing identity.

## Signing roles

- Committer and reviewer: [metalcated](https://github.com/metalcated)
- Signing approver: [metalcated](https://github.com/metalcated)

External contributions require review by the maintainer before they can enter a release. If the maintainer group grows, these roles may move to explicit repository teams while preserving separate source-review and release-approval responsibilities.

## Artifact policy

- The x64 and ARM64 MSI installers and WireRoute-maintained Windows binaries are the intended signed artifacts.
- Product-name and product-version metadata must be consistent across each release and verified before signing.
- Only binaries maintained by WireRoute and approved under the selected provider's policy may receive its release signature.
- Third-party and upstream binaries retain their existing signatures and must not be re-signed as WireRoute-maintained artifacts without explicit review and approval.
- Release downloads include SHA-256 hashes so users can verify transport and mirror integrity independently of Authenticode.

## Privacy and security

WireRoute's [privacy policy](PRIVACY.md) describes local data, user-requested network connections, exports, and removal. Security vulnerabilities must be reported according to [SECURITY.md](SECURITY.md).

Signing approval confirms provenance and policy compliance for the submitted artifact; it is not a substitute for security review, malware scanning, dependency review, or release testing.
