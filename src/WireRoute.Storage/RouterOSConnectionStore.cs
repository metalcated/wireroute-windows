using System.Runtime.Versioning;
using System.Globalization;
using WireRoute.RouterOS;

namespace WireRoute.Storage;

public sealed record RouterOSStoredConnection(
    Guid Id,
    string Name,
    string Url,
    string Username,
    string Password,
    string? DefaultInterface = null);

[SupportedOSPlatform("windows")]
public sealed class RouterOSConnectionStore
{
    private readonly ProtectedJsonFile<List<RouterOSStoredConnection>> connections;

    public RouterOSConnectionStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        connections = new ProtectedJsonFile<List<RouterOSStoredConnection>>(
            Path.Combine(directory, "routeros-connections.dpapi"),
            () => []);
    }

    public async Task<IReadOnlyList<RouterOSStoredConnection>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        (await connections.ReadAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    public async Task SaveAsync(
        RouterOSStoredConnection connection,
        CancellationToken cancellationToken = default)
    {
        Validate(connection);
        _ = await connections.UpdateAsync(current =>
        {
            var duplicate = current.FirstOrDefault(value =>
                value.Id != connection.Id
                && CultureInfo.CurrentCulture.CompareInfo.Compare(
                    value.Name,
                    connection.Name,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    "Use a unique name for each RouterOS connection.",
                    nameof(connection));
            }

            var updated = current.ToList();
            var index = updated.FindIndex(value => value.Id == connection.Id);
            if (index >= 0)
            {
                updated[index] = connection;
            }
            else
            {
                updated.Add(connection);
            }

            return updated;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _ = await connections.UpdateAsync(
            current => current.Where(value => value.Id != id).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(RouterOSStoredConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Id == Guid.Empty)
        {
            throw new ArgumentException("A connection identifier is required.", nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(connection.Name))
        {
            throw new ArgumentException("Enter a name for this connection.", nameof(connection));
        }

        if (!Uri.TryCreate(connection.Url, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(url.Host))
        {
            throw new ArgumentException(
                "Enter a complete secure RouterOS address beginning with https://.",
                nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(connection.Username))
        {
            throw new ArgumentException("Enter the RouterOS username.", nameof(connection));
        }

        if (string.IsNullOrEmpty(connection.Password))
        {
            throw new ArgumentException("Enter the RouterOS password.", nameof(connection));
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class RouterOSCertificateStore
{
    private readonly ProtectedJsonFile<List<StoredCertificate>> certificates;

    public RouterOSCertificateStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        certificates = new ProtectedJsonFile<List<StoredCertificate>>(
            Path.Combine(directory, "routeros-certificates.dpapi"),
            () => []);
    }

    public async Task<RouterOSServerCertificate?> LoadAsync(
        Uri routerUrl,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint(routerUrl);
        var stored = (await certificates.ReadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(value =>
                value.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
                && value.Port == endpoint.Port);
        if (stored is null)
        {
            return null;
        }

        try
        {
            return new RouterOSServerCertificate(
                stored.Host,
                stored.Port,
                Convert.FromBase64String(stored.DerEncodedCertificate));
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new WireRouteStorageException(
                "The trusted RouterOS certificate is unavailable in protected storage.",
                exception);
        }
    }

    public async Task SaveAsync(
        RouterOSServerCertificate certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var stored = new StoredCertificate(
            certificate.Host,
            certificate.Port,
            Convert.ToBase64String(certificate.DerEncodedCertificate.Span));
        _ = await certificates.UpdateAsync(current =>
        {
            var updated = current.Where(value =>
                !value.Host.Equals(stored.Host, StringComparison.OrdinalIgnoreCase)
                || value.Port != stored.Port).ToList();
            updated.Add(stored);
            return updated;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (string Host, int Port) Endpoint(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || string.IsNullOrWhiteSpace(url.Host))
        {
            throw new ArgumentException("A complete RouterOS URL is required.", nameof(url));
        }

        return (url.Host.ToLowerInvariant(), url.IsDefaultPort ? 443 : url.Port);
    }

    private sealed record StoredCertificate(string Host, int Port, string DerEncodedCertificate);
}

internal static class WireRouteStoragePaths
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireRoute");
}
