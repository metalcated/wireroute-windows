using System.Runtime.Versioning;

namespace WireRoute.Storage;

public enum WireRouteActivityKind
{
    AppStarted,
    ProfileImported,
    ProfileCreated,
    ProfileUpdated,
    ProfileDeleted,
    ProfileActivated,
    ProfileDeactivated,
    OnDemandMatched,
    TunnelError,
    RouterOSProfileCreated,
    OnDemandUnmatched,
}

public sealed record WireRouteActivityEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    WireRouteActivityKind Kind,
    Guid? ProfileId,
    string? ProfileName,
    string Message);

[SupportedOSPlatform("windows")]
public sealed class WireRouteActivityStore
{
    private const int MaximumEntries = 1000;
    private readonly ProtectedJsonFile<List<WireRouteActivityEntry>> entries;

    public WireRouteActivityStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        entries = new ProtectedJsonFile<List<WireRouteActivityEntry>>(
            Path.Combine(directory, "activity.dpapi"),
            () => []);
    }

    public async Task<IReadOnlyList<WireRouteActivityEntry>> LoadAsync(
        Guid? profileId = null,
        CancellationToken cancellationToken = default)
    {
        var stored = await entries.ReadAsync(cancellationToken).ConfigureAwait(false);
        return stored
            .Where(entry => profileId is null || entry.ProfileId == profileId)
            .OrderByDescending(entry => entry.Timestamp)
            .ToArray();
    }

    public async Task AppendAsync(
        WireRouteActivityEntry entry,
        CancellationToken cancellationToken = default)
    {
        Validate(entry);
        _ = await entries.UpdateAsync(current =>
        {
            var updated = current.ToList();
            updated.Add(entry);
            return updated
                .OrderByDescending(value => value.Timestamp)
                .Take(MaximumEntries)
                .OrderBy(value => value.Timestamp)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(WireRouteActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty)
        {
            throw new ArgumentException("An activity identifier is required.", nameof(entry));
        }

        if (entry.Timestamp == default)
        {
            throw new ArgumentException("An activity timestamp is required.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            throw new ArgumentException("An activity message is required.", nameof(entry));
        }
    }
}
