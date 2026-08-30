using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;

namespace WireRoute.App.Models;

public sealed class ProfileNavigationItem
{
    public ProfileNavigationItem(WireGuardProfile profile)
    {
        Profile = profile;
        Name = profile.Name;
        Status = "Imported preview";
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
    }

    public WireGuardProfile Profile { get; }

    public string Name { get; set; }

    public string Status { get; set; }

    public string RoutingLabel { get; set; }
}
