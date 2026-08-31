# macOS RouterOS parity baseline

The Windows RouterOS workflow uses the released macOS implementation at Apple commit `efaba13` as its behavioral source of truth. Platform substitutions are limited to the operating-system boundaries described below.

## Released workflow to preserve

1. Settings owns multiple named RouterOS connections. Each connection contains a stable identifier, display name, HTTPS URL, username, protected password, and optional default WireGuard interface.
2. RouterOS Peer Manager selects one saved connection. Connect performs parallel, read-only discovery of WireGuard interfaces, WireGuard peers, and IP addresses.
3. A valid public certificate uses normal platform trust. An untrusted or changed certificate opens an in-app fingerprint review before trust is stored for that exact host and port.
4. Successful discovery changes the Connect button to `Connected`. The default view includes only peers whose comment is `Managed by WireRoute`; `Show all peers` includes the rest.
5. `Set Up Peer…` generates a fresh X25519 key pair locally before review. The private key is never sent to RouterOS.
6. The setup form preselects the saved default interface when available, otherwise an active interface. Endpoint, DNS, Split routes, and keepalive use saved peer defaults but remain editable.
7. Client addresses are suggested only when one unambiguous IPv4 /24 pool can be inferred from existing /32 peer addresses on the selected interface.
8. `Review RouterOS Change` shows the device, interface, client address, endpoint, and client routes. Confirmation adds exactly one peer and does not change RouterOS addresses, firewall, NAT, or routes.
9. Only after RouterOS confirms the write does WireRoute import the matching private client configuration into its tunnel store, refresh the profile list, and select the new profile.
10. A rejected 4xx write remains a normal failure. A timeout, 408, server failure, or transport interruption after submission is treated as an uncertain write; the matching private configuration must remain available for recovery while the user reconnects and verifies RouterOS.

## Windows-native substitutions

| macOS boundary | Windows counterpart |
| --- | --- |
| Keychain connection and certificate storage | Current-user DPAPI-protected storage, with no plaintext password or certificate-pin file |
| NetworkExtension tunnel preferences | Privileged manager configuration store encrypted with the inherited WireGuard for Windows DPAPI format |
| NetworkExtension activation | Existing WireGuard for Windows tunnel service and WireGuardNT lifecycle |
| AppKit sheets and alerts | WinUI `ContentDialog` modals |
| `PrivateKey()` generation | Windows CNG X25519 generation in the unprivileged client process |

WireGuard for Windows tunnel service identifiers are limited to 1–32 ASCII letters, numbers, and `_ = + . -` characters. macOS display names do not have this restriction. Windows therefore keeps the user-entered device name as profile metadata and derives a separate collision-checked service identifier at the manager boundary. The UI must not silently replace the displayed device name.

The RouterOS REST paths, payload field names, TLS pin semantics, 15-second request timeout, no-redirect behavior, validation rules, and recovery ordering remain equivalent to macOS.
