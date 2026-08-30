namespace WireRoute.Core.Routing;

public enum TunnelRouteMode
{
    Split,
    Full,
}

public sealed class TunnelRoutingPolicy
{
    public TunnelRoutingPolicy(IEnumerable<RoutePrefix> splitAllowedIps, TunnelRouteMode mode = TunnelRouteMode.Split)
    {
        Mode = mode;
        SplitAllowedIps = Unique(splitAllowedIps);
    }

    public TunnelRouteMode Mode { get; set; }

    public IReadOnlyList<RoutePrefix> SplitAllowedIps { get; }

    public static IReadOnlyList<RoutePrefix> SuggestedSplitAllowedIps(IEnumerable<RoutePrefix> importedAllowedIps) =>
        Unique(importedAllowedIps.Where(route => !route.IsDefaultRoute));

    private static IReadOnlyList<RoutePrefix> Unique(IEnumerable<RoutePrefix> routes)
    {
        var uniqueRoutes = new List<RoutePrefix>();
        var seen = new HashSet<RoutePrefix>();
        foreach (var route in routes)
        {
            if (seen.Add(route))
            {
                uniqueRoutes.Add(route);
            }
        }

        return uniqueRoutes;
    }
}

public sealed class ProfileNetworkCapabilities
{
    public ProfileNetworkCapabilities(IEnumerable<RoutePrefix> interfaceAddresses)
        : this(interfaceAddresses.Select(route => route.Family))
    {
    }

    public ProfileNetworkCapabilities(IEnumerable<IpFamily> supportedFamilies)
    {
        SupportedFamilies = new HashSet<IpFamily>(supportedFamilies);
    }

    public IReadOnlySet<IpFamily> SupportedFamilies { get; }
}

public sealed class RoutingPlan
{
    public RoutingPlan(
        TunnelRouteMode mode,
        IEnumerable<RoutePrefix> allowedIps,
        IEnumerable<IpFamily> blockedFamilies)
    {
        Mode = mode;
        AllowedIps = allowedIps.ToArray();
        BlockedFamilies = new HashSet<IpFamily>(blockedFamilies);
    }

    public TunnelRouteMode Mode { get; }

    public IReadOnlyList<RoutePrefix> AllowedIps { get; }

    public IReadOnlySet<IpFamily> BlockedFamilies { get; }

    public bool BlocksIpv6 => BlockedFamilies.Contains(IpFamily.Ipv6);
}

public enum RoutingPlanError
{
    MissingSplitRoutes,
    SplitRoutesContainDefaultRoute,
    ProfileHasNoTunnelAddress,
}

public sealed class RoutingPlanException : InvalidOperationException
{
    public RoutingPlanException(RoutingPlanError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public RoutingPlanError Error { get; }

    private static string MessageFor(RoutingPlanError error) => error switch
    {
        RoutingPlanError.MissingSplitRoutes => "Split routing requires at least one specific route.",
        RoutingPlanError.SplitRoutesContainDefaultRoute => "Default routes belong to Full routing.",
        RoutingPlanError.ProfileHasNoTunnelAddress => "Full routing requires at least one tunnel address.",
        _ => "The routing plan is invalid.",
    };
}

public static class RoutingPlanBuilder
{
    private static readonly IpFamily[] AllFamilies = [IpFamily.Ipv4, IpFamily.Ipv6];

    public static RoutingPlan MakePlan(TunnelRoutingPolicy policy, ProfileNetworkCapabilities capabilities)
    {
        if (policy.Mode == TunnelRouteMode.Split)
        {
            if (policy.SplitAllowedIps.Count == 0)
            {
                throw new RoutingPlanException(RoutingPlanError.MissingSplitRoutes);
            }

            if (policy.SplitAllowedIps.Any(route => route.IsDefaultRoute))
            {
                throw new RoutingPlanException(RoutingPlanError.SplitRoutesContainDefaultRoute);
            }

            return new RoutingPlan(TunnelRouteMode.Split, policy.SplitAllowedIps, []);
        }

        if (capabilities.SupportedFamilies.Count == 0)
        {
            throw new RoutingPlanException(RoutingPlanError.ProfileHasNoTunnelAddress);
        }

        var routedFamilies = AllFamilies.Where(capabilities.SupportedFamilies.Contains).ToArray();
        var blockedFamilies = AllFamilies.Where(family => !capabilities.SupportedFamilies.Contains(family));
        return new RoutingPlan(
            TunnelRouteMode.Full,
            routedFamilies.Select(RoutePrefix.DefaultFor),
            blockedFamilies);
    }
}
