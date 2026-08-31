using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.Storage.Tests;

[TestClass]
public sealed class ProtectedRouterOSStorageTests
{
    [TestMethod]
    public void LegacySettingsDefaultToServiceFreeMode()
    {
        const string json = """
            {
              "Theme": "Blue Nordic",
              "TrayIconStyle": "Default",
              "PreferredEndpoint": "",
              "DnsServers": "",
              "SplitTunnelRoutes": "",
              "PersistentKeepalive": 25
            }
            """;

        var settings = JsonSerializer.Deserialize<WireRouteAppSettings>(json);

        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.PersistentTunnelService);
    }

    [TestMethod]
    public async Task SettingsStoreRoundTripsPeerDefaults()
    {
        var directory = NewStorageDirectory();
        try
        {
            var store = new WireRouteSettingsStore(directory);
            var settings = new WireRouteAppSettings(
                "Blue Nordic",
                "WireRoute Color",
                "vpn.example.com",
                "192.0.2.53, 192.0.2.54",
                "10.20.0.0/16",
                30,
                true);

            await store.SaveAsync(settings);

            Assert.AreEqual(settings, await store.LoadAsync());
            var protectedText = Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(directory, "settings.dpapi")));
            Assert.IsFalse(protectedText.Contains("vpn.example.com", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProfileStoreProtectsAndRoundTripsConfiguration()
    {
        var directory = NewStorageDirectory();
        try
        {
            var store = new WireGuardProfileStore(directory);
            var now = DateTimeOffset.UtcNow;
            var profile = new WireRouteStoredProfile(
                Guid.NewGuid(),
                "Laptop",
                "[Interface]\nPrivateKey = sensitive-material",
                StoredTunnelRouteMode.Split,
                new[] { "192.168.50.0/24" },
                StoredDnsProtectionMode.Profile,
                null,
                null,
                Array.Empty<string>(),
                false,
                false,
                now,
                now);

            await store.SaveAsync(profile);

            var loaded = await store.LoadAllAsync();
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(profile.Id, loaded[0].Id);
            Assert.AreEqual(profile.Name, loaded[0].Name);
            Assert.AreEqual("Laptop", loaded[0].TunnelName);
            Assert.AreEqual("Laptop", loaded[0].ServiceName);
            Assert.AreEqual(profile.Configuration, loaded[0].Configuration);
            CollectionAssert.AreEqual(profile.SplitRoutes.ToArray(), loaded[0].SplitRoutes.ToArray());
            var protectedBytes = await File.ReadAllBytesAsync(
                Path.Combine(directory, "wireguard-profiles.dpapi"));
            var protectedText = System.Text.Encoding.UTF8.GetString(protectedBytes);
            Assert.IsFalse(protectedText.Contains("sensitive-material", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProfileStoreSeparatesFriendlyNameFromStableTunnelName()
    {
        var directory = NewStorageDirectory();
        try
        {
            var store = new WireGuardProfileStore(directory);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.Parse("dff129a9-1da4-4ae9-bfa3-f38846ca1b85");
            var profile = new WireRouteStoredProfile(
                id,
                "iPhone 12 Dev",
                "[Interface]\nPrivateKey = sensitive-material",
                StoredTunnelRouteMode.Full,
                Array.Empty<string>(),
                StoredDnsProtectionMode.Profile,
                null,
                null,
                Array.Empty<string>(),
                false,
                false,
                now,
                now);

            Assert.AreEqual("iPhone-12-Dev-dff129a91da4", profile.TunnelName);
            await store.SaveAsync(profile);
            var loaded = (await store.LoadAllAsync()).Single();
            Assert.AreEqual("iPhone 12 Dev", loaded.Name);
            Assert.AreEqual("iPhone-12-Dev-dff129a91da4", loaded.TunnelName);

            var renamed = loaded with { Name = "Travel Phone" };
            await store.SaveAsync(renamed);
            Assert.AreEqual(
                "iPhone-12-Dev-dff129a91da4",
                (await store.LoadAllAsync()).Single().TunnelName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ProfileDisplayNameComparisonMatchesProtectedStoreUniqueness()
    {
        Assert.IsTrue(WireRouteStoredProfile.DisplayNamesEqual("Café", "CAFE"));
        Assert.IsFalse(WireRouteStoredProfile.DisplayNamesEqual("Phone", "Laptop"));
    }

    [TestMethod]
    public async Task ActivityStoreProtectsAndFiltersProfileHistory()
    {
        var directory = NewStorageDirectory();
        try
        {
            var store = new WireRouteActivityStore(directory);
            var profileId = Guid.NewGuid();
            var otherProfileId = Guid.NewGuid();
            var first = new WireRouteActivityEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddSeconds(-1),
                WireRouteActivityKind.ProfileActivated,
                profileId,
                "Laptop",
                "Activated Laptop.");
            var second = new WireRouteActivityEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                WireRouteActivityKind.TunnelError,
                otherProfileId,
                "Phone",
                "Tunnel failure details that remain protected.");

            await store.AppendAsync(first);
            await store.AppendAsync(second);

            var all = await store.LoadAsync();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual(second.Id, all[0].Id);
            var profileHistory = await store.LoadAsync(profileId);
            Assert.AreEqual(1, profileHistory.Count);
            Assert.AreEqual(first.Id, profileHistory[0].Id);
            var protectedText = Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(Path.Combine(directory, "activity.dpapi")));
            Assert.IsFalse(protectedText.Contains("Tunnel failure details", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DpapiRoundTripUsesNonInteractiveCurrentUserProtection()
    {
        var plaintext = Encoding.UTF8.GetBytes("router-password-that-must-not-be-plaintext");

        var ciphertext = WindowsDpapi.Protect(plaintext);
        var recovered = WindowsDpapi.Unprotect(ciphertext);

        CollectionAssert.AreEqual(plaintext, recovered);
        Assert.IsFalse(ciphertext.AsSpan().IndexOf(plaintext) >= 0);
    }

    [TestMethod]
    public async Task ConnectionStoreAddsUpdatesAndRemovesNamedConnections()
    {
        var directory = NewStorageDirectory();
        var store = new RouterOSConnectionStore(directory);
        var id = Guid.NewGuid();
        var first = new RouterOSStoredConnection(
            id,
            "Home Router",
            "https://router.example",
            "wire-route",
            "correct horse battery staple",
            "wg-remote");

        await store.SaveAsync(first);
        var loaded = (await store.LoadAllAsync()).Single();
        Assert.AreEqual(first, loaded);

        var updated = first with { Username = "wire-route-read-write", DefaultInterface = "wg-clients" };
        await store.SaveAsync(updated);
        loaded = (await store.LoadAllAsync()).Single();
        Assert.AreEqual(updated, loaded);

        var protectedBytes = await File.ReadAllBytesAsync(
            Path.Combine(directory, "routeros-connections.dpapi"));
        var renderedCiphertext = Encoding.UTF8.GetString(protectedBytes);
        Assert.IsFalse(renderedCiphertext.Contains(first.Password, StringComparison.Ordinal));
        Assert.IsFalse(renderedCiphertext.Contains(first.Url, StringComparison.Ordinal));

        await store.DeleteAsync(id);
        Assert.AreEqual(0, (await store.LoadAllAsync()).Count);
    }

    [TestMethod]
    public async Task ConnectionStoreRejectsDuplicateDisplayNamesLikeMacOs()
    {
        var store = new RouterOSConnectionStore(NewStorageDirectory());
        await store.SaveAsync(Connection("Café"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.SaveAsync(Connection("CAFE")));
    }

    [TestMethod]
    public async Task ConnectionStoreRejectsInsecureRouterUrls()
    {
        var store = new RouterOSConnectionStore(NewStorageDirectory());
        var connection = Connection("Lab") with { Url = "http://router.example" };

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.SaveAsync(connection));
    }

    [TestMethod]
    public async Task ProfileRecoveryStoreProtectsAndUpdatesGeneratedConfiguration()
    {
        var directory = NewStorageDirectory();
        var store = new RouterOSProfileRecoveryStore(directory);
        var recovery = new RouterOSProfileRecovery(
            Guid.NewGuid(),
            "Laptop",
            "[Interface]\nPrivateKey = private-material",
            DateTimeOffset.UtcNow,
            RouterOSProfileRecoveryReason.PendingRouterWrite);

        await store.SaveAsync(recovery);
        Assert.AreEqual(recovery, (await store.LoadAllAsync()).Single());
        var protectedBytes = await File.ReadAllBytesAsync(
            Path.Combine(directory, "routeros-profile-recovery.dpapi"));
        Assert.IsFalse(Encoding.UTF8.GetString(protectedBytes).Contains("private-material", StringComparison.Ordinal));

        var updated = recovery with { Reason = RouterOSProfileRecoveryReason.RouterWriteUncertain };
        await store.SaveAsync(updated);
        Assert.AreEqual(updated, (await store.LoadAllAsync()).Single());

        await store.DeleteAsync(recovery.Id);
        Assert.AreEqual(0, (await store.LoadAllAsync()).Count);
    }

    [TestMethod]
    public async Task CertificateStorePinsExactDerToHostAndPort()
    {
        var store = new RouterOSCertificateStore(NewStorageDirectory());
        using var first = CreateCertificate("router.example");
        using var replacement = CreateCertificate("router.example");
        var firstModel = new RouterOSServerCertificate(
            "router.example",
            8443,
            first.Export(X509ContentType.Cert));
        var replacementModel = new RouterOSServerCertificate(
            "router.example",
            8443,
            replacement.Export(X509ContentType.Cert));

        Assert.IsNull(await store.LoadAsync(new Uri("https://router.example:8443")));
        await store.SaveAsync(firstModel);
        var loaded = await store.LoadAsync(new Uri("https://router.example:8443/rest"));
        Assert.AreEqual(firstModel, loaded);
        Assert.IsNull(await store.LoadAsync(new Uri("https://router.example")));

        await store.SaveAsync(replacementModel);
        loaded = await store.LoadAsync(new Uri("https://router.example:8443"));
        Assert.AreEqual(replacementModel, loaded);
    }

    private static RouterOSStoredConnection Connection(string name) => new(
        Guid.NewGuid(),
        name,
        "https://router.example",
        "wire-route",
        "password",
        null);

    private static string NewStorageDirectory() => Path.Combine(
        Path.GetTempPath(),
        "WireRoute.Storage.Tests",
        Guid.NewGuid().ToString("N"));

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
}
