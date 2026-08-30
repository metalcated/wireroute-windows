using WireRoute.Core.Routing;

namespace WireRoute.Core.Profiles;

public sealed class WireGuardProfile
{
    internal WireGuardProfile(
        string name,
        WireGuardInterface interfaceConfiguration,
        IEnumerable<WireGuardPeer> peers)
    {
        Name = name;
        Interface = interfaceConfiguration;
        Peers = peers.ToArray();
        ImportedAllowedIps = Unique(Peers.SelectMany(peer => peer.AllowedIps));
        DetectedRouteMode = ImportedAllowedIps.Any(route => route.IsDefaultRoute)
            ? TunnelRouteMode.Full
            : TunnelRouteMode.Split;
        SuggestedSplitAllowedIps = TunnelRoutingPolicy.SuggestedSplitAllowedIps(ImportedAllowedIps);
        DnsRouteSummary = new ProfileDnsRouteSummary(
            Interface.DnsServers,
            Interface.DnsSearchDomains,
            ImportedAllowedIps);
    }

    public string Name { get; }

    public WireGuardInterface Interface { get; }

    public IReadOnlyList<WireGuardPeer> Peers { get; }

    public TunnelRouteMode DetectedRouteMode { get; }

    public IReadOnlyList<RoutePrefix> ImportedAllowedIps { get; }

    public IReadOnlyList<RoutePrefix> SuggestedSplitAllowedIps { get; }

    public ProfileDnsRouteSummary DnsRouteSummary { get; }

    public override string ToString() => Name;

    private static IReadOnlyList<RoutePrefix> Unique(IEnumerable<RoutePrefix> routes)
    {
        var result = new List<RoutePrefix>();
        var seen = new HashSet<RoutePrefix>();
        foreach (var route in routes)
        {
            if (seen.Add(route))
            {
                result.Add(route);
            }
        }

        return result;
    }
}

public sealed class WireGuardInterface
{
    internal WireGuardInterface(
        SensitiveWireGuardKey privateKey,
        IEnumerable<RoutePrefix> addresses,
        IEnumerable<string> dnsServers,
        IEnumerable<string> dnsSearchDomains,
        ushort? listenPort,
        ushort? mtu,
        bool tableOff,
        WireGuardHooks hooks)
    {
        PrivateKey = privateKey;
        Addresses = addresses.ToArray();
        DnsServers = dnsServers.ToArray();
        DnsSearchDomains = dnsSearchDomains.ToArray();
        ListenPort = listenPort;
        Mtu = mtu;
        TableOff = tableOff;
        Hooks = hooks;
    }

    internal SensitiveWireGuardKey PrivateKey { get; }

    internal WireGuardHooks Hooks { get; }

    public bool HasPrivateKey => true;

    public IReadOnlyList<RoutePrefix> Addresses { get; }

    public IReadOnlyList<string> DnsServers { get; }

    public IReadOnlyList<string> DnsSearchDomains { get; }

    public ushort? ListenPort { get; }

    public ushort? Mtu { get; }

    public bool TableOff { get; }

    public bool HasHooks => Hooks.HasAny;
}

public sealed class WireGuardPeer
{
    internal WireGuardPeer(
        string publicKey,
        SensitiveWireGuardKey? presharedKey,
        IEnumerable<RoutePrefix> allowedIps,
        WireGuardEndpoint? endpoint,
        ushort? persistentKeepalive)
    {
        PublicKey = publicKey;
        PresharedKey = presharedKey;
        AllowedIps = allowedIps.ToArray();
        Endpoint = endpoint;
        PersistentKeepalive = persistentKeepalive;
    }

    internal SensitiveWireGuardKey? PresharedKey { get; }

    public string PublicKey { get; }

    public bool HasPresharedKey => PresharedKey is not null;

    public IReadOnlyList<RoutePrefix> AllowedIps { get; }

    public WireGuardEndpoint? Endpoint { get; }

    public ushort? PersistentKeepalive { get; }
}

public sealed record WireGuardEndpoint(string Host, ushort Port)
{
    public string DisplayValue => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}

internal sealed class SensitiveWireGuardKey
{
    public SensitiveWireGuardKey(byte[] bytes)
    {
        Bytes = bytes.ToArray();
    }

    public byte[] Bytes { get; }

    public bool IsZero => Bytes.All(value => value == 0);

    public string EncodedValue => Convert.ToBase64String(Bytes);

    public override string ToString() => "[redacted]";
}

internal sealed record WireGuardHooks(string? PreUp, string? PostUp, string? PreDown, string? PostDown)
{
    public bool HasAny => PreUp is not null || PostUp is not null || PreDown is not null || PostDown is not null;
}
