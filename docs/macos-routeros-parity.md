# macOS RouterOS parity baseline

The Windows RouterOS workflow uses the released macOS implementation at Apple commit `efaba13` as its audited behavioral baseline. Platform substitutions are limited to operating-system boundaries and are recorded here so future changes can be reviewed deliberately.

## Workflow that Windows preserves

1. Settings owns multiple named RouterOS connections. Each connection contains a stable identifier, display name, HTTPS URL, username, protected password, and optional default WireGuard interface.
2. RouterOS Peer Manager selects one saved connection. Connect performs parallel, read-only discovery of WireGuard interfaces, WireGuard peers, and IP addresses.
3. A valid public certificate uses normal platform trust. An untrusted or changed certificate opens an app-owned fingerprint review before trust is stored for that exact host and port.
4. Successful discovery changes the Connect button to `Connected`. The default view includes only peers whose comment is exactly `Managed by WireRoute`; `Show all peers` includes the rest.
5. `Set Up Peer…` generates a fresh X25519 key pair locally before review. The private key is never sent to RouterOS.
6. The setup form preselects the saved default interface when available, otherwise an active interface. Endpoint, DNS, Split routes, and keepalive use saved peer defaults but remain editable.
7. Client addresses are suggested only when one unambiguous IPv4 `/24` pool can be inferred from existing `/32` peer addresses on the selected interface.
8. `Review RouterOS Change` shows the device, interface, client address, endpoint, and client routes. Confirmation adds exactly one peer and does not change RouterOS addresses, firewall, NAT, or routes.
9. Only after RouterOS confirms the write does WireRoute import the matching private client configuration into its protected tunnel store, refresh the profile list, and select the new profile.
10. A rejected 4xx write remains a normal failure. A timeout, HTTP 408, server failure, or transport interruption after submission is treated as an uncertain write. The matching private configuration remains protected for recovery while the user reconnects and verifies RouterOS.

## Windows-native substitutions

| macOS boundary | Windows counterpart |
| --- | --- |
| Keychain connection and certificate storage | Current-user DPAPI-protected files under `%LOCALAPPDATA%\WireRoute`; no plaintext password or certificate-pin file |
| NetworkExtension tunnel preferences | Current-user DPAPI-protected WireRoute profile store |
| NetworkExtension activation | Demand-start per-tunnel WireGuard service and WireGuardNT; optional Persistent VPN changes only the active per-tunnel service to automatic startup, never the manager service |
| AppKit sheets and alerts | Responsive app-owned WinUI modal host |
| `PrivateKey()` generation | Windows CNG X25519 generation in the unprivileged client process |

WireGuard for Windows tunnel identifiers are limited to 1-32 ASCII letters, numbers, and `_ = + . -` characters. macOS display names do not have this restriction. Windows keeps the user-entered device name as profile metadata and derives a separate collision-checked, stable tunnel identifier before RouterOS review. The UI never silently replaces the displayed device name.

## Transport and recovery invariants

The Windows implementation keeps the following audited behavior:

- RouterOS REST field names and endpoint paths match the released workflow.
- Requests use TLS 1.2 or TLS 1.3, disable automatic redirects, and use a 15-second timeout.
- Windows certificate trust is accepted normally; manually approved certificates are pinned by DER value to an exact host and port.
- A changed pin requires a new review showing the previous and presented SHA-256 fingerprints.
- The peer private key exists only in the client and protected recovery/profile storage.
- Recovery is saved before an uncertain result is exposed and is removed only after the result is reconciled or the user explicitly discards it.

Changes to RouterOS request ordering, certificate validation, client-address inference, key handling, or uncertain-write recovery should be compared with the current Apple release and covered by both RouterOS and Storage tests.
