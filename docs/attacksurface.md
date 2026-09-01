# WireRoute attack surface

This document describes the supported WireRoute deployment path. WireRoute inherits the WireGuard for Windows tunnel engine and some compatibility code, but it does not normally install the upstream automatic manager service or use the inherited in-app updater.

## Normal process and privilege model

### WireRoute WinUI application

`WireRoute.exe` is an unpackaged WinUI 3 application with an `asInvoker` manifest. It normally runs as the signed-in user and owns profile editing, RouterOS workflows, the notification-area UI, activity history, and the optional encrypted DNS proxy.

The application has access to all data available to that user. DPAPI protects stored data from offline disclosure and other Windows users, but it does not protect against malware already running as the same user.

### Elevated tunnel operation

Starting or stopping a tunnel launches the bundled native `wireguard.exe` backend with the Windows `runas` verb. Elevation is limited to that operation; WireRoute itself does not relaunch at high integrity.

For activation, the application writes a short-lived wg-quick configuration under:

```text
%LOCALAPPDATA%\WireRoute\Runtime\<profile-id>\
```

The elevated backend consumes that file. WireRoute deletes the plaintext configuration in a `finally` path after the start request completes. A crash, forced termination, or hostile same-user process can still expose the file during that window, so runtime-directory cleanup and permissions remain security-sensitive.

### Per-tunnel service

Each active profile uses a service named `WireGuardTunnel$<tunnel-name>` running as Local System.

- In the default mode, WireRoute creates a demand-start service and removes it when the tunnel disconnects.
- With Persistent VPN enabled, WireRoute stores a WireRoute-marked protected copy and configures that profile's tunnel service for automatic startup. Disabling Persistent VPN removes those marked service copies without deleting the user's local profiles.
- The service writes a diagnostic log and a small metrics snapshot to the profile runtime directory. The UI bounds each log read to the newest 2 MiB.

The tunnel service creates and configures the WireGuardNT adapter, applies routes and DNS, and retains the privileges required by the inherited WireGuard service implementation. A flaw in its configuration parsing, adapter configuration, service-control handling, or logging crosses from user-supplied profile data into Local System.

### WireGuardNT

WireGuardNT is a kernel NDIS miniport driver. Its reachable surface includes:

- IP traffic entering and leaving the adapter;
- UDP WireGuard packet parsing;
- NDIS OID, plug-and-play, and close callbacks; and
- restricted IOCTLs used to configure adapters, change state, and read the driver ring log.

The inherited device security descriptor restricts those IOCTLs to Local System and Administrators at high integrity. Kernel-driver and tunnel-engine updates must continue to be reviewed and rebased from the upstream WireGuard for Windows foundation.

## Protected local data

WireRoute stores profiles, RouterOS connections and passwords, RouterOS certificate pins, uncertain-write recovery records, settings, activity events, and connection sessions beneath `%LOCALAPPDATA%\WireRoute`.

Each structured file is serialized, protected with current-user Windows DPAPI plus WireRoute-specific optional entropy, and written through a temporary file before replacement. Plaintext serialization buffers are zeroed after use where the implementation controls them.

Sensitive values can intentionally leave this boundary when the user copies a private key, renders a configuration QR code, or exports a profile. Clipboard, screen-capture, and exported-file handling then become the user's responsibility.

## RouterOS HTTPS boundary

RouterOS management is performed from the unprivileged application over REST:

- automatic redirects are disabled;
- TLS 1.2 and TLS 1.3 are enabled;
- requests time out after 15 seconds;
- certificates trusted by Windows use normal platform validation; and
- untrusted or changed certificates require an app-owned fingerprint review and are pinned to the exact host and port after approval.

A timeout or transport failure after a peer-creation request is treated as an uncertain write. WireRoute protects the matching private configuration locally so the user can reconnect and reconcile state without generating an unrelated key.

RouterOS credentials grant whatever rights are assigned to that RouterOS account. Use a dedicated least-privilege account where practical and treat an approved certificate replacement as a security-sensitive action.

## Encrypted DNS boundary

Encrypted DNS runs inside the signed-in WireRoute process. It binds UDP and TCP exclusively to `127.0.0.1:53`, accepts at most 32 concurrent queries, and forwards DNS wire messages to the configured HTTPS resolver with a 12-second HTTP timeout. Bootstrap addresses avoid resolving the resolver hostname through the proxy itself.

Only one WireRoute encrypted-DNS tunnel can own the loopback listener. Another local DNS proxy can prevent activation. Persistent VPN requires Profile DNS because this in-process proxy is unavailable before sign-in or after the tray process exits.

## Configuration input

Imported and edited profiles are parsed in managed code and again by the authoritative Go tunnel engine when activated. Profile names are separated from constrained Windows service identifiers. WireRoute detects all four wg-quick hook fields and refuses to activate profiles that contain them; the inherited `DangerousScriptExecution` registry switch is not a supported WireRoute feature.

Split/Full rewrites, private-address exclusion, DNS changes, and RouterOS-generated profiles all pass through the shared parser and formatter. Parser and formatter changes therefore affect import, editing, activation, QR/export, and RouterOS provisioning and should be tested together.

## Compatibility code outside the normal path

The repository still contains the inherited manager service, legacy UI, updater, and the implemented `wireroute-manager` v1 pipe protocol. They exist for upstream heritage and compatibility with earlier development installations. The WireRoute MSI does not install the manager, and a normal `WireRoute.exe` launch does not connect to it.

Manually enabling the legacy manager adds a long-running Local System service, session enumeration, inherited-pipe IPC, system-level protected configuration storage, and inherited update code to the attack surface. That deployment is not supported by the current product documentation.

## Release and distribution boundary

Release artifacts include executable code, native dependencies, the WireGuardNT resource, and an MSI. Production artifacts should be Authenticode-signed and timestamped, hashes must be published, and the signing identity must be isolated from ordinary development credentials. See [Code-signing policy](../CODE_SIGNING_POLICY.md).

## Security review priorities

1. Keep the WireGuard for Windows engine and WireGuardNT foundation current.
2. Minimize the lifetime and accessibility of plaintext runtime configurations.
3. Preserve hook blocking on every supported activation path.
4. Verify persistent-service ownership markers before replacement or deletion.
5. Treat RouterOS certificate changes and uncertain writes as explicit review states.
6. Fuzz or adversarially test configuration, manager-frame, REST, DNS, and metrics parsers.
7. Remove dormant manager/updater compatibility code if compatibility is formally retired.
