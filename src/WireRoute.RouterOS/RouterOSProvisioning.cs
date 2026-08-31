using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using WireRoute.Core.Routing;

namespace WireRoute.RouterOS;

public enum RouterOSProvisioningError
{
    MissingInterface,
    MissingPeerName,
    InvalidKey,
    InvalidClientAddress,
    DuplicatePublicKey,
    OverlappingClientAddress,
    InvalidPersistentKeepalive,
    MissingEndpoint,
    InvalidEndpoint,
    InvalidEndpointPort,
    MissingClientRoutes,
    InvalidClientRoute,
    InvalidDnsServer,
}

public sealed class RouterOSProvisioningException : ArgumentException
{
    public RouterOSProvisioningException(
        RouterOSProvisioningError error,
        string message,
        string? invalidValue = null)
        : base(message)
    {
        Error = error;
        InvalidValue = invalidValue;
    }

    public RouterOSProvisioningError Error { get; }

    public string? InvalidValue { get; }
}

public sealed record RouterOSPeerDefaults(
    string? EndpointAddress,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<RoutePrefix> SplitRoutes,
    ushort PersistentKeepalive)
{
    public static RouterOSPeerDefaults Standard { get; } = new(null, [], [], 25);

    public static RouterOSPeerDefaults Create(
        string? endpointAddress,
        IEnumerable<string> dnsServers,
        IEnumerable<string> splitRoutes,
        int persistentKeepalive = 25)
    {
        var trimmedEndpoint = endpointAddress?.Trim();
        var normalizedEndpoint = string.IsNullOrEmpty(trimmedEndpoint)
            ? null
            : WireGuardClientConfiguration.NormalizeEndpointAddress(trimmedEndpoint)
                ?? throw Error(RouterOSProvisioningError.InvalidEndpoint);

        var normalizedDnsServers = dnsServers
            .Select(value => WireGuardClientConfiguration.NormalizeIpAddress(value)
                ?? throw Error(RouterOSProvisioningError.InvalidDnsServer, value))
            .ToArray();
        var routes = new List<RoutePrefix>();
        foreach (var route in splitRoutes)
        {
            try
            {
                routes.Add(new RoutePrefix(route));
            }
            catch (RoutePrefixValidationException)
            {
                throw Error(RouterOSProvisioningError.InvalidClientRoute, route);
            }
        }

        if (persistentKeepalive is < 0 or > ushort.MaxValue)
        {
            throw Error(RouterOSProvisioningError.InvalidPersistentKeepalive);
        }

        return new RouterOSPeerDefaults(
            normalizedEndpoint,
            normalizedDnsServers,
            routes,
            (ushort)persistentKeepalive);
    }

    private static RouterOSProvisioningException Error(
        RouterOSProvisioningError error,
        string? value = null) => RouterOSProvisioningErrors.Create(error, value);
}

public sealed record RouterOSPublicEndpointSuggestion(string Address)
{
    public static RouterOSPublicEndpointSuggestion? Discover(IEnumerable<RouterOSIpAddress> addresses)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var routerAddress in addresses)
        {
            if (routerAddress.IsDisabled || routerAddress.IsInvalid)
            {
                continue;
            }

            RoutePrefix prefix;
            try
            {
                prefix = new RoutePrefix(routerAddress.Address);
            }
            catch (RoutePrefixValidationException)
            {
                continue;
            }

            if (prefix.Family == IpFamily.Ipv4
                && IPAddress.TryParse(prefix.Address, out var address)
                && IsPublicIpv4(address))
            {
                candidates.Add(address.ToString());
            }
        }

        return candidates.Count == 1
            ? new RouterOSPublicEndpointSuggestion(candidates.Single())
            : null;
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes switch
        {
            [0, _, _, _] or [10, _, _, _] or [127, _, _, _] => false,
            [100, >= 64 and <= 127, _, _] => false,
            [169, 254, _, _] => false,
            [172, >= 16 and <= 31, _, _] => false,
            [192, 0, 0, _] or [192, 0, 2, _] or [192, 168, _, _] => false,
            [198, >= 18 and <= 19, _, _] or [198, 51, 100, _] or [203, 0, 113, _] => false,
            [>= 224, _, _, _] => false,
            _ => true,
        };
    }
}

public sealed record RouterOSClientAddressSuggestion(RoutePrefix Address, int SourceAddressCount)
{
    public static RouterOSClientAddressSuggestion? Discover(
        string interfaceName,
        IEnumerable<RouterOSWireGuardPeer> existingPeers)
    {
        var existingPrefixes = existingPeers
            .Where(peer => peer.InterfaceName.Equals(interfaceName, StringComparison.Ordinal))
            .SelectMany(peer => peer.AllowedAddresses)
            .Select(value =>
            {
                try
                {
                    return (RoutePrefix?)new RoutePrefix(value);
                }
                catch (RoutePrefixValidationException)
                {
                    return null;
                }
            })
            .Where(prefix => prefix is not null)
            .Select(prefix => prefix!.Value)
            .ToArray();

        var uniqueHostAddresses = existingPrefixes
            .Where(prefix => prefix.Family == IpFamily.Ipv4 && prefix.PrefixLength == 32)
            .Select(prefix => IPAddress.Parse(prefix.Address).GetAddressBytes())
            .Distinct(ByteArrayComparer.Instance)
            .ToArray();
        var pools = uniqueHostAddresses
            .GroupBy(bytes => Convert.ToHexString(bytes.AsSpan(0, 3)), StringComparer.Ordinal)
            .ToArray();
        if (pools.Length == 0)
        {
            return null;
        }

        var largestSize = pools.Max(pool => pool.Count());
        var largestPools = pools.Where(pool => pool.Count() == largestSize).ToArray();
        if (largestPools.Length != 1)
        {
            return null;
        }

        var poolAddresses = largestPools[0].ToArray();
        var highestHost = poolAddresses.Max(bytes => bytes[3]);
        if (highestHost >= 254)
        {
            return null;
        }

        var candidateBytes = poolAddresses[0].ToArray();
        candidateBytes[3] = (byte)(highestHost + 1);
        var candidate = new RoutePrefix($"{new IPAddress(candidateBytes)}/32");
        return existingPrefixes.Any(prefix => RouterOSPeerCreation.Overlaps(candidate, prefix))
            ? null
            : new RouterOSClientAddressSuggestion(candidate, poolAddresses.Length);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            foreach (var value in obj)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}

public sealed class RouterOSPeerCreation
{
    public const string WireRouteManagedComment = "Managed by WireRoute";

    public RouterOSPeerCreation(
        string interfaceName,
        string name,
        string? comment,
        string publicKey,
        string clientAddress,
        int persistentKeepalive = 25,
        bool isResponder = true,
        IEnumerable<RouterOSWireGuardPeer>? existingPeers = null)
    {
        InterfaceName = interfaceName.Trim();
        if (InterfaceName.Length == 0)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingInterface);
        }

        Name = name.Trim();
        if (Name.Length == 0)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingPeerName);
        }

        if (!IsWireGuardKey(publicKey))
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidKey);
        }

        if (persistentKeepalive is < 0 or > ushort.MaxValue)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidPersistentKeepalive);
        }

        try
        {
            ClientAddress = new RoutePrefix(clientAddress);
        }
        catch (RoutePrefixValidationException)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidClientAddress);
        }

        var requiredPrefixLength = ClientAddress.Family == IpFamily.Ipv4 ? 32 : 128;
        if (ClientAddress.PrefixLength != requiredPrefixLength)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidClientAddress);
        }

        var peersOnInterface = (existingPeers ?? [])
            .Where(peer => peer.InterfaceName.Equals(InterfaceName, StringComparison.Ordinal))
            .ToArray();
        if (peersOnInterface.Any(peer => peer.PublicKey.Equals(publicKey, StringComparison.Ordinal)))
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.DuplicatePublicKey);
        }

        foreach (var existingAddress in peersOnInterface.SelectMany(peer => peer.AllowedAddresses))
        {
            RoutePrefix existingPrefix;
            try
            {
                existingPrefix = new RoutePrefix(existingAddress);
            }
            catch (RoutePrefixValidationException)
            {
                continue;
            }

            if (Overlaps(ClientAddress, existingPrefix))
            {
                throw RouterOSProvisioningErrors.Create(
                    RouterOSProvisioningError.OverlappingClientAddress,
                    existingPrefix.Notation);
            }
        }

        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        PublicKey = publicKey;
        PersistentKeepalive = (ushort)persistentKeepalive;
        IsResponder = isResponder;
    }

    public string InterfaceName { get; }

    public string Name { get; }

    public string? Comment { get; }

    public string PublicKey { get; }

    public RoutePrefix ClientAddress { get; }

    public ushort PersistentKeepalive { get; }

    public bool IsResponder { get; }

    internal RouterOSPeerCreateRequest RequestPayload => new(
        InterfaceName,
        Name,
        Comment,
        PublicKey,
        ClientAddress.Notation,
        $"{PersistentKeepalive}s",
        IsResponder ? "true" : "false");

    public static bool IsWireGuardKey(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 && Convert.ToBase64String(bytes).Equals(value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsWireRouteManagedComment(string? comment) =>
        comment?.Trim().Equals(WireRouteManagedComment, StringComparison.OrdinalIgnoreCase) == true;

    internal static bool Overlaps(RoutePrefix left, RoutePrefix right) =>
        left.Family == right.Family
        && (left.Contains(right.Address) || right.Contains(left.Address));
}

public sealed record WireGuardKeyPair(string PublicKey, string PrivateKey)
{
    public static WireGuardKeyPair Generate()
    {
        using var agreement = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("curve25519"));
        var parameters = agreement.ExportParameters(includePrivateParameters: true);
        var privateKey = parameters.D;
        var publicKey = parameters.Q.X;
        if (privateKey is not { Length: 32 } || publicKey is not { Length: 32 })
        {
            throw new CryptographicException("Windows did not generate a valid X25519 key pair.");
        }

        try
        {
            return new WireGuardKeyPair(
                Convert.ToBase64String(publicKey),
                Convert.ToBase64String(privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public static WireGuardKeyPair FromPrivateKey(string privateKey)
    {
        byte[] privateBytes;
        try
        {
            privateBytes = Convert.FromBase64String(privateKey);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The WireGuard private key is not valid base64.", exception);
        }
        if (privateBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            throw new CryptographicException("A WireGuard private key must contain exactly 32 bytes.");
        }

        try
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.CreateFromFriendlyName("curve25519"),
                D = privateBytes,
            };
            using var agreement = ECDiffieHellman.Create(parameters);
            var exported = agreement.ExportParameters(includePrivateParameters: false);
            if (exported.Q.X is not { Length: 32 } publicBytes)
            {
                throw new CryptographicException("Windows did not derive a valid X25519 public key.");
            }
            return new WireGuardKeyPair(
                Convert.ToBase64String(publicBytes),
                privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }
}

public sealed class WireGuardClientConfiguration
{
    public WireGuardClientConfiguration(
        string name,
        string privateKey,
        string clientAddress,
        IEnumerable<string> dnsServers,
        string serverPublicKey,
        string endpointAddress,
        int endpointPort,
        IEnumerable<string> allowedIps,
        int persistentKeepalive = 25)
    {
        if (!RouterOSPeerCreation.IsWireGuardKey(privateKey)
            || !RouterOSPeerCreation.IsWireGuardKey(serverPublicKey))
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidKey);
        }

        Name = name.Trim();
        if (Name.Length == 0)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingPeerName);
        }

        var endpoint = endpointAddress.Trim();
        if (endpoint.Length == 0)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingEndpoint);
        }

        EndpointAddress = NormalizeEndpointAddress(endpoint)
            ?? throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidEndpoint);
        if (endpointPort is < 1 or > ushort.MaxValue)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidEndpointPort);
        }

        if (persistentKeepalive is < 0 or > ushort.MaxValue)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidPersistentKeepalive);
        }

        try
        {
            ClientAddress = new RoutePrefix(clientAddress);
        }
        catch (RoutePrefixValidationException)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidClientAddress);
        }

        var requiredPrefixLength = ClientAddress.Family == IpFamily.Ipv4 ? 32 : 128;
        if (ClientAddress.PrefixLength != requiredPrefixLength)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidClientAddress);
        }

        var routes = new List<RoutePrefix>();
        try
        {
            routes.AddRange(allowedIps.Select(value => new RoutePrefix(value)));
        }
        catch (RoutePrefixValidationException)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingClientRoutes);
        }

        if (routes.Count == 0)
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.MissingClientRoutes);
        }

        var normalizedDnsServers = dnsServers
            .Select(value => NormalizeIpAddress(value)
                ?? throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidDnsServer, value))
            .ToArray();

        PrivateKey = privateKey;
        DnsServers = normalizedDnsServers;
        ServerPublicKey = serverPublicKey;
        EndpointPort = (ushort)endpointPort;
        AllowedIps = routes;
        PersistentKeepalive = (ushort)persistentKeepalive;
    }

    public string Name { get; }

    public string PrivateKey { get; }

    public RoutePrefix ClientAddress { get; }

    public IReadOnlyList<string> DnsServers { get; }

    public string ServerPublicKey { get; }

    public string EndpointAddress { get; }

    public ushort EndpointPort { get; }

    public IReadOnlyList<RoutePrefix> AllowedIps { get; }

    public ushort PersistentKeepalive { get; }

    public string WgQuickConfiguration
    {
        get
        {
            var interfaceLines = new List<string>
            {
                "[Interface]",
                $"PrivateKey = {PrivateKey}",
                $"Address = {ClientAddress.Notation}",
            };
            if (DnsServers.Count > 0)
            {
                interfaceLines.Add($"DNS = {string.Join(", ", DnsServers)}");
            }

            var endpoint = EndpointAddress.Contains(':', StringComparison.Ordinal)
                && !EndpointAddress.StartsWith("[", StringComparison.Ordinal)
                    ? $"[{EndpointAddress}]"
                    : EndpointAddress;
            interfaceLines.AddRange([
                string.Empty,
                "[Peer]",
                $"PublicKey = {ServerPublicKey}",
                $"Endpoint = {endpoint}:{EndpointPort}",
                $"AllowedIPs = {string.Join(", ", AllowedIps.Select(route => route.Notation))}",
                $"PersistentKeepalive = {PersistentKeepalive}",
                string.Empty,
            ]);
            return string.Join('\n', interfaceLines);
        }
    }

    internal static string? NormalizeIpAddress(string value) =>
        IPAddress.TryParse(value.Trim(), out var address) ? address.ToString() : null;

    internal static string? NormalizeEndpointAddress(string value)
    {
        var unwrapped = value.StartsWith("[", StringComparison.Ordinal)
            && value.EndsWith("]", StringComparison.Ordinal)
                ? value[1..^1]
                : value;
        if (IPAddress.TryParse(unwrapped, out var address))
        {
            return address.ToString();
        }

        if (unwrapped.Length == 0
            || unwrapped.Any(char.IsWhiteSpace)
            || unwrapped.IndexOfAny("/?#@[]:".ToCharArray()) >= 0
            || unwrapped.StartsWith(".", StringComparison.Ordinal)
            || unwrapped.EndsWith(".", StringComparison.Ordinal)
            || unwrapped.Contains("..", StringComparison.Ordinal)
            || unwrapped.Any(character => !char.IsLetterOrDigit(character) && ".-_".IndexOf(character) < 0))
        {
            return null;
        }

        return unwrapped;
    }
}

internal sealed record RouterOSPeerCreateRequest(
    [property: JsonPropertyName("interface")] string InterfaceName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("public-key")] string PublicKey,
    [property: JsonPropertyName("allowed-address")] string AllowedAddress,
    [property: JsonPropertyName("persistent-keepalive")] string PersistentKeepalive,
    [property: JsonPropertyName("responder")] string Responder);

internal static class RouterOSProvisioningErrors
{
    public static RouterOSProvisioningException Create(
        RouterOSProvisioningError error,
        string? value = null) => new(error, Message(error, value), value);

    private static string Message(RouterOSProvisioningError error, string? value) => error switch
    {
        RouterOSProvisioningError.MissingInterface => "Select a WireGuard interface.",
        RouterOSProvisioningError.MissingPeerName => "Enter a name for this peer.",
        RouterOSProvisioningError.InvalidKey => "A WireGuard key is invalid.",
        RouterOSProvisioningError.InvalidClientAddress =>
            "The client address must be one IPv4 /32 or IPv6 /128 address.",
        RouterOSProvisioningError.DuplicatePublicKey =>
            "This WireGuard public key already exists on the selected interface.",
        RouterOSProvisioningError.OverlappingClientAddress =>
            $"The client address overlaps the existing RouterOS peer route {value}.",
        RouterOSProvisioningError.InvalidPersistentKeepalive =>
            "Persistent keepalive must be between 0 and 65535 seconds.",
        RouterOSProvisioningError.MissingEndpoint =>
            "Enter the public hostname or address clients use to reach this router.",
        RouterOSProvisioningError.InvalidEndpoint =>
            "Enter only a public hostname or IP address, without a URL scheme, path, or port.",
        RouterOSProvisioningError.InvalidEndpointPort =>
            "The endpoint port must be between 1 and 65535.",
        RouterOSProvisioningError.MissingClientRoutes =>
            "Choose at least one route for the client profile.",
        RouterOSProvisioningError.InvalidClientRoute => $"{value} is not a valid client route.",
        RouterOSProvisioningError.InvalidDnsServer => $"{value} is not a valid DNS server address.",
        _ => "The RouterOS peer proposal is invalid.",
    };
}
