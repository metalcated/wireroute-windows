# WireRoute Windows architecture

## Reference points

- Windows foundation: WireGuard for Windows `4e6726c`, including WireGuardNT, the Go tunnel engine, per-tunnel services, routing, DNS integration, installer, updater, and diagnostics.
- Product parity reference: WireRoute Apple `efaba13` on `origin/feature/macos-sidebar-modernization`.
- UI and product rules come from the Apple implementation. Tunnel lifecycle and Windows security rules come from the existing Windows implementation.

## Current decisions

1. The existing Go tunnel engine and WireGuardNT integration remain authoritative. WireRoute does not reimplement adapter management, route application, or tunnel execution in C#.
2. `WireRoute.exe` is an unprivileged, unpackaged WinUI 3 process with an `asInvoker` manifest. Releases target native x64 and ARM64 only.
3. WireRoute does not install or require the automatic-start `WireGuardManager` service. Starting or stopping a tunnel produces a normal Windows elevation prompt.
4. While connected, one manual-start per-tunnel service runs under Local System so WireGuardNT can apply routes and DNS. Disconnect stops and deletes that service; no inactive WireRoute service remains installed.
5. Profiles, RouterOS credentials, certificate pins, recovery configurations, settings, and activity history are protected per user with Windows DPAPI.
6. Nordic Blue is the default appearance. The optional System appearance follows Windows light/dark changes. The notification icon is always present and supports the same WireRoute style choices as macOS.
7. Closing the window hides it to the notification area. Quit is explicit.

## Profile and tunnel boundary

- The WinUI app imports, validates, edits, creates, exports, and protects WireGuard configurations.
- Private and preshared keys stay inside protected profile data except for explicit copy, QR, or export actions.
- Activation writes a short-lived plaintext configuration under the current user's WireRoute runtime directory. The elevated backend consumes it, and the UI deletes it immediately after startup completes.
- `PreUp`, `PostUp`, `PreDown`, and `PostDown` commands are detected and blocked before activation in service-free mode.
- Split and Full modes rewrite the active profile's allowed routes through the shared parser and formatter.
- Profile DNS is applied by the WireGuard tunnel service.
- Encrypted DNS uses an in-process loopback proxy on `127.0.0.1:53`, forwards RFC 8484 messages over HTTPS/HTTP2 to the selected resolver, and makes no persistent system-wide DNS changes.

## RouterOS boundary

RouterOS behavior is ported from Apple commit `efaba13`; the audited flow is recorded in `docs/macos-routeros-parity.md`. `WireRoute.RouterOS` owns REST/TLS pinning, discovery, provisioning, local key generation, and recovery. The default peer list includes only peers whose comment is `Managed by WireRoute`; Show all peers reveals manual, server, and site-to-site peers.

## Compatibility code

The versioned inherited-pipe manager protocol remains in the tree for compatibility with earlier development builds, but the normal `WireRoute.exe` and native launcher paths do not install or connect to the persistent manager service.

## Release gates

1. Run all managed and focused Go tests.
2. Build the native backend and WinUI app for x64 and ARM64.
3. Validate profile import, RouterOS discovery/provisioning, connect/disconnect, routes, DNS, tray behavior, and responsive modals on the Windows 11 x64 laptop.
4. Repeat architecture and tunnel validation in the native Windows ARM64 VM.
5. Sign and validate the installer and final artifacts on the x64 release machine.
