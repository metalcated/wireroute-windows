using System.Globalization;
using System.Runtime.Versioning;

namespace WireRoute.Storage;

public enum StoredTunnelRouteMode
{
    Split,
    Full,
}

public enum StoredDnsProtectionMode
{
    Profile,
    Encrypted,
}

public sealed record WireRouteStoredProfile(
    Guid Id,
    string Name,
    string Configuration,
    StoredTunnelRouteMode RouteMode,
    IReadOnlyList<string> SplitRoutes,
    StoredDnsProtectionMode DnsProtectionMode,
    string? DnsProvider,
    string? DnsResolverUrl,
    IReadOnlyList<string> DnsBootstrapAddresses,
    bool OnDemandEthernet,
    bool OnDemandWiFi,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

[SupportedOSPlatform("windows")]
public sealed class WireGuardProfileStore
{
    private readonly ProtectedJsonFile<List<WireRouteStoredProfile>> profiles;

    public WireGuardProfileStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        profiles = new ProtectedJsonFile<List<WireRouteStoredProfile>>(
            Path.Combine(directory, "wireguard-profiles.dpapi"),
            () => []);
    }

    public async Task<IReadOnlyList<WireRouteStoredProfile>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        (await profiles.ReadAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(profile => profile.CreatedAt)
            .ToArray();

    public async Task SaveAsync(
        WireRouteStoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        Validate(profile);
        _ = await profiles.UpdateAsync(current =>
        {
            var duplicate = current.FirstOrDefault(value =>
                value.Id != profile.Id
                && CultureInfo.CurrentCulture.CompareInfo.Compare(
                    value.Name,
                    profile.Name,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    "Use a unique name for each WireGuard profile.",
                    nameof(profile));
            }

            var updated = current.ToList();
            var index = updated.FindIndex(value => value.Id == profile.Id);
            if (index >= 0)
            {
                updated[index] = profile;
            }
            else
            {
                updated.Add(profile);
            }

            return updated;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A profile identifier is required.", nameof(id));
        }

        _ = await profiles.UpdateAsync(
            current => current.Where(value => value.Id != id).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(WireRouteStoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty)
        {
            throw new ArgumentException("A profile identifier is required.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Enter a profile name.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Configuration))
        {
            throw new ArgumentException("A WireGuard configuration is required.", nameof(profile));
        }

        if (profile.SplitRoutes is null || profile.DnsBootstrapAddresses is null)
        {
            throw new ArgumentException("Profile route and DNS collections are required.", nameof(profile));
        }

        if (profile.DnsProtectionMode == StoredDnsProtectionMode.Encrypted
            && string.IsNullOrWhiteSpace(profile.DnsResolverUrl))
        {
            throw new ArgumentException(
                "An encrypted DNS resolver URL is required.",
                nameof(profile));
        }

        if (profile.CreatedAt == default || profile.UpdatedAt == default)
        {
            throw new ArgumentException("Profile timestamps are required.", nameof(profile));
        }
    }
}
