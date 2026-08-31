using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.Storage.Tests;

[TestClass]
public sealed class ProtectedRouterOSStorageTests
{
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
