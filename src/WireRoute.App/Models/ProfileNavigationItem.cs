using WireRoute.Core.Profiles;
using WireRoute.Core.Manager;
using WireRoute.Core.Routing;

namespace WireRoute.App.Models;

public sealed class ProfileNavigationItem
{
    public ProfileNavigationItem(
        WireGuardProfile profile,
        string? managerName = null,
        string? displayName = null,
        ManagerTunnelState managerState = ManagerTunnelState.Unknown)
    {
        Profile = profile;
        ManagerName = managerName;
        Name = displayName ?? profile.Name;
        ManagerState = managerState;
        Status = managerName is null ? "Imported preview" : StatusText(managerState);
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
    }

    public ProfileNavigationItem(ManagerProfileSummary profile)
    {
        ManagerName = profile.Name;
        Name = profile.DisplayName;
        ManagerState = profile.State;
        Status = StatusText(profile.State);
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
    }

    public WireGuardProfile? Profile { get; }

    public string Name { get; set; }

    public string Status { get; set; }

    public string RoutingLabel { get; set; }

    internal string? ManagerName { get; }

    internal ManagerTunnelState ManagerState { get; private set; }

    internal bool IsManaged => ManagerName is not null;

    internal void UpdateState(ManagerTunnelState state)
    {
        ManagerState = state;
        Status = StatusText(state);
    }

    private static string StatusText(ManagerTunnelState state) => state switch
    {
        ManagerTunnelState.Starting => "Connecting",
        ManagerTunnelState.Started => "Connected",
        ManagerTunnelState.Stopping => "Disconnecting",
        ManagerTunnelState.Stopped => "Disconnected",
        _ => "Status unavailable",
    };
}
