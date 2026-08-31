using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Serialization;
using WireRoute.Core.Profiles;

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
    DateTimeOffset UpdatedAt)
{
    public string? ServiceName { get; init; }

    [JsonIgnore]
    public string TunnelName =>
        WireGuardConfigParser.IsValidProfileName(ServiceName ?? string.Empty)
            ? ServiceName!
            : CreateTunnelName(Name, Id);

    public static string CreateTunnelName(string displayName, Guid id)
    {
        if (WireGuardConfigParser.IsValidProfileName(displayName))
        {
            return displayName;
        }

        var rendered = new StringBuilder();
        foreach (var character in displayName.Normalize(NormalizationForm.FormKC))
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '=' or '+' or '.' or '-';
            if (allowed)
            {
                rendered.Append(character);
            }
            else if (rendered.Length == 0 || rendered[^1] != '-')
            {
                rendered.Append('-');
            }
        }

        var stem = rendered.ToString().Trim('-', '.');
        if (stem.Length == 0)
        {
            stem = "WireRoute";
        }
        const int suffixLength = 12;
        var suffix = id.ToString("N")[..suffixLength];
        var maximumStemLength = 32 - suffixLength - 1;
        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength].TrimEnd('-', '.');
        }
        if (stem.Length == 0)
        {
            stem = "WireRoute";
        }

        var candidate = stem + "-" + suffix;
        return WireGuardConfigParser.IsValidProfileName(candidate)
            ? candidate
            : "WireRoute-" + suffix;
    }
}

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
        profile = profile with { ServiceName = profile.TunnelName };
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

        if (!WireGuardConfigParser.IsValidProfileName(profile.TunnelName))
        {
            throw new ArgumentException("The internal WireGuard tunnel name is invalid.", nameof(profile));
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
