# Network configuration behavior and quirks

WireRoute delegates adapter, route, firewall, and Profile DNS application to the inherited WireGuard for Windows tunnel engine. The WinUI application selects and rewrites profile policy, but it does not reimplement Windows networking in C#.

## Routing

The tunnel service deduplicates every peer's `AllowedIPs` and adds the resulting prefixes as routes on the WireGuard interface. If the profile does not specify an MTU, it observes the non-WireGuard interface carrying the default route and sets the WireGuard interface MTU to 80 bytes less.

WireGuardNT observes the routing table to choose an outgoing path that does not loop back into itself. It sends with `IP_PKTINFO` or `IPV6_PKTINFO`, remembers the incoming interface and source address, and replies through the same path.

WireRoute exposes two policy modes:

- **Full** uses the address-family default route (`0.0.0.0/0` or `::/0`) and is intended to carry all supported traffic.
- **Split** uses only the CIDR routes entered for that profile; other traffic follows the device's normal routing table.

Changing modes rewrites the stored configuration's allowed routes through the shared parser and formatter. Review multi-peer profiles carefully because `AllowedIPs` determine both route selection and the cryptographic peer used for a destination.

## Firewall behavior for default routes

When an interface has one peer and that peer contains a `/0` allowed prefix, the inherited tunnel service enables restrictive kill-switch behavior:

- packets from the tunnel service are permitted so WireGuard transport can flow;
- when DNS servers are configured, port 53 traffic is permitted only to those DNS servers;
- loopback and packets through the WireGuard tunnel are permitted;
- IPv4 and IPv6 DHCP and IPv6 neighbor discovery are permitted; and
- other traffic is blocked.

This prevents supported traffic from leaking outside the tunnel if the protected route fails.

A configuration can cover IPv4 without the exact `/0` trigger by using `0.0.0.0/1` plus `128.0.0.0/1`, and can cover IPv6 with `::/1` plus `8000::/1`. That avoids the inherited `/0` kill-switch condition and therefore has different leak behavior. Use it only when that tradeoff is intentional.

## Excluding private IPv4 destinations

For a compatible single-peer IPv4 full-tunnel profile, the editor exposes **Exclude private IPs**. WireRoute replaces `0.0.0.0/0` with an explicit set of non-private IPv4 prefixes, preserves IPv6 routes, and adds configured DNS server addresses to the allowed set so Profile DNS remains reachable.

This is not equivalent to the two-half default-route workaround. It deliberately keeps private and other excluded IPv4 ranges on the local network while routing public IPv4 through the VPN. Changing DNS while exclusion is enabled refreshes the DNS routes.

The control is hidden when the profile shape cannot be recognized safely, including incompatible multi-peer or custom-route configurations.

## Split routes and ordinary Windows behavior

Without the single-peer `/0` condition, Windows handles route selection and multihomed DNS in its ordinary way. Split mode is routing policy, not a general firewall or application allowlist. Traffic to destinations outside the configured prefixes can use another interface unless separate Windows Firewall or device-management policy prevents it.

The tunnel service still creates the rule required for its WireGuard UDP transport.

## DNS modes

### Profile DNS

Profile DNS uses the DNS server addresses in the WireGuard configuration. The tunnel service applies them to the WireGuard interface. Whether a DNS server is reachable through the tunnel depends on the profile's allowed routes; the private-address exclusion editor maintains explicit DNS routes when it owns that route pattern.

### Encrypted DNS

Encrypted DNS replaces the active runtime profile's DNS server with `127.0.0.1`. The signed-in WireRoute process binds UDP and TCP on loopback port 53 and forwards DNS wire messages to the selected HTTPS resolver using bootstrap IP addresses.

Consequences:

- only one WireRoute encrypted-DNS tunnel can be active;
- another local DNS proxy using `127.0.0.1:53` prevents activation;
- the tray process must remain running; and
- Persistent VPN cannot use encrypted DNS because the proxy is not available before sign-in or after sign-out.

WireRoute does not install a permanent system-wide DNS proxy.

## Network List Manager identity

Windows assigns a GUID to a WireGuard adapter. The inherited implementation derives it deterministically from tunnel configuration so firewall categorization can remain stable while the configuration is unchanged. A material configuration change can produce a different GUID and cause Windows or enterprise firewall policy to treat the adapter as a new network.

The upstream derivation is described in the [WireGuard mailing-list discussion](https://lists.zx2c4.com/pipermail/wireguard/2019-June/004259.html).

## Adapter and service lifetime

The adapter exists only while its per-tunnel service is running.

- Demand-start mode destroys the adapter and removes the service on disconnect.
- Persistent VPN configures the same per-tunnel engine for automatic startup and removes the WireRoute-owned protected service copy when persistence is disabled.

Additional filters, address families, or protocols should be deployed through separately reviewed administrative or NDIS policy. WireRoute blocks `PreUp`, `PostUp`, `PreDown`, and `PostDown` commands and does not support `DangerousScriptExecution` as a customization mechanism.

## Validation checklist

For any routing or DNS change, test:

1. IPv4 and IPv6 separately.
2. Full, Split, and private-address exclusion behavior.
3. DNS reachability and leak behavior.
4. Endpoint reachability after routes are applied.
5. Sleep, network changes, sign-out, restart, and reconnect.
6. Demand-start and Persistent VPN service removal.
7. Both x64 and ARM64 release builds.
