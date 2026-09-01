# WireRoute Privacy Policy

Effective date: August 31, 2026

This policy describes the data practices of WireRoute for Windows.

## Summary

WireRoute does not require an account and does not operate a developer-controlled VPN, analytics, advertising, telemetry, or crash-reporting service. The project does not collect or sell personal data, track users, or use third-party advertising or analytics SDKs.

Based on the current source and data-flow audit, neither the WireRoute project nor an integrated third-party partner receives app data for storage or later access. Data processed locally by the app stays on the Windows PC unless the user explicitly exports or shares it or asks WireRoute to connect to a configured network service.

## Data stored on your PC

WireRoute stores information needed to provide the features you choose:

- Tunnel profiles, routing preferences, on-demand rules, endpoints, DNS servers, public keys, private keys, and other tunnel configuration material
- RouterOS connection details and credentials, trusted certificate pins, peer defaults, and recoverable client configuration material
- App appearance and peer-creation defaults
- Local activity entries and connection-session history, including transfer totals and handshake times
- Bounded tunnel diagnostic logs and live metrics while a tunnel is active

Profiles, RouterOS connections, certificate pins, recovery configurations, settings, and activity history are stored under `%LOCALAPPDATA%\WireRoute` and protected for the current Windows user with Windows Data Protection API (DPAPI). Diagnostic logs and runtime metrics are stored in profile-specific subdirectories under `%LOCALAPPDATA%\WireRoute\Runtime`.

When the user enables Persistent VPN, WireRoute installs the selected per-tunnel Windows service for automatic startup and places the required tunnel configuration in system-protected service storage. Disabling Persistent VPN removes WireRoute-managed persistent service copies without deleting the user's protected local profiles.

Diagnostic logs can contain network-interface names, endpoint hostnames or addresses, public keys, handshake status, and error details. Logs stay on the PC unless the user explicitly saves, exports, or shares them.

## Network connections

WireRoute makes network connections only to provide user-requested functionality:

- VPN traffic is sent to the WireGuard endpoint configured in the selected profile.
- Profile DNS requests are sent to DNS servers configured in that profile.
- If Encrypted DNS is selected, DNS messages are forwarded over HTTPS to the resolver selected by the user. Bootstrap addresses may be configured by the user or resolved through Windows before the tunnel starts.
- The optional RouterOS Peer Manager connects over HTTPS to the RouterOS address entered by the user. It reads WireGuard configuration and performs only peer changes that the user separately reviews and confirms.

Those systems are selected and controlled by the user or the user's network administrator. Their operators, DNS providers, internet providers, and network administrators may process traffic according to their own policies. The WireRoute project cannot access those systems or traffic.

## Exports and support requests

WireRoute exports data only when the user requests it and chooses a destination. Tunnel configurations, QR codes, ZIP exports, and copied key material can contain private keys and other sensitive network information. Protect exported files and clipboard contents and remove them when they are no longer needed.

If you contact the project through GitHub, GitHub handles the information you submit under its own privacy terms. Support requests are voluntary and should never contain private keys, passwords, complete configurations, or other secrets.

## Your choices and data removal

- Delete tunnel profiles and RouterOS connections in WireRoute when they are no longer needed.
- Clear previous activity from the profile Activity window.
- Delete exported configurations and logs from the destination where they were saved.
- Disable Persistent VPN before uninstalling if it has been enabled.
- After quitting and uninstalling WireRoute, remove `%LOCALAPPDATA%\WireRoute` if you also want to delete remaining per-user settings, protected records, logs, and runtime files.

WireRoute has no remote user account or developer-controlled data store, so there is no remote account data to request or delete.

## Changes

Material policy changes will be published in this repository with an updated effective date. The policy should be reviewed whenever application data flows or integrated dependencies change.

## Contact

For privacy questions, use the contact methods in [SUPPORT.md](SUPPORT.md). Do not include secrets or sensitive configuration details in a public issue.

## Open-source notice

WireRoute is free and open-source software provided under the [MIT License](COPYING). See [LEGAL.md](LEGAL.md) for attribution and trademark information.
