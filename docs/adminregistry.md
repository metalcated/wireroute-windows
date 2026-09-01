# Administrative registry compatibility

WireRoute does not currently expose supported registry-based policy settings. The normal application is an unprivileged, per-user WinUI process, and the standard installer does not install the inherited `WireGuardManager` service.

This page records inherited WireGuard registry behavior so administrators do not mistake it for a supported WireRoute configuration surface.

## WireRoute installer bookkeeping

The MSI creates `HKCU\Software\WireRoute\Installed` as a Windows Installer component key path for the Start menu shortcut. It is not a policy switch and should not be edited to configure the application.

WireRoute does not claim ownership of the entire `HKLM\Software\WireGuard` key, and its installer does not promise to remove settings created by WireGuard for Windows or another product.

## Inherited keys that WireRoute does not support

### `HKLM\Software\WireGuard\LimitedOperatorUI`

Upstream WireGuard for Windows reads this value only from its automatic manager-service workflow. The standard WireRoute installer and normal WireRoute launch path do not install or connect to that manager, so setting this value has no effect on the supported WireRoute experience.

The inherited manager compatibility code remains in the native backend for earlier development deployments. Manually installing that manager expands the privileged attack surface and is not a supported way to deploy WireRoute. See [Enterprise deployment](enterprise.md) and [Manager protocol v1](manager-protocol-v1.md).

### `HKLM\Software\WireGuard\DangerousScriptExecution`

Upstream WireGuard tunnel services can use this value to permit `PreUp`, `PostUp`, `PreDown`, and `PostDown` commands as Local System. WireRoute intentionally detects those hooks and blocks activation before invoking either its demand-start or Persistent VPN tunnel path.

Setting this registry value is therefore not a supported bypass. Remove hook commands from profiles used by WireRoute and perform required machine configuration through separately reviewed administrative tooling or device-management policy.

## Future policy support

If WireRoute adds enterprise policy settings, they should use WireRoute-owned names, document their scope and precedence, and include deployment and removal behavior here. Until then, Settings in the application is the supported configuration surface.
