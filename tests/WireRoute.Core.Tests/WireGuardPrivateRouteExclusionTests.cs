using WireRoute.Core.Profiles;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class WireGuardPrivateRouteExclusionTests
{
    private static readonly string PrivateKey = Key(1);
    private static readonly string PublicKey = Key(33);

    [TestMethod]
    public void OffersControlOnlyForSinglePeerDefaultOrExcludedRoutes()
    {
        var full = Parse("0.0.0.0/0, ::/0");
        Assert.AreEqual(new(true, false), WireGuardPrivateRouteExclusion.Evaluate(full));

        var excluded = WireGuardConfigParser.Parse(
            WireGuardPrivateRouteExclusion.SetEnabled(full, true),
            "profile");
        Assert.AreEqual(new(true, true), WireGuardPrivateRouteExclusion.Evaluate(excluded));

        Assert.AreEqual(new(false, false), WireGuardPrivateRouteExclusion.Evaluate(Parse("10.0.0.0/8")));
        Assert.AreEqual(
            new(false, false),
            WireGuardPrivateRouteExclusion.Evaluate(Parse("0.0.0.0/0", includeSecondPeer: true)));
    }

    [TestMethod]
    public void EnablingMatchesMacRouteSetAndPreservesIpv6()
    {
        var profile = Parse("0.0.0.0/0, 2001:db8:1::7/64", "192.168.80.45, 2001:db8::53");
        var updated = WireGuardConfigParser.Parse(
            WireGuardPrivateRouteExclusion.SetEnabled(profile, true),
            "profile");
        var allowed = updated.Peers.Single().AllowedIps.Select(route => route.Notation).ToArray();

        Assert.AreEqual("2001:db8:1::/64", allowed[0]);
        Assert.AreEqual("1.0.0.0/8", allowed[1]);
        Assert.Contains("208.0.0.0/4", allowed);
        Assert.Contains("192.168.80.45/32", allowed);
        Assert.Contains("2001:db8::53/128", allowed);
        Assert.DoesNotContain("0.0.0.0/0", allowed);
    }

    [TestMethod]
    public void DisablingRemovesInjectedIpv6DnsAndRestoresDefaultRoute()
    {
        var full = Parse("0.0.0.0/0, 2001:db8:1::/64", "2001:db8::53");
        var excluded = WireGuardConfigParser.Parse(
            WireGuardPrivateRouteExclusion.SetEnabled(full, true),
            "profile");
        var restored = WireGuardConfigParser.Parse(
            WireGuardPrivateRouteExclusion.SetEnabled(excluded, false, new[] { "2001:db8::53" }),
            "profile");
        var allowed = restored.Peers.Single().AllowedIps.Select(route => route.Notation).ToArray();

        CollectionAssert.AreEqual(new[] { "2001:db8:1::/64", "0.0.0.0/0" }, allowed);
    }

    [TestMethod]
    public void RefreshingDnsRoutesReplacesPreviouslyInjectedServers()
    {
        var full = Parse("0.0.0.0/0", "192.168.80.45");
        var excludedText = WireGuardPrivateRouteExclusion.SetEnabled(full, true);
        var changedDnsText = excludedText.Replace(
            "DNS = 192.168.80.45",
            "DNS = 192.168.80.46",
            StringComparison.Ordinal);
        var changedDns = WireGuardConfigParser.Parse(changedDnsText, "profile");
        var refreshed = WireGuardConfigParser.Parse(
            WireGuardPrivateRouteExclusion.RefreshDnsRoutes(changedDns, new[] { "192.168.80.45" }),
            "profile");
        var allowed = refreshed.Peers.Single().AllowedIps.Select(route => route.Notation).ToArray();

        Assert.DoesNotContain("192.168.80.45/32", allowed);
        Assert.Contains("192.168.80.46/32", allowed);
    }

    private static WireGuardProfile Parse(
        string allowedIps,
        string? dns = null,
        bool includeSecondPeer = false) =>
        WireGuardConfigParser.Parse(
            $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}
            {{(dns is null ? string.Empty : $"DNS = {dns}")}}

            [Peer]
            PublicKey = {{PublicKey}}
            AllowedIPs = {{allowedIps}}
            {{(includeSecondPeer ? $"[Peer]{Environment.NewLine}PublicKey = {Key(65)}{Environment.NewLine}AllowedIPs = 10.0.0.0/8" : string.Empty)}}
            """,
            "profile");

    private static string Key(int start) => Convert.ToBase64String(
        Enumerable.Range(start, 32).Select(value => unchecked((byte)value)).ToArray());
}
