# WireRoute manager protocol v1

This document defines the proposed C# ↔ Go boundary for the native Windows client. The contract and C# client exist today; the Go manager does not yet serve this protocol.

## Security and process model

- Keep the manager-created anonymous pipes and inherited-handle trust model already used by WireGuard for Windows.
- Do not add a TCP listener, local HTTP server, globally named pipe, COM activation surface, or discoverable endpoint.
- Keep the WinUI process unprivileged. The manager service remains the only process that installs tunnel services, applies routes or DNS, touches protected configuration storage, or uses WireGuardNT.
- Use a distinct manager launch mode for this protocol. Do not sniff a stream to guess whether it contains Go `gob` or WireRoute JSON.
- Require `hello` as the first request and reject unsupported protocol ranges before any other method.
- Limit every frame to 1 MiB before allocating its payload.
- Never return interface private keys, preshared keys, hook command text, or decrypted configuration text in read responses or errors.
- Parse imported configurations again inside the manager with the authoritative Go parser before saving them. C# preview validation is not an authorization boundary.

## Transport

The manager owns three full-lifetime anonymous pipe channels:

| Channel | Direction | Payload |
| --- | --- | --- |
| Requests | WinUI → manager | `ManagerRequest` |
| Responses | Manager → WinUI | `ManagerResponse` |
| Events | Manager → WinUI | `ManagerEvent` |

Each message is one frame:

1. Four-byte unsigned little-endian JSON byte length.
2. Exactly that many UTF-8 JSON bytes.

Zero-length, truncated, malformed, and larger-than-1-MiB frames terminate the connection. There is no compression and no byte-order negotiation.

## Envelopes

Requests carry the selected protocol version, a monotonically increasing request ID, a method, and method parameters:

```json
{
  "version": 1,
  "requestId": 1,
  "method": "hello",
  "parameters": {
    "protocol": "wireroute-manager",
    "minimumVersion": 1,
    "maximumVersion": 1,
    "clientVersion": "0.1.0",
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
    "managerVersion": "0.1.0",
    "capabilities": {
      "canListProfiles": true,
      "canReadProfileDetails": true,
      "canReadTunnelState": true,
      "canImportProfiles": false,
      "canStartTunnels": false,
      "canStopTunnels": false,
      "canQuitManager": false
    }
  }
}
```

Events have a strictly increasing sequence number for the lifetime of one connection. A duplicate or decreasing sequence terminates the client connection rather than applying stale state.

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
| `manager.quit` | Whether to stop tunnels | Whether the manager had already begun quitting |

The manager advertises each privileged capability according to the connected session's token. `manager.quit` intentionally leaves active tunnels running when `stopTunnels` is `false`.

## Events

| Event | Payload |
| --- | --- |
| `profiles.changed` | Current profile-name set |
| `tunnel.stateChanged` | Profile name, state, and sanitized error code |
| `manager.stopping` | Non-sensitive reason |

## Error behavior

Errors use stable machine-readable codes and user-safe messages. Messages must not include configuration text, key material, hook commands, inherited handle values, protected paths, or raw driver structures. Unknown methods, invalid profile names, missing profiles, unsupported versions, denied operations, and internal failures remain distinct codes.

## Approved implementation sequence

The following steps require explicit approval because they change manager launch or service behavior:

1. Add a separate Go v1 framed-JSON server without changing existing `gob` behavior.
2. Add read-only handlers backed by `conf.ListConfigNames`, redacted `conf.LoadFromName`, and current tunnel state.
3. Teach the compatibility manager runtime to locate and launch `WireRoute.exe` with inherited v1 pipe handles.
4. Validate profile listing and state events on x64 while connect/import remain disabled.
5. Repeat the read-only validation in the native ARM64 VM.
6. Specify and threat-review import/start/stop requests before enabling mutations.
