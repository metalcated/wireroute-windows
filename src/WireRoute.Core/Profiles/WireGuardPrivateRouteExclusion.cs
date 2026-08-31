using System.Net;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Profiles;

public sealed record WireGuardPrivateRouteExclusionState(bool IsAvailable, bool IsEnabled);

public static class WireGuardPrivateRouteExclusion
{
    private static readonly IReadOnlyList<RoutePrefix> NonPrivateIpv4Routes = Array.AsReadOnly(
        new[]
        {
            "1.0.0.0/8", "2.0.0.0/8", "3.0.0.0/8", "4.0.0.0/6", "8.0.0.0/7", "11.0.0.0/8",
            "12.0.0.0/6", "16.0.0.0/4", "32.0.0.0/3", "64.0.0.0/2", "128.0.0.0/3",
            "160.0.0.0/5", "168.0.0.0/6", "172.0.0.0/12", "172.32.0.0/11", "172.64.0.0/10",
            "172.128.0.0/9", "173.0.0.0/8", "174.0.0.0/7", "176.0.0.0/4", "192.0.0.0/9",
            "192.128.0.0/11", "192.160.0.0/13", "192.169.0.0/16", "192.170.0.0/15",
            "192.172.0.0/14", "192.176.0.0/12", "192.192.0.0/10", "193.0.0.0/8",
            "194.0.0.0/7", "196.0.0.0/6", "200.0.0.0/5", "208.0.0.0/4",
        }.Select(notation => new RoutePrefix(notation)).ToArray());
    private static readonly RoutePrefix Ipv4DefaultRoute = new("0.0.0.0/0");

    public static WireGuardPrivateRouteExclusionState Evaluate(WireGuardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Peers.Count != 1)
        {
            return new(false, false);
        }

        var allowed = Canonicalize(profile.Peers[0].AllowedIps).ToHashSet();
        if (allowed.Contains(Ipv4DefaultRoute))
        {
            return new(true, false);
        }

        return NonPrivateIpv4Routes.All(allowed.Contains)
            ? new(true, true)
            : new(false, false);
    }

    public static string SetEnabled(
        WireGuardProfile profile,
        bool isEnabled,
        IReadOnlyList<string>? oldDnsServers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Peers.Count != 1)
        {
            throw new ArgumentException("Private IP exclusion requires exactly one WireGuard peer.", nameof(profile));
        }

        var currentDns = NormalizeDnsServers(profile.Interface.DnsServers);
        var previousDns = oldDnsServers is null
            ? currentDns
            : NormalizeDnsServers(oldDnsServers);
        var ipv6Routes = Canonicalize(profile.Peers[0].AllowedIps)
            .Where(route => route.Family == IpFamily.Ipv6);
        IReadOnlyList<RoutePrefix> allowedIps;
        if (isEnabled)
        {
            allowedIps = ipv6Routes
                .Concat(NonPrivateIpv4Routes)
                .Concat(currentDns)
                .ToArray();
        }
        else
        {
            var previousDnsSet = previousDns.ToHashSet();
            allowedIps = ipv6Routes
                .Where(route => !previousDnsSet.Contains(route))
                .Append(Ipv4DefaultRoute)
                .ToArray();
        }

        return WireGuardConfigFormatter.ToWgQuick(profile, allowedIps);
    }

    public static string RefreshDnsRoutes(
        WireGuardProfile profile,
        IReadOnlyList<string> oldDnsServers)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(oldDnsServers);
        if (profile.Peers.Count != 1)
        {
            throw new ArgumentException("Private IP exclusion requires exactly one WireGuard peer.", nameof(profile));
        }

        var oldDns = NormalizeDnsServers(oldDnsServers).ToHashSet();
        var allowedIps = Canonicalize(profile.Peers[0].AllowedIps)
            .Where(route => !oldDns.Contains(route))
            .Concat(NormalizeDnsServers(profile.Interface.DnsServers))
            .ToArray();
        return WireGuardConfigFormatter.ToWgQuick(profile, allowedIps);
    }

    private static IReadOnlyList<RoutePrefix> NormalizeDnsServers(IEnumerable<string> dnsServers)
    {
        var result = new List<RoutePrefix>();
        foreach (var value in dnsServers)
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                continue;
            }

            var prefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? 32
                : 128;
            result.Add(new RoutePrefix($"{address}/{prefixLength}"));
        }

        return result;
    }

    private static IEnumerable<RoutePrefix> Canonicalize(IEnumerable<RoutePrefix> routes) =>
        routes.Select(Canonicalize);

    private static RoutePrefix Canonicalize(RoutePrefix route)
    {
        var bytes = IPAddress.Parse(route.Address).GetAddressBytes();
        var wholeBytes = route.PrefixLength / 8;
        var remainingBits = route.PrefixLength % 8;
        if (remainingBits != 0)
        {
            bytes[wholeBytes] &= (byte)(byte.MaxValue << (8 - remainingBits));
            wholeBytes++;
        }

        Array.Clear(bytes, wholeBytes, bytes.Length - wholeBytes);
        return new RoutePrefix($"{new IPAddress(bytes)}/{route.PrefixLength}");
    }
}
