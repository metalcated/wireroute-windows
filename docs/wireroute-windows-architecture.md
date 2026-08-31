# WireRoute Windows architecture

## Reference points

- Windows foundation: WireGuard for Windows `4e6726c`, including its manager service, per-tunnel services, WireGuardNT integration, DPAPI-backed configuration store, installer, updater, and diagnostics.
- Product parity reference: WireRoute Apple `efaba13` on `origin/feature/macos-sidebar-modernization`.
- UI and product rules come from the Apple implementation. Tunnel lifecycle and Windows security rules come from the existing Windows implementation.

## Initial decisions

1. The existing Go tunnel engine is preserved. WireRoute will not reimplement WireGuardNT adapter management, tunnel service installation, route application, or the encrypted configuration store in C#.
2. `WireRoute.App` is an unprivileged WinUI 3 process with an `asInvoker` manifest. It targets native x64 and ARM64 only.
3. During the parity milestone, the privileged boundary remains the upstream automatic-start `WireGuardManager` service. This is an intentional Windows-specific exception to the macOS lifecycle and will be reassessed after functional parity; the WinUI process remains unprivileged.
4. The manager exposes a versioned, bounded JSON protocol over inherited anonymous pipes while preserving the existing manager-created trust boundary. The current Go `gob` protocol is not reimplemented in C#.
5. The WinUI project is initially unpackaged. This allows the existing service and installer model to launch a normal executable and pass explicit handles. Release packaging remains an installer milestone.
6. Nordic Blue is the default visual system. System appearance remains required, but it is not enabled until every control has a complete system-theme resource path.
7. Profile secrets remain in the existing DPAPI-protected store. The UI receives redacted profile details; private configuration material crosses the boundary only during explicitly requested import and provisioning workflows.

## Current import boundary

- The WinUI app can open one or more user-selected `.conf` files and validate them with the shared C# parser.
- Parsed private and preshared keys remain internal to `WireRoute.Core`; the UI receives only presence flags and non-secret network metadata.
- Imported profiles are session-only previews. They are not written to disk, registered with the manager, or made connectable.
- Configurations containing `PreUp`, `PostUp`, `PreDown`, or `PostDown` are detected and visibly flagged. Preview never executes hooks.
- The manager service, inherited handles, Go `gob` IPC, tunnel services, routes, DNS, registry, and installer remain unchanged by this slice.

## Reuse map

| WireRoute capability | Existing Windows foundation | Planned Windows surface |
| --- | --- | --- |
| Profile parsing and protected storage | `conf` and `conf/dpapi` | Profile list, import, edit, export |
| Tunnel lifecycle | `manager`, `tunnel`, `driver` | Activate/deactivate and live status |
| Runtime counters and handshake | Manager runtime configuration | Activity dashboard and history |
| Routes and DNS | Tunnel service and `winipcfg` | Split/Full and DNS Protection policy |
| Logs | `ringlogger` | Diagnostics and export |
| Installation and signing | Existing WiX installer and build scripts | x64/ARM64 WireRoute releases |

## Milestone gates

1. Build and visually compare the Nordic Blue shell.
2. Port and test profile-independent routing and DNS policy.
3. Define and threat-model the UI/manager protocol before changing either process.
4. Import and display real profiles without exposing private keys.
5. Validate a real x64 tunnel before extending RouterOS or secondary settings.
6. Validate the same tunnel path in a native ARM64 VM without emulation.

The versioned protocol contract and C# framed-stream client are defined in `docs/manager-protocol-v1.md`. No manager implementation or launch behavior has been changed yet.

RouterOS behavior is ported from Apple commit `efaba13`; the audited flow and required Windows-only substitutions are recorded in `docs/macos-routeros-parity.md`. `WireRoute.RouterOS` owns the platform-neutral REST, TLS-pin, discovery, provisioning, local key-generation, and recovery rules. The WinUI app will own protected per-user connection settings, while the privileged manager remains the only component allowed to persist or activate tunnel configurations.
