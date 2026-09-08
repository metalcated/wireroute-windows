using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using WireRoute.Core.Profiles;

namespace WireRoute.App.Interop;

internal sealed record ProfileNetworkSnapshot(ProfileNetworkTransport Transport, string Identity,
    string? Ssid, bool AnotherVpn);

internal static class ProfileNetworkMonitor
{
    public static ProfileNetworkSnapshot Read(IReadOnlySet<string> ownedTunnelNames, bool readWiFiNames)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var activeInterfaces = interfaces.Where(i => i.OperationalStatus == OperationalStatus.Up).ToArray();
        // Conservative: unknown virtual connections with an address block switching.
        var anotherVpn = activeInterfaces.Any(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && !ownedTunnelNames.Contains(i.Name) && Guid.TryParse(i.Id, out var id)
            && !PhysicalNetworkInterface.IsHardware(id)
            && i.GetIPProperties().UnicastAddresses.Count != 0);
        var profiles = NetworkInformation.GetConnectionProfiles()
            .Where(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None
                && p.NetworkAdapter is not null
                && PhysicalNetworkInterface.IsHardware(p.NetworkAdapter.NetworkAdapterId))
            .ToArray();
        // Use a stable physical-transport priority. NCSI/default-route changes
        // caused by our own full-tunnel connection must not switch profiles again.
        var selected = profiles.OrderByDescending(p => Transport(p) switch
            {
                ProfileNetworkTransport.Ethernet => 3,
                ProfileNetworkTransport.WiFi => 2,
                ProfileNetworkTransport.Cellular => 1,
                _ => 0,
            })
            .ThenBy(p => p.NetworkAdapter.NetworkAdapterId).FirstOrDefault();
        if (selected is null) return new(ProfileNetworkTransport.None, "none", null, anotherVpn);
        var transport = Transport(selected);
        string? ssid = null;
        if (transport == ProfileNetworkTransport.WiFi && readWiFiNames)
        {
            try { ssid = selected.WlanConnectionProfileDetails?.GetConnectedSsid(); }
            catch (UnauthorizedAccessException) { /* Named rules hold until access is available. */ }
        }
        // A Windows profile display name is part of identity, never a substitute SSID.
        var identity = $"{selected.NetworkAdapter.NetworkAdapterId:N}:{transport}:{selected.ProfileName}:{ssid}";
        return new(transport, identity, ssid, anotherVpn);
    }

    private static ProfileNetworkTransport Transport(ConnectionProfile profile) =>
        profile.IsWlanConnectionProfile ? ProfileNetworkTransport.WiFi
        : profile.IsWwanConnectionProfile ? ProfileNetworkTransport.Cellular
        : profile.NetworkAdapter.IanaInterfaceType switch
        {
            6 => ProfileNetworkTransport.Ethernet,
            71 => ProfileNetworkTransport.WiFi,
            243 or 244 => ProfileNetworkTransport.Cellular,
            _ => ProfileNetworkTransport.Other,
        };
}
