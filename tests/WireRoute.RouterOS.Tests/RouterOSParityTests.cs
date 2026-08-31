using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WireRoute.Core.Profiles;
using WireRoute.RouterOS;

namespace WireRoute.RouterOS.Tests;

[TestClass]
public sealed class RouterOSParityTests
{
    private static readonly string ClientPublicKey = Key(1);
    private static readonly string ClientPrivateKey = Key(33);
    private static readonly string ServerPublicKey = Key(65);

    [TestMethod]
    [DataRow("Managed by WireRoute", true)]
    [DataRow(" managed BY wireroute ", true)]
    [DataRow("Site-to-site server", false)]
    [DataRow("Manually created peer", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void ManagedPeerVisibilityMatchesMacOs(string? comment, bool expected) =>
        Assert.AreEqual(expected, RouterOSPeerCreation.IsWireRouteManagedComment(comment));

    [TestMethod]
    public async Task DiscoveryUsesRouterOSRestEndpointsAndFlexibleValues()
    {
        var transport = new QueueTransport(
            Response("""
                [{
                  ".id":"*1",
                  "name":"wg-remote",
                  "mtu":"1420",
                  "listen-port":51820,
                  "public-key":"AQID",
                  "disabled":"false",
                  "running":true
                }]
                """),
            Response("""
                [{
                  ".id":"*2",
                  "interface":"wg-remote",
                  "name":"Laptop",
                  "comment":"Managed by WireRoute",
                  "public-key":"peer-key",
                  "allowed-address":"10.20.0.4/32, 10.30.0.0/16",
                  "last-handshake":"12s",
                  "rx":"2048",
                  "tx":4096,
                  "disabled":"no",
                  "dynamic":0,
                  "responder":"yes"
                }]
                """),
            Response("""
                [{
                  ".id":"*3",
                  "address":"198.51.100.2/24",
                  "interface":"ether1",
                  "disabled":"false",
                  "dynamic":"true",
                  "invalid":"false"
                }]
                """));
        var client = CreateClient(transport);

        var interfaces = await client.GetWireGuardInterfacesAsync();
        var peers = await client.GetWireGuardPeersAsync();
        var addresses = await client.GetIpAddressesAsync();

        Assert.AreEqual("wg-remote", interfaces.Single().Name);
        Assert.AreEqual(51820, interfaces.Single().ListenPort);
        Assert.IsTrue(interfaces.Single().IsRunning);
        CollectionAssert.AreEqual(
            new[] { "10.20.0.4/32", "10.30.0.0/16" },
            peers.Single().AllowedAddresses.ToArray());
        Assert.AreEqual(2048UL, peers.Single().ReceivedBytes);
        Assert.AreEqual(4096UL, peers.Single().TransmittedBytes);
        Assert.IsTrue(peers.Single().IsResponder);
        Assert.IsTrue(addresses.Single().IsDynamic);

        CollectionAssert.AreEqual(
            new[]
            {
                "https://router.example/rest/interface/wireguard",
                "https://router.example/rest/interface/wireguard/peers",
                "https://router.example/rest/ip/address",
            },
            transport.Requests.Select(request => request.Uri).ToArray());
        Assert.IsTrue(transport.Requests.All(request => request.Authorization == "Basic dXNlcjpwYXNz"));
    }

    [TestMethod]
    public async Task PeerCreationMatchesTheReleasedMacOsPayload()
    {
        var transport = new QueueTransport(Response("""
            {
              ".id":"*9",
              "interface":"wg-remote",
              "name":"Windows Laptop",
              "comment":"Managed by WireRoute",
              "public-key":"AQ==",
              "allowed-address":"10.20.0.9/32",
              "responder":"true"
            }
            """));
        var client = CreateClient(transport);
        var proposal = new RouterOSPeerCreation(
            "wg-remote",
            "Windows Laptop",
            RouterOSPeerCreation.WireRouteManagedComment,
            ClientPublicKey,
            "10.20.0.9/32",
            persistentKeepalive: 25);

        await client.CreateWireGuardPeerAsync(proposal);

        var request = transport.Requests.Single();
        Assert.AreEqual("PUT", request.Method);
        Assert.AreEqual("https://router.example/rest/interface/wireguard/peers", request.Uri);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.AreEqual("wg-remote", root.GetProperty("interface").GetString());
        Assert.AreEqual("Windows Laptop", root.GetProperty("name").GetString());
        Assert.AreEqual("Managed by WireRoute", root.GetProperty("comment").GetString());
        Assert.AreEqual(ClientPublicKey, root.GetProperty("public-key").GetString());
        Assert.AreEqual("10.20.0.9/32", root.GetProperty("allowed-address").GetString());
        Assert.AreEqual("25s", root.GetProperty("persistent-keepalive").GetString());
        Assert.AreEqual("true", root.GetProperty("responder").GetString());
    }

    [TestMethod]
    public async Task PeerCreationDistinguishesRejectedAndUncertainWrites()
    {
        var proposal = new RouterOSPeerCreation(
            "wg-remote",
            "Windows Laptop",
            RouterOSPeerCreation.WireRouteManagedComment,
            ClientPublicKey,
            "10.20.0.9/32");
        var rejectedClient = CreateClient(new QueueTransport(
            Response("""{"message":"failure","detail":"duplicate"}""", HttpStatusCode.BadRequest)));
        var rejected = await Assert.ThrowsExactlyAsync<RouterOSHttpException>(async () =>
            await rejectedClient.CreateWireGuardPeerAsync(proposal));
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);

        var uncertainClient = CreateClient(new QueueTransport(
            Response("""{"message":"failure"}""", HttpStatusCode.InternalServerError)));
        await Assert.ThrowsExactlyAsync<RouterOSWriteOutcomeUncertainException>(async () =>
            await uncertainClient.CreateWireGuardPeerAsync(proposal));
    }

    [TestMethod]
    public void ClientRequiresTheSameSecureBaseUrlRulesAsMacOs()
    {
        var transport = new QueueTransport();
        Assert.AreEqual(
            RouterOSClientError.InsecureTransport,
            Assert.ThrowsExactly<RouterOSClientException>(() =>
                new RouterOSClient(
                    new Uri("http://router.example"),
                    new RouterOSCredentials("user", "pass"),
                    transport)).Error);
        Assert.AreEqual(
            RouterOSClientError.InvalidBaseUrl,
            Assert.ThrowsExactly<RouterOSClientException>(() =>
                new RouterOSClient(
                    new Uri("https://router.example/custom"),
                    new RouterOSCredentials("user", "pass"),
                    transport)).Error);
        Assert.AreEqual(
            RouterOSClientError.InvalidUsername,
            Assert.ThrowsExactly<RouterOSClientException>(() =>
                new RouterOSClient(
                    new Uri("https://router.example"),
                    new RouterOSCredentials("bad:user", "pass"),
                    transport)).Error);

        _ = new RouterOSClient(
            new Uri("https://router.example/rest"),
            new RouterOSCredentials("user", "pass"),
            transport);
    }

    [TestMethod]
    public void GeneratedKeyPairIsCanonicalClampedX25519Material()
    {
        var pair = WireGuardKeyPair.Generate();
        var privateBytes = Convert.FromBase64String(pair.PrivateKey);
        var publicBytes = Convert.FromBase64String(pair.PublicKey);

        Assert.AreEqual(32, privateBytes.Length);
        Assert.AreEqual(32, publicBytes.Length);
        Assert.AreEqual(0, privateBytes[0] & 7);
        Assert.AreEqual(0, privateBytes[31] & 128);
        Assert.AreEqual(64, privateBytes[31] & 64);
        Assert.IsTrue(publicBytes.Any(value => value != 0));
    }

    [TestMethod]
    public void PublicKeyCanBeDerivedFromStoredPrivateKey()
    {
        var generated = WireGuardKeyPair.Generate();

        var derived = WireGuardKeyPair.FromPrivateKey(generated.PrivateKey);

        Assert.AreEqual(generated.PublicKey, derived.PublicKey);
        Assert.AreEqual(generated.PrivateKey, derived.PrivateKey);
    }

    [TestMethod]
    public void GeneratedClientConfigurationMatchesMacOsAndParsesAsWireGuard()
    {
        var configuration = new WireGuardClientConfiguration(
            "Windows Laptop",
            ClientPrivateKey,
            "10.20.0.9/32",
            ["10.20.0.1", "2001:db8::53"],
            ServerPublicKey,
            "2001:db8::1",
            51820,
            ["10.20.0.0/16", "2001:db8:1::/64"],
            25);

        var expected = $"""
            [Interface]
            PrivateKey = {ClientPrivateKey}
            Address = 10.20.0.9/32
            DNS = 10.20.0.1, 2001:db8::53

            [Peer]
            PublicKey = {ServerPublicKey}
            Endpoint = [2001:db8::1]:51820
            AllowedIPs = 10.20.0.0/16, 2001:db8:1::/64
            PersistentKeepalive = 25

            """;
        Assert.AreEqual(expected.ReplaceLineEndings("\n"), configuration.WgQuickConfiguration);

        var parsed = WireGuardConfigParser.Parse(configuration.WgQuickConfiguration, "Windows-Laptop");
        Assert.AreEqual("Windows-Laptop", parsed.Name);
        Assert.AreEqual(2, parsed.Peers.Single().AllowedIps.Count);
    }

    [TestMethod]
    public void PeerProposalRejectsDuplicateKeysAndOverlappingAddresses()
    {
        var existing = Peer("10.20.0.4/32", ClientPublicKey);
        Assert.AreEqual(
            RouterOSProvisioningError.DuplicatePublicKey,
            Assert.ThrowsExactly<RouterOSProvisioningException>(() => new RouterOSPeerCreation(
                "wg-remote",
                "Laptop",
                RouterOSPeerCreation.WireRouteManagedComment,
                ClientPublicKey,
                "10.20.0.5/32",
                existingPeers: [existing])).Error);

        Assert.AreEqual(
            RouterOSProvisioningError.OverlappingClientAddress,
            Assert.ThrowsExactly<RouterOSProvisioningException>(() => new RouterOSPeerCreation(
                "wg-remote",
                "Laptop",
                RouterOSPeerCreation.WireRouteManagedComment,
                ServerPublicKey,
                "10.20.0.4/32",
                existingPeers: [existing])).Error);
    }

    [TestMethod]
    public void AddressSuggestionsMatchMacOsGuardrails()
    {
        var peers = new[]
        {
            Peer("10.20.0.4/32", Key(2)),
            Peer("10.20.0.5/32", Key(3)),
            Peer("10.30.0.8/32", Key(4), interfaceName: "other"),
        };
        var clientAddress = RouterOSClientAddressSuggestion.Discover("wg-remote", peers);
        Assert.AreEqual("10.20.0.6/32", clientAddress?.Address.Notation);
        Assert.AreEqual(2, clientAddress?.SourceAddressCount);

        Assert.IsNull(RouterOSPublicEndpointSuggestion.Discover([
            IpAddress("10.0.0.1/24"),
            IpAddress("198.51.100.2/24"),
        ]));
        Assert.AreEqual(
            "8.8.8.8",
            RouterOSPublicEndpointSuggestion.Discover([
                IpAddress("10.0.0.1/24"),
                IpAddress("8.8.8.8/32"),
            ])?.Address);
    }

    [TestMethod]
    public void CertificateFingerprintAndMatchingUseExactDerHostAndPort()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=router.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var der = certificate.Export(X509ContentType.Cert);
        var model = new RouterOSServerCertificate("ROUTER.EXAMPLE", 443, der);

        Assert.AreEqual("router.example", model.Host);
        Assert.AreEqual(
            string.Join(":", SHA256.HashData(der).Select(value => value.ToString("X2"))),
            model.FingerprintSha256);
        Assert.IsTrue(model.Matches("router.example", 443, der));
        Assert.IsFalse(model.Matches("router.example", 8443, der));
    }

    [TestMethod]
    public void CertificateValidatorPinsExactDerAndReportsChanges()
    {
        using var first = CreateCertificate("router.example");
        using var second = CreateCertificate("router.example");
        var firstDer = first.Export(X509ContentType.Cert);
        var trusted = new RouterOSServerCertificate("router.example", 443, firstDer);

        var untrustedValidator = new RouterOSCertificateValidator("router.example", 443, null);
        Assert.IsFalse(untrustedValidator.Validate(
            this,
            first,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsInstanceOfType<RouterOSUntrustedCertificateException>(
            untrustedValidator.CertificateFailure());

        var pinnedValidator = new RouterOSCertificateValidator("router.example", 443, trusted);
        Assert.IsTrue(pinnedValidator.Validate(
            this,
            first,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));

        var changedValidator = new RouterOSCertificateValidator("router.example", 443, trusted);
        Assert.IsFalse(changedValidator.Validate(
            this,
            second,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        var changed = changedValidator.CertificateFailure();
        Assert.IsInstanceOfType<RouterOSChangedCertificateException>(changed);
        Assert.AreEqual(
            ((RouterOSChangedCertificateException)changed).ExpectedFingerprint, trusted.FingerprintSha256);
    }

    private static RouterOSClient CreateClient(IRouterOSHttpTransport transport) => new(
        new Uri("https://router.example"),
        new RouterOSCredentials("user", "pass"),
        transport);

    private static RouterOSHttpResponse Response(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode, Encoding.UTF8.GetBytes(body));

    private static string Key(int first) => Convert.ToBase64String(
        Enumerable.Range(first, 32).Select(value => (byte)value).ToArray());

    private static RouterOSWireGuardPeer Peer(
        string allowedAddress,
        string publicKey,
        string interfaceName = "wg-remote") => new(
            "*1",
            interfaceName,
            "Existing",
            RouterOSPeerCreation.WireRouteManagedComment,
            publicKey,
            [allowedAddress],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            true);

    private static RouterOSIpAddress IpAddress(string address) => new(
        "*1",
        address,
        null,
        "ether1",
        null,
        false,
        false,
        false);

    private static X509Certificate2 CreateCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class QueueTransport(params RouterOSHttpResponse[] responses) : IRouterOSHttpTransport
    {
        private readonly Queue<RouterOSHttpResponse> responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        public async Task<RouterOSHttpResponse> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Method, string Uri, string? Authorization, string? Body);
}
