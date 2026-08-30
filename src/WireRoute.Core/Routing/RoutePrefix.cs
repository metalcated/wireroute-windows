using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireRoute.Core.Routing;

public enum RoutePrefixValidationError
{
    InvalidFormat,
    InvalidAddress,
    InvalidPrefixLength,
}

public sealed class RoutePrefixValidationException : FormatException
{
    public RoutePrefixValidationException(RoutePrefixValidationError error, string value)
        : base(MessageFor(error, value))
    {
        Error = error;
        Value = value;
    }

    public RoutePrefixValidationError Error { get; }

    public string Value { get; }

    private static string MessageFor(RoutePrefixValidationError error, string value) => error switch
    {
        RoutePrefixValidationError.InvalidFormat => $"{value} is not an address and prefix.",
        RoutePrefixValidationError.InvalidAddress => $"{value} does not contain a valid IP address.",
        RoutePrefixValidationError.InvalidPrefixLength => $"{value} has an invalid prefix length.",
        _ => $"{value} is not a valid route prefix.",
    };
}

[JsonConverter(typeof(RoutePrefixJsonConverter))]
public readonly record struct RoutePrefix
{
    public RoutePrefix(string notation)
    {
        var trimmedNotation = notation.Trim();
        var components = trimmedNotation.Split('/', StringSplitOptions.None);
        if (components.Length != 2)
        {
            throw new RoutePrefixValidationException(RoutePrefixValidationError.InvalidFormat, notation);
        }

        if (!byte.TryParse(components[1], out var prefixLength))
        {
            throw new RoutePrefixValidationException(RoutePrefixValidationError.InvalidPrefixLength, notation);
        }

        if (!IPAddress.TryParse(components[0], out var address))
        {
            throw new RoutePrefixValidationException(RoutePrefixValidationError.InvalidAddress, notation);
        }

        var family = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IpFamily.Ipv4,
            AddressFamily.InterNetworkV6 => IpFamily.Ipv6,
            _ => throw new RoutePrefixValidationException(RoutePrefixValidationError.InvalidAddress, notation),
        };
        var maximumPrefixLength = family == IpFamily.Ipv4 ? 32 : 128;
        if (prefixLength > maximumPrefixLength)
        {
            throw new RoutePrefixValidationException(RoutePrefixValidationError.InvalidPrefixLength, notation);
        }

        Address = address.ToString();
        PrefixLength = prefixLength;
        Family = family;
    }

    private RoutePrefix(string address, byte prefixLength, IpFamily family)
    {
        Address = address;
        PrefixLength = prefixLength;
        Family = family;
    }

    public string Address { get; }

    public byte PrefixLength { get; }

    public IpFamily Family { get; }

    public string Notation => $"{Address}/{PrefixLength}";

    public bool IsDefaultRoute => PrefixLength == 0;

    public bool Contains(string candidate)
    {
        if (!IPAddress.TryParse(Address, out var routeAddress)
            || !IPAddress.TryParse(candidate, out var candidateAddress)
            || routeAddress.AddressFamily != candidateAddress.AddressFamily)
        {
            return false;
        }

        var routeBytes = routeAddress.GetAddressBytes();
        var candidateBytes = candidateAddress.GetAddressBytes();
        var wholeBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var index = 0; index < wholeBytes; index++)
        {
            if (routeBytes[index] != candidateBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(byte.MaxValue << (8 - remainingBits));
        return (routeBytes[wholeBytes] & mask) == (candidateBytes[wholeBytes] & mask);
    }

    public static IReadOnlyList<RoutePrefix> ParseList(string value)
    {
        var values = value.Split(
            [' ', '\t', '\r', '\n', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var routes = new List<RoutePrefix>(values.Length);
        var seen = new HashSet<RoutePrefix>();
        foreach (var notation in values)
        {
            var route = new RoutePrefix(notation);
            if (seen.Add(route))
            {
                routes.Add(route);
            }
        }

        return routes;
    }

    internal static RoutePrefix DefaultFor(IpFamily family) => family switch
    {
        IpFamily.Ipv4 => new RoutePrefix("0.0.0.0", 0, IpFamily.Ipv4),
        IpFamily.Ipv6 => new RoutePrefix("::", 0, IpFamily.Ipv6),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}

public sealed class RoutePrefixJsonConverter : JsonConverter<RoutePrefix>
{
    public override RoutePrefix Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var notation = reader.GetString()
            ?? throw new JsonException("A route prefix must be encoded as a string.");
        try
        {
            return new RoutePrefix(notation);
        }
        catch (RoutePrefixValidationException exception)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, RoutePrefix value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Notation);
}

public enum DnsServerRoute
{
    ThroughTunnel,
    OutsideTunnel,
}

public sealed record ProfileDnsServerRoute(string Address, DnsServerRoute Route);

public sealed class ProfileDnsRouteSummary
{
    public ProfileDnsRouteSummary(
        IEnumerable<string> dnsServers,
        IEnumerable<string> searchDomains,
        IEnumerable<RoutePrefix> allowedRoutes,
        bool isConfigurationAvailable = true)
    {
        var routes = allowedRoutes.ToArray();
        Servers = dnsServers
            .Select(address => new ProfileDnsServerRoute(
                address,
                routes.Any(route => route.Contains(address))
                    ? DnsServerRoute.ThroughTunnel
                    : DnsServerRoute.OutsideTunnel))
            .ToArray();
        SearchDomains = searchDomains.ToArray();
        IsConfigurationAvailable = isConfigurationAvailable;
    }

    public bool IsConfigurationAvailable { get; }

    public IReadOnlyList<ProfileDnsServerRoute> Servers { get; }

    public IReadOnlyList<string> SearchDomains { get; }
}
