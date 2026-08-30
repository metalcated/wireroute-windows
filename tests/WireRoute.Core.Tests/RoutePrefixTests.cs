using System.Text.Json;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class RoutePrefixTests
{
    [TestMethod]
    public void ParsesIpv4AndIpv6Prefixes()
    {
        var ipv4 = new RoutePrefix(" 192.0.2.4/24 ");
        var ipv6 = new RoutePrefix("2001:db8::4/64");

        Assert.AreEqual(IpFamily.Ipv4, ipv4.Family);
        Assert.AreEqual("192.0.2.4/24", ipv4.Notation);
        Assert.AreEqual(IpFamily.Ipv6, ipv6.Family);
        Assert.AreEqual("2001:db8::4/64", ipv6.Notation);
    }

    [TestMethod]
    public void RejectsInvalidAddressesAndPrefixLengths()
    {
        Assert.ThrowsExactly<RoutePrefixValidationException>(() => new RoutePrefix("not-an-address/24"));
        Assert.ThrowsExactly<RoutePrefixValidationException>(() => new RoutePrefix("192.0.2.1/33"));
        Assert.ThrowsExactly<RoutePrefixValidationException>(() => new RoutePrefix("2001:db8::1/129"));
        Assert.ThrowsExactly<RoutePrefixValidationException>(() => new RoutePrefix("192.0.2.1"));
    }

    [TestMethod]
    public void JsonRoundTripUsesStandardNotation()
    {
        var route = new RoutePrefix("2001:db8::10/64");
        var encoded = JsonSerializer.Serialize(route);
        var decoded = JsonSerializer.Deserialize<RoutePrefix>(encoded);

        Assert.AreEqual(route, decoded);
        Assert.AreEqual("\"2001:db8::10/64\"", encoded);
    }

    [TestMethod]
    public void ParsesRouteListAndPreservesFirstOccurrenceOrder()
    {
        var routes = RoutePrefix.ParseList(
            "192.168.0.0/16, 10.0.0.0/8\n192.168.0.0/16;2001:db8::/32");

        CollectionAssert.AreEqual(
            new[] { "192.168.0.0/16", "10.0.0.0/8", "2001:db8::/32" },
            routes.Select(route => route.Notation).ToArray());
    }

    [TestMethod]
    public void EmptyRouteListParsesAsEmpty()
    {
        Assert.AreEqual(0, RoutePrefix.ParseList("  \n, ; ").Count);
    }

    [TestMethod]
    public void DetectsIpv4AndIpv6RouteCoverage()
    {
        var privateIpv4 = new RoutePrefix("192.168.80.0/24");
        var documentationIpv6 = new RoutePrefix("2001:db8::/32");

        Assert.IsTrue(privateIpv4.Contains("192.168.80.45"));
        Assert.IsFalse(privateIpv4.Contains("192.168.81.45"));
        Assert.IsFalse(privateIpv4.Contains("2001:db8::53"));
        Assert.IsTrue(documentationIpv6.Contains("2001:db8:1::53"));
        Assert.IsFalse(documentationIpv6.Contains("2001:db9::53"));
    }

    [TestMethod]
    public void DefaultRoutesCoverOnlyTheirAddressFamily()
    {
        var ipv4Default = new RoutePrefix("0.0.0.0/0");
        var ipv6Default = new RoutePrefix("::/0");

        Assert.IsTrue(ipv4Default.Contains("203.0.113.53"));
        Assert.IsFalse(ipv4Default.Contains("2001:db8::53"));
        Assert.IsTrue(ipv6Default.Contains("2001:db8::53"));
        Assert.IsFalse(ipv6Default.Contains("203.0.113.53"));
    }

    [TestMethod]
    public void ProfileDnsSummaryClassifiesServerRoutes()
    {
        var summary = new ProfileDnsRouteSummary(
            ["192.168.80.45", "9.9.9.9", "2001:db8::53"],
            ["internal.example"],
            [new RoutePrefix("192.168.80.0/24"), new RoutePrefix("2001:db8::/32")]);

        CollectionAssert.AreEqual(
            new[] { DnsServerRoute.ThroughTunnel, DnsServerRoute.OutsideTunnel, DnsServerRoute.ThroughTunnel },
            summary.Servers.Select(server => server.Route).ToArray());
        CollectionAssert.AreEqual(new[] { "internal.example" }, summary.SearchDomains.ToArray());
    }
}
