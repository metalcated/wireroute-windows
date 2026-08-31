using System.Text;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Profiles;

public static class WireGuardConfigFormatter
{
    public static string ToWgQuick(
        WireGuardProfile profile,
        IReadOnlyList<RoutePrefix>? firstPeerAllowedIps = null,
        IReadOnlyList<string>? dnsServers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var builder = new StringBuilder();
        builder.AppendLine("[Interface]");
        Append(builder, "PrivateKey", profile.Interface.PrivateKey.EncodedValue);
        AppendList(builder, "Address", profile.Interface.Addresses.Select(value => value.Notation));
        AppendList(
            builder,
            "DNS",
            (dnsServers ?? profile.Interface.DnsServers)
                .Concat(profile.Interface.DnsSearchDomains));
        if (profile.Interface.ListenPort is not null)
        {
            Append(builder, "ListenPort", profile.Interface.ListenPort.Value.ToString());
        }
        if (profile.Interface.Mtu is not null)
        {
            Append(builder, "MTU", profile.Interface.Mtu.Value.ToString());
        }
        if (profile.Interface.TableOff)
        {
            Append(builder, "Table", "off");
        }
        AppendOptional(builder, "PreUp", profile.Interface.Hooks.PreUp);
        AppendOptional(builder, "PostUp", profile.Interface.Hooks.PostUp);
        AppendOptional(builder, "PreDown", profile.Interface.Hooks.PreDown);
        AppendOptional(builder, "PostDown", profile.Interface.Hooks.PostDown);

        for (var index = 0; index < profile.Peers.Count; index++)
        {
            var peer = profile.Peers[index];
            builder.AppendLine();
            builder.AppendLine("[Peer]");
            Append(builder, "PublicKey", peer.PublicKey);
            if (peer.PresharedKey is not null)
            {
                Append(builder, "PresharedKey", peer.PresharedKey.EncodedValue);
            }
            AppendList(
                builder,
                "AllowedIPs",
                (index == 0 && firstPeerAllowedIps is not null
                    ? firstPeerAllowedIps
                    : peer.AllowedIps).Select(value => value.Notation));
            if (peer.Endpoint is not null)
            {
                Append(builder, "Endpoint", peer.Endpoint.DisplayValue);
            }
            if (peer.PersistentKeepalive is not null)
            {
                Append(builder, "PersistentKeepalive", peer.PersistentKeepalive.Value.ToString());
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string PrivateKey(WireGuardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Interface.PrivateKey.EncodedValue;
    }

    private static void Append(StringBuilder builder, string key, string value) =>
        builder.Append(key).Append(" = ").AppendLine(value);

    private static void AppendOptional(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Append(builder, key, value);
        }
    }

    private static void AppendList(StringBuilder builder, string key, IEnumerable<string> values)
    {
        var rendered = string.Join(", ", values);
        if (rendered.Length > 0)
        {
            Append(builder, key, rendered);
        }
    }
}
