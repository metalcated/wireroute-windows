using WireRoute.Core.Routing;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class RoutingPlanTests
{
    [TestMethod]
    public void SplitPlanPreservesConfiguredRoutes()
    {
        var routes = new[]
        {
            new RoutePrefix("10.20.0.0/16"),
            new RoutePrefix("2001:db8:20::/48"),
        };
        var policy = new TunnelRoutingPolicy(routes);
        var capabilities = new ProfileNetworkCapabilities([IpFamily.Ipv4, IpFamily.Ipv6]);

        var plan = RoutingPlanBuilder.MakePlan(policy, capabilities);

        CollectionAssert.AreEqual(routes, plan.AllowedIps.ToArray());
        Assert.AreEqual(0, plan.BlockedFamilies.Count);
    }

    [TestMethod]
    public void SplitPlanRequiresSpecificRoutes()
    {
        var importedRoutes = new[] { new RoutePrefix("0.0.0.0/0"), new RoutePrefix("::/0") };
        var policy = new TunnelRoutingPolicy(TunnelRoutingPolicy.SuggestedSplitAllowedIps(importedRoutes));

        var exception = Assert.ThrowsExactly<RoutingPlanException>(() =>
            RoutingPlanBuilder.MakePlan(
                policy,
                new ProfileNetworkCapabilities([IpFamily.Ipv4, IpFamily.Ipv6])));

        Assert.AreEqual(RoutingPlanError.MissingSplitRoutes, exception.Error);
    }

    [TestMethod]
    public void FullPlanRoutesBothFamilies()
    {
        var policy = new TunnelRoutingPolicy([], TunnelRouteMode.Full);

        var plan = RoutingPlanBuilder.MakePlan(
            policy,
            new ProfileNetworkCapabilities([IpFamily.Ipv4, IpFamily.Ipv6]));

        CollectionAssert.AreEqual(
            new[] { "0.0.0.0/0", "::/0" },
            plan.AllowedIps.Select(route => route.Notation).ToArray());
        Assert.AreEqual(0, plan.BlockedFamilies.Count);
    }

    [TestMethod]
    public void FullPlanBlocksIpv6ForIpv4OnlyProfile()
    {
        var plan = RoutingPlanBuilder.MakePlan(
            new TunnelRoutingPolicy([], TunnelRouteMode.Full),
            new ProfileNetworkCapabilities([IpFamily.Ipv4]));

        CollectionAssert.AreEqual(
            new[] { "0.0.0.0/0" },
            plan.AllowedIps.Select(route => route.Notation).ToArray());
        CollectionAssert.AreEquivalent(new[] { IpFamily.Ipv6 }, plan.BlockedFamilies.ToArray());
        Assert.IsTrue(plan.BlocksIpv6);
    }

    [TestMethod]
    public void FullPlanBlocksIpv4ForIpv6OnlyProfile()
    {
        var plan = RoutingPlanBuilder.MakePlan(
            new TunnelRoutingPolicy([], TunnelRouteMode.Full),
            new ProfileNetworkCapabilities([IpFamily.Ipv6]));

        CollectionAssert.AreEqual(
            new[] { "::/0" },
            plan.AllowedIps.Select(route => route.Notation).ToArray());
        CollectionAssert.AreEquivalent(new[] { IpFamily.Ipv4 }, plan.BlockedFamilies.ToArray());
    }

    [TestMethod]
    public void FullPlanRejectsProfileWithoutTunnelAddresses()
    {
        var exception = Assert.ThrowsExactly<RoutingPlanException>(() =>
            RoutingPlanBuilder.MakePlan(
                new TunnelRoutingPolicy([], TunnelRouteMode.Full),
                new ProfileNetworkCapabilities(Array.Empty<IpFamily>())));

        Assert.AreEqual(RoutingPlanError.ProfileHasNoTunnelAddress, exception.Error);
    }

    [TestMethod]
    public void PolicyDeduplicatesRoutesWithoutReordering()
    {
        var first = new RoutePrefix("10.0.0.0/8");
        var second = new RoutePrefix("192.168.50.0/24");

        var policy = new TunnelRoutingPolicy([first, second, first]);

        CollectionAssert.AreEqual(new[] { first, second }, policy.SplitAllowedIps.ToArray());
    }

    [TestMethod]
    public void SplitPlanRejectsDefaultRoutes()
    {
        var exception = Assert.ThrowsExactly<RoutingPlanException>(() =>
            RoutingPlanBuilder.MakePlan(
                new TunnelRoutingPolicy([new RoutePrefix("0.0.0.0/0")]),
                new ProfileNetworkCapabilities([IpFamily.Ipv4])));

        Assert.AreEqual(RoutingPlanError.SplitRoutesContainDefaultRoute, exception.Error);
    }
}
