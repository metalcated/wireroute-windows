# WireRoute Code Signing Policy

## Status

WireRoute is applying for the SignPath Foundation open-source code-signing program. Public artifacts are not represented as SignPath-signed until the application is accepted, the verified release pipeline is active, and the individual artifact has a valid Authenticode signature issued in the name of SignPath Foundation.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by SignPath Foundation.

## Source and release boundary

- Source repository: [metalcated/wireroute-windows](https://github.com/metalcated/wireroute-windows)
- Release branch: `main`
- Official downloads: [GitHub Releases](https://github.com/metalcated/wireroute-windows/releases)
- Supported Windows architectures: x64 and ARM64

Release artifacts submitted for Foundation signing must be produced by a reviewed workflow stored in this repository and executed on GitHub-hosted runners. SignPath trusted-build and origin verification must bind the artifact to this repository, branch or release tag, commit, and workflow run.

Every release signing request requires manual approval. Local builds, pull-request builds, and artifacts produced outside the verified release workflow must not use the Foundation release certificate.

## Signing roles

- Committer and reviewer: [metalcated](https://github.com/metalcated)
- Signing approver: [metalcated](https://github.com/metalcated)

External contributions require review by the maintainer before they can enter a release. If the maintainer group grows, these roles may move to explicit repository teams while preserving separate source-review and release-approval responsibilities.

## Artifact policy

- The x64 and ARM64 MSI installers and WireRoute-maintained Windows binaries are the intended signed artifacts.
- Product-name and product-version metadata must be consistent across each release and enforced by the SignPath artifact configuration.
- Only binaries that SignPath Foundation approves as maintained by WireRoute may receive the Foundation signature.
- Third-party and upstream binaries are not re-signed with WireRoute's Foundation policy. They retain their upstream signatures or remain included without a WireRoute signature as permitted by Foundation policy.
- Release downloads include SHA-256 hashes so users can verify transport and mirror integrity independently of Authenticode.

## Privacy and security

WireRoute's [privacy policy](PRIVACY.md) describes local data, user-requested network connections, exports, and removal. Security vulnerabilities must be reported according to [SECURITY.md](SECURITY.md).

Signing approval confirms provenance and policy compliance for the submitted artifact; it is not a substitute for security review, malware scanning, dependency review, or release testing.
