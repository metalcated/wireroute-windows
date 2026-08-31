using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class WireGuardConfigParserTests
{
    [TestMethod]
    public void FormatterRoundTripsSecretsRoutesDnsAndHooks()
    {
        var source = $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}
            Address = 10.20.0.2/32
            DNS = 10.20.0.1, corp.example
            MTU = 1380
            PostUp = echo ready

            [Peer]
            PublicKey = {{PublicKey}}
            PresharedKey = {{SecondPublicKey}}
            AllowedIPs = 10.20.0.0/24
            Endpoint = vpn.example:51820
            PersistentKeepalive = 25
            """;
        var parsed = WireGuardConfigParser.Parse(source, "Laptop");

        var formatted = WireGuardConfigFormatter.ToWgQuick(parsed);
        var reparsed = WireGuardConfigParser.Parse(formatted, "Laptop");

        Assert.AreEqual(parsed.Interface.Addresses.Single(), reparsed.Interface.Addresses.Single());
        Assert.AreEqual("10.20.0.1", reparsed.Interface.DnsServers.Single());
        Assert.AreEqual("corp.example", reparsed.Interface.DnsSearchDomains.Single());
        Assert.IsTrue(reparsed.Interface.HasHooks);
        Assert.IsTrue(reparsed.Peers.Single().HasPresharedKey);
        Assert.AreEqual("10.20.0.0/24", reparsed.Peers.Single().AllowedIps.Single().Notation);
        Assert.AreEqual("vpn.example:51820", reparsed.Peers.Single().Endpoint?.DisplayValue);
        Assert.AreEqual((ushort)25, reparsed.Peers.Single().PersistentKeepalive);
    }

    private static readonly string PrivateKey = Key(1);
    private static readonly string PublicKey = Key(33);
    private static readonly string SecondPublicKey = Key(65);
    private static readonly string PresharedKey = Key(97);

    [TestMethod]
    public void ParsesFullProfileAndBuildsRedactedNetworkSummary()
    {
        var profile = WireGuardConfigParser.Parse(
            $$"""
            # imported profile
            [Interface]
            PrivateKey = {{PrivateKey}}
            Address = 10.10.0.2/32, 2001:db8::2/128
            DNS = 10.10.0.1, corp.example
            ListenPort = 51820
            MTU = 1420

            [Peer]
            PublicKey = {{PublicKey}}
            PresharedKey = {{PresharedKey}}
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = [2001:db8::1]:51820
            PersistentKeepalive = 25
            """,
            "office");

        Assert.AreEqual("office", profile.Name);
        Assert.IsTrue(profile.Interface.HasPrivateKey);
        Assert.AreEqual((ushort)51820, profile.Interface.ListenPort);
        Assert.AreEqual((ushort)1420, profile.Interface.Mtu);
        Assert.AreEqual(TunnelRouteMode.Full, profile.DetectedRouteMode);
        Assert.AreEqual(2, profile.ImportedAllowedIps.Count);
        Assert.AreEqual(0, profile.SuggestedSplitAllowedIps.Count);
        Assert.AreEqual("[2001:db8::1]:51820", profile.Peers[0].Endpoint?.DisplayValue);
        Assert.IsTrue(profile.Peers[0].HasPresharedKey);
        Assert.AreEqual(DnsServerRoute.ThroughTunnel, profile.DnsRouteSummary.Servers[0].Route);
        CollectionAssert.AreEqual(new[] { "corp.example" }, profile.Interface.DnsSearchDomains.ToArray());
        Assert.AreEqual("office", profile.ToString());
        Assert.IsFalse(profile.ToString().Contains(PrivateKey, StringComparison.Ordinal));
        Assert.IsFalse(profile.ToString().Contains(PresharedKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParsesSplitProfileAndNormalizesBareAddresses()
    {
        var profile = WireGuardConfigParser.Parse(
            $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}
            Address = 10.10.0.2
            DNS = 9.9.9.9

            [Peer]
            PublicKey = {{PublicKey}}
            AllowedIPs = 10.0.0.0/8, 10.0.0.0/8
            Endpoint = vpn.example:443
            """,
            "split-profile");

        Assert.AreEqual(TunnelRouteMode.Split, profile.DetectedRouteMode);
        Assert.AreEqual("10.10.0.2/32", profile.Interface.Addresses[0].Notation);
        CollectionAssert.AreEqual(
            new[] { "10.0.0.0/8" },
            profile.SuggestedSplitAllowedIps.Select(route => route.Notation).ToArray());
        Assert.AreEqual(DnsServerRoute.OutsideTunnel, profile.DnsRouteSummary.Servers[0].Route);
    }

    [TestMethod]
    public void AcceptsWindowsHooksButOnlyExposesTheirPresence()
    {
        var profile = WireGuardConfigParser.Parse(
            $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}
            PreUp = command.exe --value # preserved as part of the command
            PostDown = cleanup.exe

            [Peer]
            PublicKey = {{PublicKey}}
            AllowedIPs = 192.168.0.0/16
            """,
            "hooks");

        Assert.IsTrue(profile.Interface.HasHooks);
    }

    [TestMethod]
    public void AccumulatesRepeatedListKeysAndRejectsRepeatedScalarKeys()
    {
        var profile = WireGuardConfigParser.Parse(
            $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}
            Address = 10.0.0.2/32
            Address = 2001:db8::2/128

            [Peer]
            PublicKey = {{PublicKey}}
            AllowedIPs = 10.0.0.0/8
            AllowedIPs = 2001:db8::/32
            """,
            "repeated-lists");

        Assert.AreEqual(2, profile.Interface.Addresses.Count);
        Assert.AreEqual(2, profile.Peers[0].AllowedIps.Count);

        var exception = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}
                MTU = 1420
                MTU = 1380
                """,
                "duplicate-mtu"));
        Assert.AreEqual(WireGuardConfigParseError.DuplicateKey, exception.Error);
    }

    [TestMethod]
    public void RejectsDuplicatePeerPublicKeys()
    {
        var exception = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}

                [Peer]
                PublicKey = {{PublicKey}}

                [Peer]
                PublicKey = {{PublicKey}}
                """,
                "duplicate-peer"));

        Assert.AreEqual(WireGuardConfigParseError.DuplicatePeerPublicKey, exception.Error);
    }

    [TestMethod]
    public void RejectsMissingAndInvalidKeysWithoutEchoingSecrets()
    {
        var missing = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse("[Interface]\nAddress = 10.0.0.2/32", "missing-key"));
        Assert.AreEqual(WireGuardConfigParseError.MissingPrivateKey, missing.Error);

        const string invalidSecret = "this-is-not-a-wireguard-key";
        var invalid = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $"[Interface]\nPrivateKey = {invalidSecret}",
                "invalid-key"));
        Assert.AreEqual(WireGuardConfigParseError.InvalidKey, invalid.Error);
        Assert.IsFalse(invalid.Message.Contains(invalidSecret, StringComparison.Ordinal));
        Assert.AreEqual("[redacted]", invalid.OffendingValue);
    }

    [TestMethod]
    public void RejectsInvalidNetworkAndEndpointValues()
    {
        var invalidRoute = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}

                [Peer]
                PublicKey = {{PublicKey}}
                AllowedIPs = not-a-route
                """,
                "bad-route"));
        Assert.AreEqual(WireGuardConfigParseError.InvalidAddress, invalidRoute.Error);

        var invalidEndpoint = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}

                [Peer]
                PublicKey = {{PublicKey}}
                Endpoint = 2001:db8::1:51820
                """,
                "bad-endpoint"));
        Assert.AreEqual(WireGuardConfigParseError.InvalidEndpoint, invalidEndpoint.Error);
    }

    [TestMethod]
    public void RejectsMissingPeerKeyAndZeroPeerKey()
    {
        var missing = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}

                [Peer]
                AllowedIPs = 10.0.0.0/8
                """,
                "missing-peer-key"));
        Assert.AreEqual(WireGuardConfigParseError.MissingPeerPublicKey, missing.Error);

        var zero = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $$"""
                [Interface]
                PrivateKey = {{PrivateKey}}

                [Peer]
                PublicKey = {{Convert.ToBase64String(new byte[32])}}
                """,
                "zero-peer-key"));
        Assert.AreEqual(WireGuardConfigParseError.MissingPeerPublicKey, zero.Error);
    }

    [TestMethod]
    public void EnforcesExistingWindowsProfileNameRules()
    {
        Assert.IsTrue(WireGuardConfigParser.IsValidProfileName("office-vpn_2"));
        Assert.IsFalse(WireGuardConfigParser.IsValidProfileName("office vpn"));
        Assert.IsFalse(WireGuardConfigParser.IsValidProfileName("CON"));
        Assert.IsFalse(WireGuardConfigParser.IsValidProfileName(new string('a', 33)));

        var exception = Assert.ThrowsExactly<WireGuardConfigParseException>(() =>
            WireGuardConfigParser.Parse(
                $"[Interface]\nPrivateKey = {PrivateKey}",
                "invalid name"));
        Assert.AreEqual(WireGuardConfigParseError.InvalidProfileName, exception.Error);
    }

    [TestMethod]
    public void ParsesMultipleDistinctPeers()
    {
        var profile = WireGuardConfigParser.Parse(
            $$"""
            [Interface]
            PrivateKey = {{PrivateKey}}

            [Peer]
            PublicKey = {{PublicKey}}
            AllowedIPs = 10.0.0.0/8

            [Peer]
            PublicKey = {{SecondPublicKey}}
            AllowedIPs = 192.168.0.0/16
            """,
            "two-peers");

        Assert.AreEqual(2, profile.Peers.Count);
        Assert.AreEqual(2, profile.ImportedAllowedIps.Count);
    }

    [TestMethod]
    public void ParsesTheFileBackedImportFixture()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-full.conf");
        var profile = WireGuardConfigParser.Parse(File.ReadAllText(fixturePath), "sample-full");

        Assert.AreEqual(TunnelRouteMode.Full, profile.DetectedRouteMode);
        Assert.AreEqual(2, profile.Interface.Addresses.Count);
        Assert.AreEqual(1, profile.Peers.Count);
        Assert.AreEqual("vpn.example.test:51820", profile.Peers[0].Endpoint?.DisplayValue);
    }

    private static string Key(int start) => Convert.ToBase64String(
        Enumerable.Range(start, 32).Select(value => unchecked((byte)value)).ToArray());
}
