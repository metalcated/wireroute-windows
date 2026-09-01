# WireRoute manager protocol v1

## Status

The framed JSON protocol is implemented in both the Go manager (`manager/protocol_v1.go`) and the C# client (`WireRoute.Core.Manager`). It is retained only for compatibility with earlier development installations that manually enabled the inherited `WireGuardManager` service.

The standard WireRoute MSI does not install that manager. A normal launch of `WireRoute.exe` has no manager arguments, uses current-user DPAPI storage, and controls per-tunnel services through short-lived elevated operations. Do not use this protocol as the basis for new deployment automation.

When a legacy manager is already installed, it can launch `WireRoute.exe /manager-v1 <response> <request> <event>` with three inherited anonymous-pipe handles. That compatibility path changes storage, privilege, lifecycle, and attack-surface assumptions and is outside the supported product configuration.

## Security and process model

- The manager owns anonymous pipes and passes inheritable handles directly to the child UI process.
- There is no TCP listener, local HTTP server, globally named pipe, COM activation surface, or discoverable protocol endpoint.
- The WinUI child runs without the manager's elevated token. The Local System manager performs privileged profile and tunnel operations.
- Protocol v1 has a distinct `/manager-v1` launch mode; neither side guesses between JSON and the inherited Go `gob` protocol.
- `hello` must be the first request. Unsupported versions are rejected before any other method.
- Every frame is limited to 1 MiB before allocating its JSON payload.
- Read responses never return interface private keys, preshared keys, hook command text, or decrypted configuration text.
- Imports are parsed by the authoritative Go parser. Any `PreUp`, `PostUp`, `PreDown`, or `PostDown` hook causes the import to fail.
- Privileged methods are capability-advertised and checked against the manager session's elevated token.

The legacy manager stores profiles in the inherited system-level protected configuration store. Those profiles are distinct from the normal WireRoute files beneath `%LOCALAPPDATA%\WireRoute`.

## Transport

The manager owns three full-lifetime anonymous-pipe channels:

| Channel | Direction | Payload |
| --- | --- | --- |
| Requests | WinUI to manager | `ManagerRequest` |
| Responses | Manager to WinUI | `ManagerResponse` |
| Events | Manager to WinUI | `ManagerEvent` |

Each message is one frame:

1. Four-byte unsigned little-endian JSON byte length.
2. Exactly that many UTF-8 JSON bytes.

Zero-length, truncated, malformed, and larger-than-1-MiB frames terminate the connection. There is no compression or byte-order negotiation.

## Envelopes

Requests carry protocol version, a positive request ID, a method, and method parameters. The C# client allocates request IDs monotonically and requires each response to echo the outstanding ID:

```json
{
  "version": 1,
  "requestId": 1,
  "method": "hello",
  "parameters": {
    "protocol": "wireroute-manager",
    "minimumVersion": 1,
    "maximumVersion": 1,
    "clientVersion": "1.1.1",
    "architecture": "x64"
  }
}
```

Responses echo the request ID and contain exactly one of `result` or `error`:

```json
{
  "version": 1,
  "requestId": 1,
  "result": {
    "protocol": "wireroute-manager",
    "selectedVersion": 1,
    "managerVersion": "1.1.1",
    "capabilities": {
      "canListProfiles": true,
      "canReadProfileDetails": true,
      "canReadTunnelState": true,
      "canImportProfiles": true,
      "canStartTunnels": true,
      "canStopTunnels": true,
      "canQuitManager": true
    }
  }
}
```

The capability values are illustrative; clients must use the values returned for the current session.

Events have a strictly increasing sequence number for one connection. A duplicate or decreasing sequence terminates the client connection instead of applying stale state.

## Methods

| Method | Request | Result |
| --- | --- | --- |
| `hello` | Client version range and architecture | Selected version and capabilities |
| `profiles.list` | Empty object | Redacted profile summaries |
| `profiles.get` | Profile name | Redacted interface, peer, route, and DNS detail |
| `tunnel.state` | Profile name | Current tunnel state |
| `profiles.import` | Display name and wg-quick configuration | Imported profile summary |
| `tunnel.start` | Profile name | Updated tunnel state |
| `tunnel.stop` | Profile name | Updated tunnel state |
| `manager.quit` | Whether to stop tunnels | Whether quitting had already begun |

`manager.quit` intentionally permits active tunnels to remain running when `stopTunnels` is false.

## Events

| Event | Payload |
| --- | --- |
| `profiles.changed` | Current profile-name set |
| `tunnel.stateChanged` | Profile name, state, and sanitized error code |
| `manager.stopping` | Non-sensitive reason |

## Error behavior

Errors use stable machine-readable codes and user-safe messages. Messages must not include configuration text, key material, hook commands, inherited handle values, protected paths, or raw driver structures. Unknown methods, invalid profile names, missing profiles, unsupported versions, denied operations, invalid frames, and internal failures remain distinct conditions.

## Compatibility maintenance rules

1. Do not make the standard WireRoute installer depend on this protocol or install the manager service.
2. Do not add a discoverable IPC endpoint or weaken the inherited-handle trust boundary.
3. Keep C# and Go frame-limit, envelope, method, event, and redaction tests synchronized.
4. Treat any new privileged method as a security design change requiring explicit review.
5. Preserve hook rejection and authoritative Go parsing for imported configurations.
6. Validate compatibility changes on native x64 and ARM64.
7. If manager compatibility is retired, remove both implementations and their tests in one separately approved change, then remove this document.
