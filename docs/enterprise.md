# Enterprise deployment

WireRoute is packaged as a standard per-machine MSI for Windows 11 x64 and ARM64. The supported product path is the unprivileged WinUI application plus demand-start per-tunnel WireGuard services. The inherited automatic `WireGuardManager` service is not part of the standard deployment.

## Installation

Choose the MSI that matches the device architecture:

```text
WireRoute-x64-<version>.msi
WireRoute-ARM64-<version>.msi
```

A quiet installation can use standard Windows Installer options:

```powershell
msiexec.exe /i WireRoute-x64-1.0.0.msi /qn /norestart
```

A quiet uninstall can use the deployed MSI or its product code:

```powershell
msiexec.exe /x WireRoute-x64-1.0.0.msi /qn /norestart
```

The package installs under Program Files and creates a Start menu shortcut. It does not automatically launch WireRoute and currently defines no product-specific MSI policy properties. Same-version upgrades are allowed; downgrades are blocked. x64 and ARM64 packages have separate upgrade identities, so deploy only the native package for each device.

Verify the Authenticode signature and published SHA-256 value before enterprise deployment. See [Downloads](DOWNLOADS.md) and [Code-signing policy](CODE_SIGNING_POLICY.md).

## User data and uninstall behavior

Each signed-in user has independent state under:

```text
%LOCALAPPDATA%\WireRoute
```

Profiles, settings, RouterOS connections and passwords, certificate pins, recovery records, activity events, and connection sessions are protected with current-user Windows DPAPI. The MSI uninstall removes installed program files and shortcuts; it does not delete per-user WireRoute data.

This is intentional data-preservation behavior. If organizational policy requires profile removal, perform it as a separate, explicitly reviewed user-data operation after confirming that no recovery material is needed.

## Tunnel lifecycle

### Default mode

WireRoute runs unprivileged. Activating or deactivating a profile invokes the bundled backend through a normal UAC prompt for that operation.

While connected, the profile uses a Local System service named:

```text
WireGuardTunnel$<tunnel-name>
```

The service is demand-start in the default mode and is removed on disconnect. WireRoute does not keep an automatic manager service running.

### Persistent VPN

A user can enable Persistent VPN in Settings. Enabling it does not immediately install a global service when no tunnel is active. The next activated profile receives a WireRoute-marked, automatic per-tunnel service so it can remain connected across sign-out and restart. Active demand-start tunnels are replaced after confirmation.

Disabling Persistent VPN disconnects and removes WireRoute-owned persistent service copies. The user's DPAPI-protected local profiles remain available and future activations return to demand-start operation.

Persistent VPN requires Profile DNS. WireRoute's encrypted DNS mode depends on an in-process loopback proxy in the signed-in tray application and therefore cannot provide pre-logon or post-sign-out name resolution.

### On-Demand

Ethernet and Wi-Fi On-Demand selections are evaluated by the signed-in WireRoute application. They are convenient user-session automation, not a pre-logon machine policy and not a replacement for Persistent VPN.

## Administrative boundaries

- Users require the ability to approve elevation, or an administrator must provide an approved elevation workflow, to start and stop tunnels.
- RouterOS credentials should belong to a dedicated least-privilege account where practical.
- WireRoute does not currently publish ADMX templates, registry policies, machine-wide profile provisioning, or a supported command-line profile-management API.
- The `HKLM\Software\WireGuard\LimitedOperatorUI` and `DangerousScriptExecution` values are inherited WireGuard behaviors and are not supported WireRoute policies. See [Administrative registry compatibility](adminregistry.md).
- Do not install the inherited `WireGuardManager` service as a deployment shortcut. Its compatibility protocol and additional attack surface are documented in [Manager protocol v1](manager-protocol-v1.md) and [Attack surface](attacksurface.md).

## Diagnostics and activity

The application reads per-tunnel runtime logs and metrics from:

```text
%LOCALAPPDATA%\WireRoute\Runtime\<profile-id>\
```

The UI exposes the tunnel log, live transfer rates, latest handshake, session totals, and locally protected connection history. Connection-session retention is selectable as 1, 7, or 30 days. Activity is device-local; WireRoute does not upload telemetry or provide a central log collector.

Runtime log reading is bounded to the newest 2 MiB. If enterprise collection is required, collect only reviewed diagnostics and treat endpoints, addresses, profile names, and timing as potentially sensitive network metadata.

## Updates

WireRoute currently has no supported in-app updater or scheduled update command. Publish a new signed MSI through the organization's normal software-distribution system and validate it before rollout.

The inherited `/update`, `/installmanagerservice`, `/dumplog`, and related upstream WireGuard commands are not WireRoute enterprise interfaces. Their presence in the native backend does not make them supported deployment contracts.

## Network policy considerations

Full tunnel, Split tunnel, Profile DNS, encrypted DNS, private-address exclusion, and the WireGuard kill-switch behavior can materially affect routing and name resolution. Review [Network configuration quirks](netquirk.md) before standardizing profiles.

Test signed x64 and ARM64 artifacts on native hardware or VMs, including connect/disconnect, reboot behavior for Persistent VPN, DNS, routes, tray behavior, activity metrics, and uninstall/upgrade handling.
