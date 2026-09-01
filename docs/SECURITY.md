# WireRoute Security Policy

## Reporting a vulnerability

Please do not disclose a suspected vulnerability or exploit details in a public GitHub issue.

Use GitHub's [private vulnerability reporting form](https://github.com/metalcated/wireroute-windows/security/advisories/new). A GitHub sign-in is required. If the private form is unavailable, open a minimal [support issue](https://github.com/metalcated/wireroute-windows/issues/new) asking the maintainer to establish a private contact channel, without including technical details or sensitive data.

Include the affected WireRoute version and architecture, Windows version, impact, reproduction conditions, and a suggested remediation if available. Never include live private keys, RouterOS passwords, complete production tunnel configurations, or unredacted recovery data. Use generated test credentials and documentation-only addresses.

## Supported versions

Security fixes target the `main` development branch and the most recent published Windows release. Older builds may be asked to update before a report is investigated.

## Scope

This policy covers the WireRoute Windows application, WireGuardNT tunnel integration, per-tunnel Windows services, routing and DNS logic, protected local storage, activity and diagnostic data, installer, and RouterOS integration.

Problems in a user-operated VPN endpoint or RouterOS installation, Windows itself, the official WireGuard project, or an unrelated third-party service should be reported to that project's owner unless WireRoute's integration is the source of the problem.
