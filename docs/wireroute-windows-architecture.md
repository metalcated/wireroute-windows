# WireRoute Windows architecture

## Reference points

- Windows foundation: WireGuard for Windows commit `4e6726c`, including WireGuardNT, the Go tunnel engine, per-tunnel services, routing, DNS integration, native diagnostics, and installer foundations.
- Product parity baseline: WireRoute Apple commit `efaba13`, recorded in [macOS RouterOS parity baseline](macos-routeros-parity.md).
- UI and product workflows follow the released Apple application where platform-neutral. Tunnel execution and Windows security boundaries follow the inherited Windows engine.

The inherited manager service, legacy UI, and updater remain compatibility/source heritage. They are not installed or invoked by the standard WireRoute product path.

## Supported runtime topology

```text
signed-in user
  WireRoute.exe (WinUI, asInvoker)
    DPAPI-protected per-user stores
    RouterOS HTTPS client
    optional loopback encrypted-DNS proxy
    activity and tray lifecycle
          |
          | UAC for start/stop only
          v
  wireguard.exe elevated command
          |
          v
  WireGuardTunnel$<name> (Local System)
          |
          v
  WireGuardNT (kernel NDIS driver)
```

There is no always-running global manager in the default topology.

## Current decisions

1. The Go tunnel engine and WireGuardNT integration remain authoritative. WireRoute does not reimplement adapter management, route application, DNS application, or tunnel execution in C#.
2. `WireRoute.exe` is an unprivileged, unpackaged WinUI 3 process with an `asInvoker` manifest. Releases target native x64 and ARM64; x86 is not a WireRoute release target.
3. The standard MSI does not install or require `WireGuardManager`. Starting or stopping a tunnel produces a normal Windows elevation prompt for that operation.
4. Default activation uses one demand-start per-tunnel Local System service while connected and removes it on disconnect.
5. Persistent VPN is opt-in. It stores only WireRoute-marked protected configurations and changes active per-tunnel services to automatic startup so they can survive sign-out and restart. Disabling the option removes those marked service copies without deleting current-user profiles.
6. Profiles, RouterOS credentials, certificate pins, uncertain-write recovery records, settings, activity events, and connection sessions use current-user Windows DPAPI beneath `%LOCALAPPDATA%\WireRoute`.
7. Blue Nordic is the default appearance. System follows Windows light/dark state. Tray icon styles mirror the macOS choices using Windows notification-area assets.
8. Closing the window hides it to the notification area. Quit is explicit.

## Profile and tunnel boundary

- The WinUI app imports, validates, edits, creates, exports, and protects WireGuard configurations.
- Friendly display names are separate from stable WireGuardNT tunnel identifiers, so names such as `iPhone 12 Dev` remain visible without violating the 32-character Windows service-name constraint.
- Private and preshared keys stay in protected profile data except during required parsing/runtime use or an explicit copy, QR, or export action.
- Activation writes a short-lived plaintext configuration beneath `%LOCALAPPDATA%\WireRoute\Runtime\<profile-id>`. The elevated backend consumes it and the UI deletes it after the start request completes.
- The service publishes `tunnel.log` and `tunnel.metrics` in that runtime directory. The UI reads the newest 2 MiB of log data and one versioned metrics snapshot.
- `PreUp`, `PostUp`, `PreDown`, and `PostDown` commands are detected and blocked before every supported local activation.
- Split and Full modes rewrite allowed routes through the shared parser and formatter.
- Single-peer private-IP exclusion uses the audited non-private IPv4 route set and refreshes DNS routes when needed.
- Profile DNS is applied by the tunnel service.
- Encrypted DNS uses an in-process UDP/TCP proxy bound exclusively to `127.0.0.1:53` and forwards RFC 8484 wire messages over HTTPS. It makes no persistent system-wide DNS-proxy installation.
- Persistent VPN requires Profile DNS because encrypted DNS depends on the signed-in tray process.

## Local storage boundary

The current-user stores are versioned JSON documents protected with DPAPI and WireRoute-specific optional entropy:

```text
%LOCALAPPDATA%\WireRoute\
  wireguard-profiles.dpapi
  routeros-connections.dpapi
  routeros-certificates.dpapi
  routeros-profile-recovery.dpapi
  settings.dpapi
  activity.dpapi
  activity-sessions.dpapi
  Runtime\
```

Writes use a new temporary file followed by replacement of the prior file. DPAPI protects data at rest from other users and offline inspection; it is not a defense against code already executing as the signed-in user.

Connection history is local, limited to the newest 1000 sessions, and purged according to the selected 1-, 7-, or 30-day retention. General activity events are separately limited to 1000 entries.

## RouterOS boundary

`WireRoute.RouterOS` owns REST transport, TLS validation and pinning, discovery, provisioning, local key generation, and uncertain-write recovery.

- TLS 1.2 and TLS 1.3 are allowed.
- Redirects are disabled and the request timeout is 15 seconds.
- Untrusted or changed certificates require an app-owned fingerprint review.
- X25519 keys are generated locally through Windows cryptography APIs.
- The private key is never sent to RouterOS.
- Peer creation changes only one `/interface/wireguard/peers` resource after explicit review.
- The default list includes only peers whose comment is `Managed by WireRoute`; `Show all peers` reveals other peers.

See [macOS RouterOS parity baseline](macos-routeros-parity.md) and [RouterOS setup](../ROUTEROS_SETUP.md).

## Activity boundary

The per-tunnel service writes a versioned metrics snapshot containing received bytes, sent bytes, and the newest handshake time. The application samples it once per second, falls back to direct WireGuardNT metrics when permitted, calculates rates, and records local connection sessions.

Activity history is not telemetry. WireRoute does not transmit it to the project or a third-party analytics service.

## Compatibility manager

The implemented `wireroute-manager` v1 inherited-pipe protocol remains for earlier development installations. If an inherited manager launches WireRoute with `/manager-v1` and three handles, the app uses the legacy system-profile boundary for that session.

The standard MSI does not install the manager and a normal launch does not connect to it. New product features must target the per-user, per-tunnel architecture rather than expanding manager compatibility. See [Manager protocol v1](manager-protocol-v1.md).

## Build and distribution

- The WinUI client targets .NET 10 and Windows App SDK 2.4.
- Native releases are produced independently for x64 and ARM64.
- The release script builds both backends and self-contained app directories, creates per-machine MSIs and portable ZIPs, and writes a SHA-256 manifest.
- Production artifacts require the signing and publication controls in [Code-signing policy](../CODE_SIGNING_POLICY.md).

## Release gates

1. Run all managed tests and focused Go tunnel, manager, and driver tests.
2. Build complete native backend and WinUI outputs for x64 and ARM64.
3. Validate profile import/edit/export, RouterOS discovery and provisioning, connect/disconnect, Full/Split routes, both DNS modes, tray behavior, responsive modals, activity metrics, and history on Windows 11 x64.
4. Validate Persistent VPN enable, reboot/sign-out survival, disable, and service cleanup.
5. Repeat architecture, tunnel, and installer validation in a native Windows ARM64 VM.
6. Sign and timestamp final artifacts, verify signatures after packaging, and compare published SHA-256 values.
7. Test same-version upgrade, newer-version upgrade, uninstall, and per-user data preservation.
