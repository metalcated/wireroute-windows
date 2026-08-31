using WireRoute.RouterOS;

namespace WireRoute.App.Models;

public sealed class RouterOSDiscoveryRow
{
    public RouterOSDiscoveryRow()
    {
    }

    private RouterOSDiscoveryRow(string name, string detail, string status, bool isPeer)
    {
        Name = name;
        Detail = detail;
        Status = status;
        IsPeer = isPeer;
    }

    public string Name { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public bool IsPeer { get; set; }

    public static RouterOSDiscoveryRow FromInterface(RouterOSWireGuardInterface value) => new(
        value.Name,
        value.ListenPort is null ? "—" : $"Port {value.ListenPort}",
        value.IsDisabled ? "Disabled" : value.IsRunning ? "Running" : "Stopped",
        false);

    public static RouterOSDiscoveryRow FromPeer(RouterOSWireGuardPeer value)
    {
        var name = FirstNonempty(value.Name, value.Comment) ?? "Unnamed peer";
        var status = value.IsDisabled
            ? "Disabled"
            : string.IsNullOrWhiteSpace(value.LastHandshake)
                ? "No handshake"
                : $"Handshake {value.LastHandshake} ago";
        return new RouterOSDiscoveryRow(name, value.InterfaceName, status, true);
    }

    private static string? FirstNonempty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
