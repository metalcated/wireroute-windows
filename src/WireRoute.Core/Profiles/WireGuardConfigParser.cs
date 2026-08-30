using System.Net;
using System.Text.RegularExpressions;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Profiles;

public enum WireGuardConfigParseError
{
    InvalidProfileName,
    InvalidLine,
    LineOutsideSection,
    MultipleInterfaces,
    DuplicateKey,
    EmptyValue,
    InvalidInterfaceKey,
    InvalidPeerKey,
    MissingPrivateKey,
    MissingPeerPublicKey,
    InvalidKey,
    InvalidAddress,
    InvalidEndpoint,
    InvalidPort,
    InvalidMtu,
    InvalidPersistentKeepalive,
    InvalidTable,
    DuplicatePeerPublicKey,
}

public sealed class WireGuardConfigParseException : FormatException
{
    public WireGuardConfigParseException(
        WireGuardConfigParseError error,
        string message,
        int? lineNumber = null,
        string? offendingValue = null)
        : base(lineNumber is null ? message : $"Line {lineNumber}: {message}")
    {
        Error = error;
        LineNumber = lineNumber;
        OffendingValue = offendingValue;
    }

    public WireGuardConfigParseError Error { get; }

    public int? LineNumber { get; }

    public string? OffendingValue { get; }
}

public static partial class WireGuardConfigParser
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static WireGuardProfile Parse(string text, string profileName)
    {
        if (!IsValidProfileName(profileName))
        {
            throw Error(
                WireGuardConfigParseError.InvalidProfileName,
                "The profile name must contain 1–32 letters, numbers, or _=+.- characters.",
                offendingValue: profileName);
        }

        InterfaceBuilder? interfaceBuilder = null;
        PeerBuilder? peerBuilder = null;
        var peers = new List<WireGuardPeer>();
        var section = Section.None;
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var rawLine = lines[index];
            var commentIndex = rawLine.IndexOf('#');
            var code = commentIndex >= 0 ? rawLine[..commentIndex] : rawLine;
            var trimmed = code.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                FinishPeer(peerBuilder, peers, lineNumber);
                peerBuilder = null;
                if (interfaceBuilder is not null)
                {
                    throw Error(
                        WireGuardConfigParseError.MultipleInterfaces,
                        "A configuration can contain only one [Interface] section.",
                        lineNumber);
                }

                interfaceBuilder = new InterfaceBuilder();
                section = Section.Interface;
                continue;
            }

            if (trimmed.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                FinishPeer(peerBuilder, peers, lineNumber);
                peerBuilder = new PeerBuilder();
                section = Section.Peer;
                continue;
            }

            if (section == Section.None)
            {
                throw Error(
                    WireGuardConfigParseError.LineOutsideSection,
                    "Configuration values must appear inside [Interface] or [Peer].",
                    lineNumber,
                    trimmed);
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
            {
                throw Error(
                    WireGuardConfigParseError.InvalidLine,
                    "A configuration value is missing the equals separator.",
                    lineNumber,
                    trimmed);
            }

            var key = trimmed[..separatorIndex].Trim().ToLowerInvariant();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (IsHookKey(key))
            {
                var rawSeparatorIndex = rawLine.IndexOf('=');
                value = rawLine[(rawSeparatorIndex + 1)..].Trim();
            }

            if (value.Length == 0)
            {
                throw Error(
                    WireGuardConfigParseError.EmptyValue,
                    $"{key} must have a value.",
                    lineNumber);
            }

            if (section == Section.Interface)
            {
                ParseInterfaceValue(interfaceBuilder!, key, value, lineNumber);
            }
            else
            {
                ParsePeerValue(peerBuilder!, key, value, lineNumber);
            }
        }

        FinishPeer(peerBuilder, peers, lines.Length);
        if (interfaceBuilder?.PrivateKey is null)
        {
            throw Error(
                WireGuardConfigParseError.MissingPrivateKey,
                "The [Interface] section must contain a private key.");
        }

        var duplicatePublicKey = peers
            .GroupBy(peer => peer.PublicKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicatePublicKey is not null)
        {
            throw Error(
                WireGuardConfigParseError.DuplicatePeerPublicKey,
                "Two peers cannot use the same public key.",
                offendingValue: duplicatePublicKey);
        }

        return new WireGuardProfile(profileName, interfaceBuilder.Build(), peers);
    }

    public static bool IsValidProfileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var baseName = name.Split('.', 2)[0];
        return NameFormat().IsMatch(name) && !ReservedNames.Contains(baseName);
    }

    private static void ParseInterfaceValue(InterfaceBuilder builder, string key, string value, int lineNumber)
    {
        builder.RegisterKey(key, key is "address" or "dns", lineNumber);
        switch (key)
        {
            case "privatekey":
                builder.PrivateKey = ParseSensitiveKey(value, lineNumber);
                break;
            case "listenport":
                builder.ListenPort = ParsePort(value, lineNumber);
                break;
            case "mtu":
                builder.Mtu = ParseMtu(value, lineNumber);
                break;
            case "address":
                builder.Addresses.AddRange(ParseRouteList(value, lineNumber));
                break;
            case "dns":
                foreach (var item in SplitList(value, lineNumber))
                {
                    if (IPAddress.TryParse(item, out var address))
                    {
                        builder.DnsServers.Add(address.ToString());
                    }
                    else
                    {
                        builder.DnsSearchDomains.Add(item);
                    }
                }

                break;
            case "preup":
                builder.PreUp = value;
                break;
            case "postup":
                builder.PostUp = value;
                break;
            case "predown":
                builder.PreDown = value;
                break;
            case "postdown":
                builder.PostDown = value;
                break;
            case "table":
                builder.TableOff = ParseTableOff(value, lineNumber);
                break;
            default:
                throw Error(
                    WireGuardConfigParseError.InvalidInterfaceKey,
                    $"{key} is not valid in [Interface].",
                    lineNumber,
                    key);
        }
    }

    private static void ParsePeerValue(PeerBuilder builder, string key, string value, int lineNumber)
    {
        builder.RegisterKey(key, key == "allowedips", lineNumber);
        switch (key)
        {
            case "publickey":
                var publicKey = ParseSensitiveKey(value, lineNumber);
                if (publicKey.IsZero)
                {
                    throw Error(
                        WireGuardConfigParseError.MissingPeerPublicKey,
                        "A peer public key cannot be all zeroes.",
                        lineNumber);
                }

                builder.PublicKey = publicKey.EncodedValue;
                break;
            case "presharedkey":
                builder.PresharedKey = ParseSensitiveKey(value, lineNumber);
                break;
            case "allowedips":
                builder.AllowedIps.AddRange(ParseRouteList(value, lineNumber));
                break;
            case "persistentkeepalive":
                builder.PersistentKeepalive = ParsePersistentKeepalive(value, lineNumber);
                break;
            case "endpoint":
                builder.Endpoint = ParseEndpoint(value, lineNumber);
                break;
            default:
                throw Error(
                    WireGuardConfigParseError.InvalidPeerKey,
                    $"{key} is not valid in [Peer].",
                    lineNumber,
                    key);
        }
    }

    private static SensitiveWireGuardKey ParseSensitiveKey(string value, int lineNumber)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32)
            {
                return new SensitiveWireGuardKey(bytes);
            }
        }
        catch (FormatException)
        {
        }

        throw Error(
            WireGuardConfigParseError.InvalidKey,
            "A WireGuard key must be valid base64 that decodes to exactly 32 bytes.",
            lineNumber,
            "[redacted]");
    }

    private static IReadOnlyList<RoutePrefix> ParseRouteList(string value, int lineNumber) =>
        SplitList(value, lineNumber).Select(item => ParseRoute(item, lineNumber)).ToArray();

    private static RoutePrefix ParseRoute(string value, int lineNumber)
    {
        try
        {
            if (value.Contains('/', StringComparison.Ordinal))
            {
                return new RoutePrefix(value);
            }

            if (IPAddress.TryParse(value, out var address))
            {
                var prefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                return new RoutePrefix($"{address}/{prefixLength}");
            }
        }
        catch (RoutePrefixValidationException)
        {
        }

        throw Error(
            WireGuardConfigParseError.InvalidAddress,
            $"{value} is not a valid IP address or route prefix.",
            lineNumber,
            value);
    }

    private static WireGuardEndpoint ParseEndpoint(string value, int lineNumber)
    {
        var separatorIndex = value.LastIndexOf(':');
        if (separatorIndex < 1)
        {
            throw Error(
                WireGuardConfigParseError.InvalidEndpoint,
                "An endpoint must include a host and port.",
                lineNumber,
                value);
        }

        var host = value[..separatorIndex];
        var port = ParsePort(value[(separatorIndex + 1)..], lineNumber);
        var containsColon = host.Contains(':', StringComparison.Ordinal);
        if (host.StartsWith("[", StringComparison.Ordinal)
            || host.EndsWith("]", StringComparison.Ordinal)
            || containsColon)
        {
            if (host.Length < 4 || host[0] != '[' || host[^1] != ']')
            {
                throw Error(
                    WireGuardConfigParseError.InvalidEndpoint,
                    "IPv6 endpoint addresses must be enclosed in brackets.",
                    lineNumber,
                    value);
            }

            var bracketedHost = host[1..^1];
            var addressValue = bracketedHost.Split('%', 2)[0];
            if (!IPAddress.TryParse(addressValue, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw Error(
                    WireGuardConfigParseError.InvalidEndpoint,
                    "Endpoint brackets must contain an IPv6 address.",
                    lineNumber,
                    value);
            }

            host = bracketedHost;
        }

        return new WireGuardEndpoint(host, port);
    }

    private static ushort ParsePort(string value, int lineNumber)
    {
        if (ushort.TryParse(value, out var port))
        {
            return port;
        }

        throw Error(WireGuardConfigParseError.InvalidPort, $"{value} is not a valid port.", lineNumber, value);
    }

    private static ushort ParseMtu(string value, int lineNumber)
    {
        if (ushort.TryParse(value, out var mtu) && mtu >= 576)
        {
            return mtu;
        }

        throw Error(WireGuardConfigParseError.InvalidMtu, $"{value} is not a valid MTU.", lineNumber, value);
    }

    private static ushort ParsePersistentKeepalive(string value, int lineNumber)
    {
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (ushort.TryParse(value, out var keepalive))
        {
            return keepalive;
        }

        throw Error(
            WireGuardConfigParseError.InvalidPersistentKeepalive,
            $"{value} is not a valid persistent keepalive interval.",
            lineNumber,
            value);
    }

    private static bool ParseTableOff(string value, int lineNumber)
    {
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("main", StringComparison.OrdinalIgnoreCase)
            || uint.TryParse(value, out _))
        {
            return false;
        }

        throw Error(WireGuardConfigParseError.InvalidTable, $"{value} is not a valid table value.", lineNumber, value);
    }

    private static IReadOnlyList<string> SplitList(string value, int lineNumber)
    {
        var values = value.Split(',').Select(item => item.Trim()).ToArray();
        if (values.Any(item => item.Length == 0))
        {
            throw Error(
                WireGuardConfigParseError.InvalidLine,
                "A comma-separated value cannot contain an empty item.",
                lineNumber,
                value);
        }

        return values;
    }

    private static bool IsHookKey(string key) => key is "preup" or "postup" or "predown" or "postdown";

    private static void FinishPeer(PeerBuilder? builder, ICollection<WireGuardPeer> peers, int lineNumber)
    {
        if (builder is null)
        {
            return;
        }

        if (builder.PublicKey is null)
        {
            throw Error(
                WireGuardConfigParseError.MissingPeerPublicKey,
                "Every [Peer] section must contain a public key.",
                lineNumber);
        }

        peers.Add(builder.Build());
    }

    private static WireGuardConfigParseException Error(
        WireGuardConfigParseError error,
        string message,
        int? lineNumber = null,
        string? offendingValue = null) =>
        new(error, message, lineNumber, offendingValue);

    [GeneratedRegex("^[a-zA-Z0-9_=+.-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameFormat();

    private enum Section
    {
        None,
        Interface,
        Peer,
    }

    private abstract class SectionBuilder
    {
        private readonly HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterKey(string key, bool repeatable, int lineNumber)
        {
            if (!repeatable && !seenKeys.Add(key))
            {
                throw Error(
                    WireGuardConfigParseError.DuplicateKey,
                    $"{key} can appear only once in this section.",
                    lineNumber,
                    key);
            }

            seenKeys.Add(key);
        }
    }

    private sealed class InterfaceBuilder : SectionBuilder
    {
        public SensitiveWireGuardKey? PrivateKey { get; set; }

        public List<RoutePrefix> Addresses { get; } = [];

        public List<string> DnsServers { get; } = [];

        public List<string> DnsSearchDomains { get; } = [];

        public ushort? ListenPort { get; set; }

        public ushort? Mtu { get; set; }

        public bool TableOff { get; set; }

        public string? PreUp { get; set; }

        public string? PostUp { get; set; }

        public string? PreDown { get; set; }

        public string? PostDown { get; set; }

        public WireGuardInterface Build() => new(
            PrivateKey!,
            StableUnique(Addresses),
            StableUnique(DnsServers),
            StableUnique(DnsSearchDomains),
            ListenPort,
            Mtu,
            TableOff,
            new WireGuardHooks(PreUp, PostUp, PreDown, PostDown));
    }

    private sealed class PeerBuilder : SectionBuilder
    {
        public string? PublicKey { get; set; }

        public SensitiveWireGuardKey? PresharedKey { get; set; }

        public List<RoutePrefix> AllowedIps { get; } = [];

        public WireGuardEndpoint? Endpoint { get; set; }

        public ushort? PersistentKeepalive { get; set; }

        public WireGuardPeer Build() => new(
            PublicKey!,
            PresharedKey,
            StableUnique(AllowedIps),
            Endpoint,
            PersistentKeepalive);
    }

    private static IReadOnlyList<T> StableUnique<T>(IEnumerable<T> values) where T : notnull
    {
        var result = new List<T>();
        var seen = new HashSet<T>();
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}
